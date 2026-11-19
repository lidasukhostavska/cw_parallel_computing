var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("ollama", client =>
{
    client.BaseAddress = new Uri("http://ollama:11434");
    client.Timeout = TimeSpan.FromSeconds(2);
});

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

app.Run();
