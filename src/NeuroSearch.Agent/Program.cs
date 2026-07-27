using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.SemanticKernel.ChatCompletion;
using NeuroSearch.Plugins;
using Qdrant.Client;

namespace NeuroSearch.Agent;

class Program
{
    static async Task Main(string[] args)
    {
        var startupSw = System.Diagnostics.Stopwatch.StartNew();

        // Display banner
        DisplayBanner();

        // Load configuration (use app base dir so published binaries work from any cwd)
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        // Setup builder with logging
        var builder = Kernel.CreateBuilder();
        
        builder.Services.AddLogging(c => 
        {
            c.AddConsole();
            c.SetMinimumLevel(LogLevel.Information);
        });

        // Configure Ollama
        var ollamaEndpoint = configuration["Ollama:Endpoint"] ?? "http://localhost:11434";
        var chatModel = configuration["Ollama:ChatModel"] ?? "llama3:8b";
        var embeddingModel = configuration["Ollama:EmbeddingModel"] ?? "nomic-embed-text";

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Connecting to Ollama at {ollamaEndpoint} with model {chatModel}");
        Console.ResetColor();

        try 
        {
            builder.AddOllamaChatCompletion(
                modelId: chatModel,
                endpoint: new Uri(ollamaEndpoint));
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[CRITICAL] Ollama connection failed: {ex.Message}");
            Console.WriteLine("Make sure Ollama is running: 'ollama serve'");
            Console.ResetColor();
            return;
        }

        // Setup HTTP services
        var services = new ServiceCollection();
        services.AddHttpClient();
        var serviceProvider = services.BuildServiceProvider();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

        // Register plugins
        var serperApiKey = Environment.GetEnvironmentVariable("SERPER_API_KEY") ?? "demo-key";
        
        if (serperApiKey == "demo-key")
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("SERPER_API_KEY not set! Using demo mode.");
            Console.ResetColor();
        }

        var searchPlugin = new WebSearchPlugin(httpClientFactory.CreateClient(), serperApiKey);
        var scraperPlugin = new WebScraperPlugin(httpClientFactory.CreateClient());

        // Force AOT/trimmer to keep KernelFunction-annotated members on plugins
        PreservePluginSurface(searchPlugin);
        PreservePluginSurface(scraperPlugin);

        builder.Plugins.AddFromObject(searchPlugin, "WebSearch");
        builder.Plugins.AddFromObject(scraperPlugin, "WebScraper");

