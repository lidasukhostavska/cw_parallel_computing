using System.Diagnostics;
using System.Text.Json;
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

app.Run();

public sealed class IndexRequest
{
    public int Threads { get; set; }
}
