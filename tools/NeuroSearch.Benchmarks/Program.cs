using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace NeuroSearch.Benchmarks;

/// <summary>
/// Benchmarks Qdrant search (synthetic vectors) and end-to-end RAG latency (real Ollama embeds + Qdrant search).
///
/// Synthetic search:
///   dotnet run -- --count 10000 --queries 200 --dim 768
///
/// E2E RAG (embed + search):
///   dotnet run -- --e2e --queries 200 --dim 768
///   (collection is seeded with 10K vectors using real embeddings of the query strings; warmup=20)
///
/// Both modes print results as KEY=VALUE for easy grep/log.
/// </summary>
class Program
{
    // 30 realistic research-style query strings cycled over N iterations
    static readonly string[] QueryStrings =
    [
        "What are the latest advances in transformer neural networks?",
        "How does retrieval augmented generation improve LLM accuracy?",
        "Explain the HNSW algorithm for approximate nearest neighbour search",
        "What is the difference between cosine and dot product similarity?",
        "How do token bucket rate limiters work in distributed systems?",
        "What is SSRF and how do you prevent it in web applications?",
        "Explain SQL injection attack vectors and defenses",
        "What are the benefits of Native AOT compilation in .NET?",
        "How does Semantic Kernel enable autonomous AI agents?",
        "What is the role of embedding models in semantic search?",
        "Explain vector database indexing strategies",
        "How does Qdrant store and retrieve high-dimensional vectors?",
        "What is the ReAct pattern for LLM agents?",
        "How do function calling APIs work in large language models?",
        "What is the difference between gRPC and REST for microservices?",
        "How does C# async await differ from Task.Run?",
        "What are OWASP Top 10 web application security risks?",
        "Explain cross-site scripting (XSS) attack mitigation",
        "What is Docker multi-stage build and why use it?",
        "How does Kubernetes autoscaling respond to traffic spikes?",
        "What is the difference between p50, p95, and p99 latency percentiles?",
        "How do you measure cold start time for serverless functions?",
        "What are the tradeoffs of local LLM inference vs cloud APIs?",
        "Explain memory-mapped files and their use in databases",
        "What is ConcurrentDictionary and when should you use it?",
        "How does garbage collection work in the .NET runtime?",
        "What is the builder pattern and how does it improve testability?",
        "Explain dependency injection in ASP.NET Core",
        "What are the benefits of source generators in C#?",
        "How does the Ollama API handle concurrent inference requests?"
    ];

    static async Task<int> Main(string[] args)
    {
        bool e2eMode = args.Contains("--e2e");

        if (e2eMode)
            return await RunE2EBenchmark(args);
        else
            return await RunSearchBenchmark(args);
    }

    // ──────────────────────────────────────────────────────────────
    // Synthetic search benchmark (random unit vectors — original)
    // ──────────────────────────────────────────────────────────────
    static async Task<int> RunSearchBenchmark(string[] args)
    {
        var count   = GetInt(args, "--count", 10_000);
        var queries = GetInt(args, "--queries", 200);
        var dim     = GetInt(args, "--dim", 768);
        var host    = GetString(args, "--host", "localhost");
        var port    = GetInt(args, "--port", 6334);
        var collection = GetString(args, "--collection", "neurosearch-bench");

        Console.WriteLine("=== NeuroSearch Qdrant Synthetic Search Benchmark ===");
        Console.WriteLine($"Host={host}:{port} Collection={collection}");
        Console.WriteLine($"Vectors={count} Dim={dim} Queries={queries}");
        Console.WriteLine($"Machine={Environment.MachineName} OS={Environment.OSVersion}");
        Console.WriteLine($"UTC={DateTime.UtcNow:O}");
        Console.WriteLine();

        var client = new QdrantClient(host, port);
        await EnsureFreshCollectionAsync(client, collection, dim);

        var rng = new Random(42);
        Console.WriteLine($"Upserting {count} synthetic vectors in batches of 256...");
        var upsertSw = Stopwatch.StartNew();
        for (var offset = 0; offset < count; offset += 256)
        {
            var batchSize = Math.Min(256, count - offset);
            var points = new PointStruct[batchSize];
            for (var i = 0; i < batchSize; i++)
            {
                var id = (ulong)(offset + i + 1);
                points[i] = new PointStruct
                {
                    Id = id,
                    Vectors = RandomUnitVector(rng, dim),
                    Payload = { ["text"] = $"doc-{id}" }
                };
            }
            await client.UpsertAsync(collection, points);
            if ((offset / 256) % 10 == 0)
                Console.WriteLine($"  upserted {offset + batchSize}/{count}");
        }
        upsertSw.Stop();
        Console.WriteLine($"Upsert complete in {upsertSw.Elapsed.TotalSeconds:F2}s");

        var info = await client.GetCollectionInfoAsync(collection);
        Console.WriteLine($"Collection points={info.PointsCount} status={info.Status}");

        Console.WriteLine("Warmup (20 searches)...");
        for (var i = 0; i < 20; i++)
            await client.SearchAsync(collection, RandomUnitVector(rng, dim), limit: 5);

        Console.WriteLine($"Measuring {queries} searches...");
        var latenciesMs = new List<double>(queries);
        for (var i = 0; i < queries; i++)
        {
            var sw = Stopwatch.StartNew();
            await client.SearchAsync(collection, RandomUnitVector(rng, dim), limit: 5);
            sw.Stop();
            latenciesMs.Add(sw.Elapsed.TotalMilliseconds);
        }

        latenciesMs.Sort();
        PrintSearchResults(latenciesMs, count, upsertSw.Elapsed.TotalSeconds);
        return 0;
    }

