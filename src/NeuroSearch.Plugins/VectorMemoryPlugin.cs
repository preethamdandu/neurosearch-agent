using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Qdrant;
using Microsoft.SemanticKernel.Memory;
using System.ComponentModel;

namespace NeuroSearch.Plugins;

/// <summary>
/// Vector memory plugin for long-term semantic storage using Qdrant
/// Enables RAG (Retrieval Augmented Generation) capabilities
/// </summary>
public class VectorMemoryPlugin
{
    private readonly ISemanticTextMemory _memory;
    private readonly string _collectionName;

    public VectorMemoryPlugin(ISemanticTextMemory memory, string collectionName = "neurosearch-memory")
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _collectionName = collectionName;
    }

    [KernelFunction]
    [Description("Saves important information to long-term memory for future retrieval. Use this to remember facts, insights, or context for later use.")]
    public async Task<string> SaveToMemoryAsync(
        [Description("The information to save (e.g., 'Tesla announced Optimus Gen 3 robot in March 2026')")]
        string text,
        [Description("Optional metadata or category tags (e.g., 'Tesla, Robotics, 2026')")]
        string? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Error: Cannot save empty text to memory";

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"[💾 MemoryPlugin] Saving to memory: {text.Substring(0, Math.Min(60, text.Length))}...");
        Console.ResetColor();

        try
        {
            var id = Guid.NewGuid().ToString();

            await _memory.SaveInformationAsync(
                collection: _collectionName,
                text: text,
                id: id,
                description: metadata,
                cancellationToken: cancellationToken);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[✓ MemoryPlugin] Saved with ID: {id.Substring(0, 8)}...");
            Console.ResetColor();

            return $"Successfully saved to memory. ID: {id}";
        }
        catch (Exception ex)
        {
            return $"Failed to save to memory: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Searches long-term memory for relevant information based on semantic similarity. Use this to recall previously saved information.")]
    public async Task<string> SearchMemoryAsync(
        [Description("The query to search for in memory (e.g., 'What did we learn about Tesla robots?')")]
        string query,
        [Description("Number of results to return (default: 3, max: 10)")]
        int limit = 3,
        [Description("Minimum relevance score (0.0 to 1.0, default: 0.7). Higher means more strict.")]
        double minRelevance = 0.7,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Error: Search query cannot be empty";

        limit = Math.Clamp(limit, 1, 10);
        minRelevance = Math.Clamp(minRelevance, 0.0, 1.0);

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"[🧠 MemoryPlugin] Searching memory for: '{query}'");
        Console.ResetColor();

        try
        {
            var results = _memory.SearchAsync(
                collection: _collectionName,
                query: query,
                limit: limit,
                minRelevanceScore: minRelevance,
                cancellationToken: cancellationToken);

            var memories = new List<string>();
            await foreach (var result in results)
            {
                memories.Add($"[Relevance: {result.Relevance:F2}] {result.Metadata.Text}");
            }

            if (memories.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[⚠ MemoryPlugin] No relevant memories found");
                Console.ResetColor();
                return "No relevant memories found. Try lowering the minRelevance threshold or rephrasing your query.";
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[✓ MemoryPlugin] Found {memories.Count} relevant memories");
            Console.ResetColor();

            return $"Found {memories.Count} relevant memories:\n\n{string.Join("\n\n", memories)}";
        }
        catch (Exception ex)
        {
            return $"Memory search failed: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Retrieves the total number of items currently stored in memory. Useful for checking memory status.")]
    public async Task<string> GetMemoryStatsAsync(CancellationToken cancellationToken = default)
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"[📊 MemoryPlugin] Fetching memory statistics...");
        Console.ResetColor();

        try
        {
            // Note: This is a simplified version
            // In production, you'd use Qdrant's collection info API
            return "Memory statistics available via Qdrant dashboard at http://localhost:6333/dashboard";
        }
        catch (Exception ex)
        {
            return $"Failed to retrieve stats: {ex.Message}";
        }
    }
}
