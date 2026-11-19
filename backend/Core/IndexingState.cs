namespace Backend.Core;

public sealed class IndexingState
{
    private readonly object _lock = new object();
    private int _documents;
    private long _indexBuildTimeMs;
    private int _threadsUsed;

    public void Update(int documents, long indexBuildTimeMs, int threadsUsed)
    {
        lock (_lock)
        {
            _documents = documents;
            _indexBuildTimeMs = indexBuildTimeMs;
            _threadsUsed = threadsUsed;
        }
    }

    public void Get(out int documents, out long indexBuildTimeMs, out int threadsUsed)
    {
        lock (_lock)
        {
            documents = _documents;
            indexBuildTimeMs = _indexBuildTimeMs;
            threadsUsed = _threadsUsed;
        }
    }
}
