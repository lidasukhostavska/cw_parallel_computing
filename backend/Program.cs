using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;
using Backend.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("ollama", client =>
{
    client.BaseAddress = new Uri("http://ollama:11434");
    client.Timeout = TimeSpan.FromSeconds(2);
});
builder.Services.AddSingleton<ThreadSafeInvertedIndex>();
builder.Services.AddSingleton<IndexingState>();
builder.Services.AddSingleton<IndexService>();
builder.Services.AddHttpClient("ollama-expander", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<OllamaQueryExpander>(provider =>
{
    var factory = provider.GetRequiredService<IHttpClientFactory>();
    return new OllamaQueryExpander(factory.CreateClient("ollama-expander"));
});

var app = builder.Build();

app.UseWebSockets();

var frontendDirectory = "/app/wwwroot";
if (!Directory.Exists(frontendDirectory))
{
    var candidates = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), "frontend"),
        Path.Combine(builder.Environment.ContentRootPath, "..", "frontend"),
        Path.Combine(builder.Environment.ContentRootPath, "frontend")
    };

    frontendDirectory = string.Empty;
    for (var i = 0; i < candidates.Length; i++)
    {
        var candidate = Path.GetFullPath(candidates[i]);
        if (Directory.Exists(candidate))
        {
            frontendDirectory = candidate;
            break;
        }
    }
}

if (!string.IsNullOrEmpty(frontendDirectory) && Directory.Exists(frontendDirectory))
{
    var frontendProvider = new PhysicalFileProvider(frontendDirectory);
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = frontendProvider,
        RequestPath = ""
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = frontendProvider,
        RequestPath = ""
    });
}

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
    IndexService indexService) =>
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

    var result = await indexService.RunAsync(threads);
    if (result == null)
        return Results.Json(new { status = "busy" }, statusCode: StatusCodes.Status409Conflict);

    return Results.Json(new
    {
        status = "ok",
        documents_indexed = result.DocumentsIndexed,
        time_ms = result.ElapsedMilliseconds
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

app.Map("/ws/search", async (
    HttpContext httpContext,
    ThreadSafeInvertedIndex index,
    OllamaQueryExpander expander) =>
{
    if (!httpContext.WebSockets.IsWebSocketRequest)
    {
        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();

    try
    {
        using var message = new MemoryStream();
        var buffer = new byte[4096];

        WebSocketReceiveResult receiveResult;
        do
        {
            receiveResult = await webSocket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                httpContext.RequestAborted);

            if (receiveResult.MessageType == WebSocketMessageType.Close)
            {
                await webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Closing",
                    httpContext.RequestAborted);
                return;
            }

            await message.WriteAsync(
                buffer.AsMemory(0, receiveResult.Count),
                httpContext.RequestAborted);
        }
        while (!receiveResult.EndOfMessage);

        var request = JsonSerializer.Deserialize<SearchRequest>(
            message.ToArray(),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

        var query = request?.Query?.Trim() ?? string.Empty;
        var expandedTerms = query.Length == 0
            ? Array.Empty<string>()
            : await expander.ExpandAsync(query);

        await SendJsonAsync(webSocket, new
        {
            stage = "query_expansion",
            data = new
            {
                original = query,
                expanded_terms = expandedTerms
            }
        }, httpContext.RequestAborted);

        var stopwatch = Stopwatch.StartNew();
        var matches = index.SearchAny(expandedTerms, 20);
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

        await SendJsonAsync(webSocket, new
        {
            stage = "keyword_search",
            data = new
            {
                results,
                time_ms = stopwatch.ElapsedMilliseconds
            }
        }, httpContext.RequestAborted);

        await SendJsonAsync(webSocket, new
        {
            stage = "done"
        }, httpContext.RequestAborted);

        await webSocket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "Search completed",
            httpContext.RequestAborted);
    }
    catch (WebSocketException)
    {
        if (webSocket.State == WebSocketState.Open ||
            webSocket.State == WebSocketState.CloseReceived)
        {
            await webSocket.CloseAsync(
                WebSocketCloseStatus.InternalServerError,
                "WebSocket error",
                CancellationToken.None);
        }
    }
});

app.MapPost("/api/search", async (
    HttpContext httpContext,
    ThreadSafeInvertedIndex index,
    OllamaQueryExpander expander) =>
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

    var mode = request.Mode?.Trim().ToLowerInvariant();
    var useExpandedSearch =
        mode == "expanded" ||
        mode == "semantic" ||
        mode == "llm";

    var searchTerms = useExpandedSearch
        ? await expander.ExpandAsync(request.Query)
        : tokens;

    var matches = index.SearchAny(searchTerms, 20);
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

static async Task SendJsonAsync(
    WebSocket webSocket,
    object message,
    CancellationToken cancellationToken)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
    await webSocket.SendAsync(
        new ArraySegment<byte>(bytes),
        WebSocketMessageType.Text,
        true,
        cancellationToken);
}

app.Lifetime.ApplicationStarted.Register(() =>
{
    var index = app.Services.GetRequiredService<ThreadSafeInvertedIndex>();
    if (index.DocumentCount != 0)
        return;

    var dataDirectory = Environment.GetEnvironmentVariable("DATA_DIR");
    if (string.IsNullOrWhiteSpace(dataDirectory))
        dataDirectory = "/app/data";

    if (!Directory.Exists(dataDirectory))
        return;

    var txtFiles = Directory.GetFiles(
        dataDirectory,
        "*.txt",
        SearchOption.AllDirectories);
    if (txtFiles.Length == 0)
        return;

    var threads = 4;
    var configuredThreads = Environment.GetEnvironmentVariable("DEFAULT_INDEX_THREADS");
    if (!string.IsNullOrWhiteSpace(configuredThreads) &&
        int.TryParse(configuredThreads, out var parsedThreads) &&
        parsedThreads > 0)
    {
        threads = parsedThreads;
    }

    var indexService = app.Services.GetRequiredService<IndexService>();
    _ = Task.Run(async () =>
    {
        await indexService.RunAsync(threads);
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
