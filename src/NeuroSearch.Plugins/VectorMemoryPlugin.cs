using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.SemanticKernel;
using NeuroSearch.Core;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace NeuroSearch.Plugins;

/// <summary>
/// Vector memory plugin for long-term semantic storage using Qdrant + Ollama embeddings.
/// Persists provenance=untrusted when saves occur while untrusted content is in context.
/// </summary>
public class VectorMemoryPlugin
{
    private readonly QdrantClient _qdrant;
    private readonly HttpClient _http;
    private readonly string _collectionName;
    private readonly string _embeddingModel;
    private readonly string _ollamaEndpoint;
    private readonly ulong _vectorSize;
    private readonly ulong _hnswEf;
    private readonly string _quantization;
    private readonly InjectionSessionState _session;
    private bool _collectionReady;

    public VectorMemoryPlugin(
        QdrantClient qdrant,
        HttpClient http,
        string ollamaEndpoint,
        string embeddingModel = "nomic-embed-text",
        string collectionName = "neurosearch-memory",
        ulong vectorSize = 768,
        InjectionSessionState? session = null,
        ulong hnswEf = 16,
        string quantization = "none")
    {
        _qdrant = qdrant ?? throw new ArgumentNullException(nameof(qdrant));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _ollamaEndpoint = ollamaEndpoint.TrimEnd('/');
        _embeddingModel = embeddingModel;
        _collectionName = collectionName;
        _vectorSize = vectorSize;
        _session = session ?? new InjectionSessionState();
        _hnswEf = hnswEf == 0 ? 16 : hnswEf;
        _quantization = string.IsNullOrWhiteSpace(quantization) ? "none" : quantization.Trim().ToLowerInvariant();
    }

    public InjectionSessionState Session => _session;
    public ulong HnswEf => _hnswEf;
    public string QuantizationMode => _quantization;

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
        Console.WriteLine($"[MemoryPlugin] Saving: {text.Substring(0, Math.Min(60, text.Length))}...");
        Console.ResetColor();

        try
        {
            await EnsureCollectionAsync(cancellationToken);
            var vector = await EmbedAsync(text, cancellationToken);
            var id = (ulong)(DateTime.UtcNow.Ticks ^ Random.Shared.NextInt64());

            // Provenance: anything saved while untrusted content is in context is tagged forever
            var provenance = _session.HasUntrustedInContext
                ? ContentProvenance.Untrusted
                : ContentProvenance.Trusted;
            var originUrl = _session.HasUntrustedInContext
                ? (_session.UntrustedOrigins.LastOrDefault() ?? "unknown")
                : "user";
            var researchHop = _session.ResearchHopDepth;
            var originatingQuery = _session.UntrustedOrigins
                .LastOrDefault(o => o.Contains(":search:", StringComparison.OrdinalIgnoreCase))
                ?? originUrl;

            var point = new PointStruct
            {
                Id = id,
                Vectors = vector,
                Payload =
                {
                    ["text"] = text,
                    ["metadata"] = metadata ?? string.Empty,
                    ["created_at"] = DateTime.UtcNow.ToString("O"),
                    ["provenance"] = provenance.ToString().ToLowerInvariant(),
                    ["origin_url"] = originUrl,
                    ["research_hop"] = researchHop,
                    ["originating_query"] = originatingQuery
                }
            };

            await _qdrant.UpsertAsync(_collectionName, new[] { point }, cancellationToken: cancellationToken);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(
                $"[MemoryPlugin] Saved id={id} provenance={provenance} origin={originUrl} " +
                $"research_hop={researchHop}");
            Console.ResetColor();

            return $"Successfully saved to memory. ID: {id}; provenance={provenance.ToString().ToLowerInvariant()}; " +
                   $"origin_url={originUrl}; research_hop={researchHop}";
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
        [Description("Minimum relevance score (0.0 to 1.0, default: 0.5). Higher means more strict.")]
        double minRelevance = 0.5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Error: Search query cannot be empty";

        limit = Math.Clamp(limit, 1, 10);
        minRelevance = Math.Clamp(minRelevance, 0.0, 1.0);

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"[MemoryPlugin] Searching: '{query}'");
        Console.ResetColor();

