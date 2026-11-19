using System.Text;
using System.Text.Json;

namespace Backend.Core;

public sealed class OllamaQueryExpander : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
    private readonly Uri _generateUri;
    private readonly string _model;

    public OllamaQueryExpander(HttpClient httpClient)
    {
        _httpClient = httpClient;

        var configuredUrl = Environment.GetEnvironmentVariable("OLLAMA_URL");
        var baseUrl = string.IsNullOrWhiteSpace(configuredUrl)
            ? "http://ollama:11434"
            : configuredUrl;

        baseUrl = baseUrl.TrimEnd('/');
        _generateUri = new Uri(baseUrl + "/api/generate");

        var configuredModel = Environment.GetEnvironmentVariable("OLLAMA_MODEL");
        _model = string.IsNullOrWhiteSpace(configuredModel)
            ? "llama3.2:1b"
            : configuredModel;
    }

    public async Task<string[]> ExpandAsync(string query, CancellationToken cancellationToken = default)
    {
        var originalTerms = NormalizeAndSplit(query);
        if (originalTerms.Length == 0)
            return Array.Empty<string>();

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            var prompt =
                "List 4 single-word English synonyms or related search terms for: \"" +
                query +
                "\". Output ONLY the 4 words separated by commas, no intro text, no numbers, no explanations.";

            var payload = new
            {
                model = _model,
                prompt,
                stream = false
            };

            using var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using var response = await _httpClient
                .PostAsync(_generateUri, content, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return originalTerms;

            var json = await response.Content
                .ReadAsStringAsync(timeout.Token)
                .ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("response", out var responseElement))
                return originalTerms;

            var modelResponse = responseElement.GetString();
            return MergeUnique(originalTerms, modelResponse);
        }
        catch (HttpRequestException)
        {
            return originalTerms;
        }
        catch (TaskCanceledException)
        {
            return originalTerms;
        }
        catch (JsonException)
        {
            return originalTerms;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        _semaphore.Dispose();
    }

    private static string[] NormalizeAndSplit(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var tokens = new string[query.Length];
        var tokenCount = 0;
        var start = 0;

        while (start < query.Length)
        {
            while (start < query.Length &&
                   (char.IsWhiteSpace(query[start]) ||
                    char.IsPunctuation(query[start]) ||
                    char.IsSeparator(query[start])))
                start++;

            var end = start;
            while (end < query.Length &&
                   !char.IsWhiteSpace(query[end]) &&
                   !char.IsPunctuation(query[end]) &&
                   !char.IsSeparator(query[end]))
                end++;

            if (end > start)
            {
                var normalized = Normalize(query.Substring(start, end - start));
                if (normalized.Length != 0)
                {
                    var duplicate = false;
                    for (var i = 0; i < tokenCount; i++)
                    {
                        if (tokens[i] == normalized)
                        {
                            duplicate = true;
                            break;
                        }
                    }

                    if (!duplicate)
                        tokens[tokenCount++] = normalized;
                }
            }

            start = end;
        }

        var result = new string[tokenCount];
        for (var i = 0; i < tokenCount; i++)
            result[i] = tokens[i];
        return result;
    }

    private static string[] MergeUnique(string[] originalTerms, string? modelResponse)
    {
        var terms = new string[originalTerms.Length + 16];
        var termCount = 0;

        for (var i = 0; i < originalTerms.Length; i++)
            terms[termCount++] = originalTerms[i];

        if (!string.IsNullOrWhiteSpace(modelResponse))
        {
            var separators = new char[] { ',', ';', ' ', '\t', '\r', '\n' };
            var responseTerms = modelResponse.Split(
                separators,
                StringSplitOptions.RemoveEmptyEntries);

            for (var i = 0; i < responseTerms.Length && termCount < originalTerms.Length + 5; i++)
            {
                var normalized = Normalize(responseTerms[i]);
                if (normalized.Length == 0)
                    continue;
                if (int.TryParse(normalized, out _))
                    continue;
                if (IsPromptEchoStopWord(normalized))
                    continue;

                var duplicate = false;
                for (var j = 0; j < termCount; j++)
                {
                    if (terms[j] == normalized)
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                    terms[termCount++] = normalized;
            }
        }

        var result = new string[termCount];
        for (var i = 0; i < termCount; i++)
            result[i] = terms[i];
        return result;
    }

    private static string Normalize(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return string.Empty;

        var characters = token.ToCharArray();
        var length = 0;
        for (var i = 0; i < characters.Length; i++)
        {
            if (char.IsLetterOrDigit(characters[i]))
                characters[length++] = char.ToLowerInvariant(characters[i]);
        }

        return length == 0 ? string.Empty : new string(characters, 0, length);
    }

    private static bool IsPromptEchoStopWord(string word)
    {
        return word == "here" ||
               word == "are" ||
               word == "is" ||
               word == "the" ||
               word == "and" ||
               word == "for" ||
               word == "with" ||
               word == "relevant" ||
               word == "english" ||
               word == "keywords" ||
               word == "synonyms" ||
               word == "words" ||
               word == "search" ||
               word == "query" ||
               word == "list" ||
               word == "terms";
    }
}