        // Vector memory (Qdrant + Ollama embeddings) — optional if Qdrant is down
        try
        {
            var qdrantHost = configuration["Qdrant:Host"] ?? "localhost";
            var qdrantPort = int.Parse(configuration["Qdrant:GrpcPort"] ?? "6334");
            var collection = configuration["Qdrant:CollectionName"] ?? "neurosearch-memory";
            var vectorSize = ulong.Parse(configuration["Qdrant:VectorSize"] ?? "768");

            var qdrant = new QdrantClient(qdrantHost, qdrantPort);
            var memoryPlugin = new VectorMemoryPlugin(
                qdrant,
                httpClientFactory.CreateClient(),
                ollamaEndpoint,
                embeddingModel,
                collection,
                vectorSize);

            builder.Plugins.AddFromObject(memoryPlugin, "VectorMemory");
            PreservePluginSurface(memoryPlugin);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"VectorMemory plugin enabled (Qdrant {qdrantHost}:{qdrantPort}, model={embeddingModel})");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"VectorMemory disabled: {ex.Message}");
            Console.ResetColor();
        }

        var kernel = builder.Build();

        startupSw.Stop();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Kernel built with {kernel.Plugins.Count} plugins");
        Console.WriteLine($"Security hardening: ENABLED");
        Console.WriteLine($"STARTUP_MS={startupSw.ElapsedMilliseconds}");
        Console.WriteLine($"READY");
        Console.ResetColor();

        // Benchmark-only mode: measure cold start then exit
        if (args.Contains("--startup-benchmark", StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        // Non-interactive smoke test: one prompt, then exit (for CI / verification)
        var smokeIdx = Array.FindIndex(args, a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase));
        if (smokeIdx >= 0)
        {
            var prompt = smokeIdx + 1 < args.Length
                ? args[smokeIdx + 1]
                : "Reply with exactly: SMOKE_OK";
            await RunSmokeTestAsync(kernel, prompt);
            return;
        }

        // Main agent loop
        await RunAgentLoopAsync(kernel);
    }

    [System.Diagnostics.CodeAnalysis.DynamicDependency(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors,
        typeof(WebSearchPlugin))]
    [System.Diagnostics.CodeAnalysis.DynamicDependency(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors,
        typeof(WebScraperPlugin))]
    [System.Diagnostics.CodeAnalysis.DynamicDependency(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors,
        typeof(VectorMemoryPlugin))]
    static void PreservePluginSurface(object plugin)
    {
        // No-op: attributes above keep plugin methods for Semantic Kernel reflection under AOT.
        _ = plugin.GetType().GetMethods();
    }

    static async Task RunSmokeTestAsync(Kernel kernel, string prompt)
    {
        var chatHistory = new ChatHistory();
        var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();
        var executionSettings = new OllamaPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: true)
        };

        Console.WriteLine($"SMOKE_PROMPT={prompt}");
        chatHistory.AddUserMessage(prompt);

        try
        {
            var result = await chatCompletionService.GetChatMessageContentAsync(
                chatHistory,
                executionSettings: executionSettings,
                kernel: kernel);

            Console.WriteLine($"SMOKE_RESPONSE={result.Content}");
            Console.WriteLine("SMOKE_PASS");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"SMOKE_FAIL={ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
                Console.WriteLine($"SMOKE_INNER={ex.InnerException.Message}");
            Console.ResetColor();
            Environment.Exit(1);
        }
    }

    static async Task RunAgentLoopAsync(Kernel kernel)
    {
        var chatHistory = new ChatHistory();
        var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();

        // Enable Auto-Function Calling (The Agentic Behavior)
        var executionSettings = new OllamaPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: true)
        };

        Console.WriteLine("Ready! Type your research query (or 'exit' to quit):\n");

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("You > ");
            Console.ResetColor();

            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input) || input.ToLower() == "exit")
            {
                Console.WriteLine("\nGoodbye!\n");
                break;
            }

            chatHistory.AddUserMessage(input);

            try 
            {
                Console.Write("\n[Agent Thinking");
                
                var startTime = DateTime.Now;
                
                // The magic: Agent decides what tools to use
                var result = await chatCompletionService.GetChatMessageContentAsync(
                    chatHistory,
                    executionSettings: executionSettings,
                    kernel: kernel);

                var elapsed = DateTime.Now - startTime;
                Console.Write("]\n\n");

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                Console.WriteLine("Agent Response:");
                Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                Console.ResetColor();
                Console.WriteLine($"\n{result.Content}\n");

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[Completed in {elapsed.TotalSeconds:F2}s]");
                Console.ResetColor();
                Console.WriteLine();

                chatHistory.AddAssistantMessage(result.Content ?? string.Empty);
            }
            catch (HttpRequestException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[NETWORK FAILURE]: {ex.Message}");
                Console.WriteLine("The AI service is unreachable. Check Ollama status.");
                Console.ResetColor();
            }
            catch (TimeoutException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[TIMEOUT]: {ex.Message}");
                Console.WriteLine("The request took too long. Try a simpler query.");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[SYSTEM FAILURE]: {ex.Message}");
                Console.ResetColor();
                
                Console.ForegroundColor = ConsoleColor.Yellow;
                if (ex.InnerException != null)
                    Console.WriteLine($"Details: {ex.InnerException.Message}");
                Console.ResetColor();
            }
        }
    }

    static void DisplayBanner()
    {
        // Skip Clear in non-interactive / benchmark runs so redirected output stays clean
        if (!Console.IsOutputRedirected)
            Console.Clear();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════╗
║                                                               ║
║   NEUROSEARCH AGENT - Autonomous Research System              ║
║                                                               ║
╚═══════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("AI Model: Ollama (Local Inference)");
        Console.WriteLine("Plugins: WebSearch | WebScraper | VectorMemory");
        Console.WriteLine("Security: OWASP-hardened input validation + rate limiting");
        Console.WriteLine("Pattern: Auto function calling (agentic tool use)\n");
        Console.ResetColor();
    }
}