    // ──────────────────────────────────────────────────────────────
    // E2E RAG benchmark: real Ollama embeddings + Qdrant search
    // ──────────────────────────────────────────────────────────────
    static async Task<int> RunE2EBenchmark(string[] args)
    {
        var queries     = GetInt(args, "--queries", 200);
        var warmup      = GetInt(args, "--warmup", 20);
        var dim         = GetInt(args, "--dim", 768);
        var host        = GetString(args, "--host", "localhost");
        var port        = GetInt(args, "--port", 6334);
        var ollamaUrl   = GetString(args, "--ollama", "http://localhost:11434");
        var model       = GetString(args, "--model", "nomic-embed-text");
        var collection  = GetString(args, "--collection", "neurosearch-e2e-bench");
        var seedCount   = GetInt(args, "--seed-count", 500);

        Console.WriteLine("=== NeuroSearch E2E RAG Latency Benchmark ===");
        Console.WriteLine($"Mode=embed+search (real Ollama embeddings)");
        Console.WriteLine($"OllamaEndpoint={ollamaUrl} EmbedModel={model}");
        Console.WriteLine($"Qdrant={host}:{port} Collection={collection}");
        Console.WriteLine($"SeedVectors={seedCount} Warmup={warmup} Queries={queries}");
        Console.WriteLine($"Machine={Environment.MachineName} OS={Environment.OSVersion}");
        Console.WriteLine($"UTC={DateTime.UtcNow:O}");
        Console.WriteLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var client = new QdrantClient(host, port);

        // Verify Ollama is reachable and model is loaded
        Console.WriteLine("Checking Ollama connectivity...");
        try
        {
            var testVec = await EmbedAsync(http, ollamaUrl, model, "connectivity check");
            Console.WriteLine($"Ollama OK: got {testVec.Length}-dim vector. Model is WARM.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BLOCKER: Ollama unreachable or model not loaded: {ex.Message}");
            Console.WriteLine("E2E_BENCHMARK_RESULT=BLOCKED");
            return 1;
        }

        // Seed collection with real embeddings of query strings
        Console.WriteLine($"Seeding collection with {seedCount} real-embedded docs...");
        await EnsureFreshCollectionAsync(client, collection, dim);
        var upsertSw = Stopwatch.StartNew();
        for (var i = 0; i < seedCount; i++)
        {
            var text = QueryStrings[i % QueryStrings.Length] + $" (doc-{i})";
            var vec = await EmbedAsync(http, ollamaUrl, model, text);
            var point = new PointStruct
            {
                Id = (ulong)(i + 1),
                Vectors = vec,
                Payload = { ["text"] = text }
            };
            await client.UpsertAsync(collection, new[] { point });
            if (i % 50 == 0) Console.WriteLine($"  seeded {i}/{seedCount}");
        }
        upsertSw.Stop();
        Console.WriteLine($"Seed upsert complete in {upsertSw.Elapsed.TotalSeconds:F2}s");

        var info = await client.GetCollectionInfoAsync(collection);
        Console.WriteLine($"Collection points={info.PointsCount} status={info.Status}");

        // Lists to record each phase latency
        var embedMs  = new List<double>(queries + warmup);
        var searchMs = new List<double>(queries + warmup);
        var e2eMs    = new List<double>(queries + warmup);

        int totalRuns = warmup + queries;
        Console.WriteLine($"Running {warmup} warmup + {queries} measured iterations...");

        for (var i = 0; i < totalRuns; i++)
        {
            var queryText = QueryStrings[i % QueryStrings.Length];

            // Measure embed
            var embedSw = Stopwatch.StartNew();
            var vec = await EmbedAsync(http, ollamaUrl, model, queryText);
            embedSw.Stop();

            // Measure search
            var searchSw = Stopwatch.StartNew();
            await client.SearchAsync(collection, vec, limit: 5);
            searchSw.Stop();

            if (i >= warmup)
            {
                embedMs.Add(embedSw.Elapsed.TotalMilliseconds);
                searchMs.Add(searchSw.Elapsed.TotalMilliseconds);
                e2eMs.Add(embedSw.Elapsed.TotalMilliseconds + searchSw.Elapsed.TotalMilliseconds);
            }

            if (i < warmup && i % 5 == 0)
                Console.WriteLine($"  warmup {i}/{warmup}");
            else if (i == warmup)
                Console.WriteLine($"  warmup complete, measuring...");
        }

        // Sort and compute stats
        embedMs.Sort();
        searchMs.Sort();
        e2eMs.Sort();

        Console.WriteLine();
        Console.WriteLine("=== E2E RAG RESULTS ===");
        Console.WriteLine($"E2E_MODE=embed+search");
        Console.WriteLine($"E2E_OLLAMA_MODEL={model}");
        Console.WriteLine($"E2E_MODEL_STATE=warm (pre-checked above)");
        Console.WriteLine($"E2E_QUERY_COUNT={queries}");
        Console.WriteLine($"E2E_WARMUP_EXCLUDED={warmup}");
        Console.WriteLine($"E2E_SEED_VECTORS={seedCount}");
        Console.WriteLine($"SEED_UPSERT_SECONDS={upsertSw.Elapsed.TotalSeconds:F2}");
        Console.WriteLine();
        PrintPhase("EMBED", embedMs);
        Console.WriteLine();
        PrintPhase("SEARCH", searchMs);
        Console.WriteLine();
        PrintPhase("E2E", e2eMs);
        Console.WriteLine("=== END ===");
        return 0;
    }

