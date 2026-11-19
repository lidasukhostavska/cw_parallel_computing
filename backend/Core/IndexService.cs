using System.Diagnostics;

namespace Backend.Core;

public sealed class IndexResult
{
    public IndexResult(int documentsIndexed, long elapsedMilliseconds, int threadsUsed)
    {
        DocumentsIndexed = documentsIndexed;
        ElapsedMilliseconds = elapsedMilliseconds;
        ThreadsUsed = threadsUsed;
    }

    public int DocumentsIndexed { get; }
    public long ElapsedMilliseconds { get; }
    public int ThreadsUsed { get; }
}

public sealed class IndexService
{
    private readonly ThreadSafeInvertedIndex _index;
    private readonly IndexingState _state;
    private readonly object _runLock = new object();
    private bool _isIndexing;

    public IndexService(
        ThreadSafeInvertedIndex index,
        IndexingState state)
    {
        _index = index;
        _state = state;
    }

    public bool IsIndexing
    {
        get
        {
            lock (_runLock)
                return _isIndexing;
        }
    }

    public Task<IndexResult?> RunAsync(int threads)
    {
        if (threads <= 0)
            threads = 4;

        lock (_runLock)
        {
            if (_isIndexing)
                return Task.FromResult<IndexResult?>(null);
            _isIndexing = true;
        }

        return Task.Run(() =>
        {
            try
            {
                return (IndexResult?)ExecuteIndexing(threads);
            }
            finally
            {
                lock (_runLock)
                    _isIndexing = false;
            }
        });
    }

    private IndexResult ExecuteIndexing(int threads)
    {
        _index.Clear();

        var dataDirectory = Environment.GetEnvironmentVariable("DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDirectory))
            dataDirectory = "/app/data";

        var stopwatch = Stopwatch.StartNew();
        var files = Directory.Exists(dataDirectory)
            ? Directory.GetFiles(dataDirectory, "*.txt", SearchOption.AllDirectories)
            : Array.Empty<string>();

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
                        _index.IndexDocument(relativePath, content);
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
        var documentsIndexed = _index.DocumentCount;
        _state.Update(documentsIndexed, elapsedMilliseconds, threads);

        return new IndexResult(
            documentsIndexed,
            elapsedMilliseconds,
            threads);
    }
}