        try
        {
            await EnsureCollectionAsync(cancellationToken);
            var vector = await EmbedAsync(query, cancellationToken);

            var results = await _qdrant.SearchAsync(
                _collectionName,
                vector,
                limit: (ulong)limit,
                scoreThreshold: (float)minRelevance,
                searchParams: BuildSearchParams(),
                cancellationToken: cancellationToken);

            if (results.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[MemoryPlugin] No relevant memories found");
                Console.ResetColor();
                return "No relevant memories found. Try lowering the minRelevance threshold or rephrasing your query.";
            }

            var memories = results.Select(r =>
            {
                var text = r.Payload.TryGetValue("text", out var t) ? t.StringValue : "";
                var provenance = r.Payload.TryGetValue("provenance", out var p) ? p.StringValue : "unknown";
                var origin = r.Payload.TryGetValue("origin_url", out var o) ? o.StringValue : "unknown";
                return $"[Relevance: {r.Score:F2}] [provenance={provenance}] [origin_url={origin}] {text}";
            }).ToList();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[MemoryPlugin] Found {memories.Count} relevant memories");
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
        try
        {
            await EnsureCollectionAsync(cancellationToken);
            var info = await _qdrant.GetCollectionInfoAsync(_collectionName, cancellationToken);
            return $"Collection '{_collectionName}': {info.PointsCount} points, status={info.Status}";
        }
        catch (Exception ex)
        {
            return $"Failed to retrieve stats: {ex.Message}";
        }
    }

    private async Task EnsureCollectionAsync(CancellationToken cancellationToken)
    {
        if (_collectionReady)
            return;

        var exists = await _qdrant.CollectionExistsAsync(_collectionName, cancellationToken);
        if (!exists)
        {
            QuantizationConfig? quant = _quantization switch
            {
                "scalar" or "int8" => new QuantizationConfig
                {
                    Scalar = new ScalarQuantization
                    {
                        Type = QuantizationType.Int8,
                        Quantile = 0.99f,
                        AlwaysRam = true
                    }
                },
                "binary" => new QuantizationConfig
                {
                    Binary = new BinaryQuantization { AlwaysRam = true }
                },
                _ => null
            };

            await _qdrant.CreateCollectionAsync(
                _collectionName,
                new VectorParams
                {
                    Size = _vectorSize,
                    Distance = Distance.Cosine,
                    OnDisk = false
                },
                quantizationConfig: quant,
                cancellationToken: cancellationToken);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(
                $"[MemoryPlugin] Created collection '{_collectionName}' " +
                $"hnsw_ef={_hnswEf} quantization={_quantization}");
            Console.ResetColor();
        }

        _collectionReady = true;
    }

    private SearchParams BuildSearchParams()
    {
        var sp = new SearchParams { HnswEf = _hnswEf };
        if (_quantization is "scalar" or "int8" or "binary")
        {
            sp.Quantization = new QuantizationSearchParams
            {
                Rescore = true,
                Oversampling = _quantization == "binary" ? 2.0 : 1.0
            };
        }
        return sp;
    }

    private async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(
            $"{_ollamaEndpoint}/api/embeddings",
            new { model = _embeddingModel, prompt = text },
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!doc.RootElement.TryGetProperty("embedding", out var embeddingElement))
            throw new InvalidOperationException("Ollama embeddings response missing 'embedding' field.");

        var vector = new float[embeddingElement.GetArrayLength()];
        var i = 0;
        foreach (var value in embeddingElement.EnumerateArray())
            vector[i++] = value.GetSingle();

        if ((ulong)vector.Length != _vectorSize)
            throw new InvalidOperationException($"Expected {_vectorSize}-dim embeddings, got {vector.Length}. Check EmbeddingModel/VectorSize config.");

        return vector;
    }
}
