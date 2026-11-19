using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("ollama", client =>
{
    client.BaseAddress = new Uri("http://ollama:11434");
    client.Timeout = TimeSpan.FromSeconds(2);
});
builder.Services.AddSingleton<ThreadSafeInvertedIndex>();
builder.Services.AddSingleton<IndexingState>();

var app = builder.Build();

app.MapGet("/api/health", async (IHttpClientFactory httpClientFactory) =>
{
    var ollamaReachable = false;

    try
    {
        using var httpClient = httpClientFactory.CreateClient("ollama");
        using var response = await httpClient.GetAsync("/api/tags");
        ollamaReachable = response.IsSuccessStatusCode;
    }
    catch (HttpRequestException)
    {
    }
    catch (TaskCanceledException)
    {
    }

    return Results.Json(new
    {
        status = "ok",
        ollama = ollamaReachable
    });
});

app.MapPost("/api/index", async (
    HttpContext httpContext,
    ThreadSafeInvertedIndex index,
    IndexingState indexingState) =>
{
    var threads = 4;

    if (httpContext.Request.ContentLength != 0)
    {
        try
        {
            var request = await httpContext.Request.ReadFromJsonAsync<IndexRequest>();
            if (request != null && request.Threads > 0)
                threads = request.Threads;
        }
        catch (JsonException)
        {
        }
    }

    index.Clear();

    var dataDirectory = Environment.GetEnvironmentVariable("DATA_DIR");
    if (string.IsNullOrWhiteSpace(dataDirectory))
        dataDirectory = "/app/data";

    var stopwatch = Stopwatch.StartNew();

    string[] files;
    if (Directory.Exists(dataDirectory))
        files = Directory.GetFiles(dataDirectory, "*.txt", SearchOption.AllDirectories);
    else
        files = Array.Empty<string>();

    using (var pool = new CustomThreadPool(threads))
    {
        for (var i = 0; i < files.Length; i++)
        {
            var filePath = files[i];
            pool.Enqueue(() =>
            {
                try
                {
                    var content = File.ReadAllText(filePath);
                    var relativePath = Path.GetRelativePath(dataDirectory, filePath)
                        .Replace('\\', '/');
                    index.IndexDocument(relativePath, content);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            });
        }

        pool.WaitAll();
    }

    stopwatch.Stop();
    var elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
    var documentsIndexed = index.DocumentCount;
    indexingState.Update(documentsIndexed, elapsedMilliseconds, threads);

    return Results.Json(new
    {
        status = "ok",
        documents_indexed = documentsIndexed,
        time_ms = elapsedMilliseconds
    });
});

app.MapGet("/api/stats", (IndexingState indexingState) =>
{
    indexingState.Get(out var documents, out var indexBuildTimeMs, out var threadsUsed);

    return Results.Json(new
    {
        documents,
        index_build_time_ms = indexBuildTimeMs,
        threads_used = threadsUsed
    });
});

app.MapPost("/api/search", async (
    HttpContext httpContext,
    ThreadSafeInvertedIndex index) =>
{
    SearchRequest? request;

    try
    {
        request = await httpContext.Request.ReadFromJsonAsync<SearchRequest>();
    }
    catch (JsonException)
    {
        request = null;
    }

    if (request == null || string.IsNullOrWhiteSpace(request.Query))
    {
        return Results.Json(new
        {
            results = Array.Empty<SearchApiResult>(),
            time_ms = 0L
        });
    }

    var stopwatch = Stopwatch.StartNew();
    var tokens = SplitSearchTokens(request.Query);
    var matches = index.SearchAny(tokens, 20);
    stopwatch.Stop();

    var maxScore = 0;
    for (var i = 0; i < matches.Length; i++)
    {
        if (matches[i].Score > maxScore)
            maxScore = matches[i].Score;
    }

    var results = new SearchApiResult[matches.Length];
    for (var i = 0; i < matches.Length; i++)
    {
        var normalizedScore = maxScore == 0
            ? 0.0
            : Math.Round((double)matches[i].Score / maxScore, 4);

        results[i] = new SearchApiResult(
            matches[i].DocId,
            normalizedScore,
            matches[i].Snippet);
    }

    return Results.Json(new
    {
        results,
        time_ms = stopwatch.ElapsedMilliseconds
    });
});

app.Run();

static string[] SplitSearchTokens(string query)
{
    var tokens = new string[query.Length];
    var tokenCount = 0;
    var start = 0;

    while (start < query.Length)
    {
        while (start < query.Length &&
               (char.IsWhiteSpace(query[start]) || char.IsPunctuation(query[start])))
            start++;

        var end = start;
        while (end < query.Length &&
               !char.IsWhiteSpace(query[end]) &&
               !char.IsPunctuation(query[end]))
            end++;

        if (end > start)
        {
            var token = query.Substring(start, end - start);
            if (!string.IsNullOrWhiteSpace(token))
                tokens[tokenCount++] = token;
        }

        start = end;
    }

    if (tokenCount == 0)
        return Array.Empty<string>();

    var resized = new string[tokenCount];
    for (var i = 0; i < tokenCount; i++)
        resized[i] = tokens[i];
    return resized;
}

public sealed class IndexRequest
{
    public int Threads { get; set; }
}

public sealed class SearchRequest
{
    public string? Query { get; set; }
    public string? Mode { get; set; }
}

public sealed class SearchApiResult
{
    [JsonPropertyName("doc_id")]
    public string DocId { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("snippet")]
    public string Snippet { get; set; }

    public SearchApiResult(string docId, double score, string snippet)
    {
        DocId = docId;
        Score = score;
        Snippet = snippet;
    }
}