    static void PrintPhase(string label, List<double> sorted)
    {
        Console.WriteLine($"{label}_AVG_MS={sorted.Average():F3}");
        Console.WriteLine($"{label}_MIN_MS={sorted.First():F3}");
        Console.WriteLine($"{label}_MAX_MS={sorted.Last():F3}");
        Console.WriteLine($"{label}_P50_MS={Percentile(sorted, 0.50):F3}");
        Console.WriteLine($"{label}_P95_MS={Percentile(sorted, 0.95):F3}");
        Console.WriteLine($"{label}_P99_MS={Percentile(sorted, 0.99):F3}");
    }

    static void PrintSearchResults(List<double> sorted, int count, double upsertSec)
    {
        Console.WriteLine("=== RESULTS ===");
        Console.WriteLine($"VECTOR_COUNT={count}");
        Console.WriteLine($"QUERY_COUNT={sorted.Count}");
        Console.WriteLine($"SEARCH_AVG_MS={sorted.Average():F3}");
        Console.WriteLine($"SEARCH_MIN_MS={sorted.First():F3}");
        Console.WriteLine($"SEARCH_MAX_MS={sorted.Last():F3}");
        Console.WriteLine($"SEARCH_P50_MS={Percentile(sorted, 0.50):F3}");
        Console.WriteLine($"SEARCH_P95_MS={Percentile(sorted, 0.95):F3}");
        Console.WriteLine($"SEARCH_P99_MS={Percentile(sorted, 0.99):F3}");
        Console.WriteLine($"UPSERT_SECONDS={upsertSec:F2}");
        Console.WriteLine("=== END ===");
    }

    static async Task EnsureFreshCollectionAsync(QdrantClient client, string collection, int dim)
    {
        if (await client.CollectionExistsAsync(collection))
        {
            Console.WriteLine($"Deleting existing collection '{collection}'...");
            await client.DeleteCollectionAsync(collection);
        }
        Console.WriteLine($"Creating collection (Cosine, HNSW defaults, dim={dim})...");
        await client.CreateCollectionAsync(
            collection,
            new VectorParams { Size = (ulong)dim, Distance = Distance.Cosine, OnDisk = false });
    }

    static async Task<float[]> EmbedAsync(HttpClient http, string ollamaUrl, string model, string text)
    {
        using var response = await http.PostAsJsonAsync(
            $"{ollamaUrl.TrimEnd('/')}/api/embeddings",
            new { model, prompt = text });

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        if (!doc.RootElement.TryGetProperty("embedding", out var embEl))
            throw new InvalidOperationException("Ollama response missing 'embedding' field");

        var v = new float[embEl.GetArrayLength()];
        var idx = 0;
        foreach (var el in embEl.EnumerateArray())
            v[idx++] = el.GetSingle();
        return v;
    }

    static float[] RandomUnitVector(Random rng, int dim)
    {
        var v = new float[dim];
        double sumSq = 0;
        for (var i = 0; i < dim; i++) { var x = (float)(rng.NextDouble() * 2 - 1); v[i] = x; sumSq += x * x; }
        var norm = Math.Sqrt(sumSq);
        if (norm > 0) for (var i = 0; i < dim; i++) v[i] = (float)(v[i] / norm);
        return v;
    }

    static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var idx = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    static int GetInt(string[] args, string name, int fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
    }

    static string GetString(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }
}
