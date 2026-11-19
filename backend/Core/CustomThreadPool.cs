namespace Backend.Core;

public sealed class CustomThreadPool : IDisposable
{
    private readonly Thread[] _workers;
    private Action[] _workItems;
    private int _head;
    private int _tail;
    private int _count;
    private int _activeTaskCount;
    private bool _disposed;
    private readonly object _lock = new object();

    public CustomThreadPool(int workerCount, int initialQueueCapacity = 256)
    {
        if (workerCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(workerCount));
        if (initialQueueCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialQueueCapacity));

        _workItems = new Action[initialQueueCapacity];
        _workers = new Thread[workerCount];

        for (var i = 0; i < workerCount; i++)
        {
            _workers[i] = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"CustomWorker-{i}"
            };
            _workers[i].Start();
        }
    }

    public void Enqueue(Action workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CustomThreadPool));

            if (_count == _workItems.Length)
                GrowQueue();

            _workItems[_tail] = workItem;
            _tail = (_tail + 1) % _workItems.Length;
            _count++;
            _activeTaskCount++;
            Monitor.PulseAll(_lock);
        }
    }

    public void Submit(Action workItem) => Enqueue(workItem);
    public void Queue(Action workItem) => Enqueue(workItem);
    public void QueueWorkItem(Action workItem) => Enqueue(workItem);

    public void WaitAll()
    {
        lock (_lock)
        {
            while (_activeTaskCount != 0)
                Monitor.Wait(_lock);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
            Monitor.PulseAll(_lock);
        }

        for (var i = 0; i < _workers.Length; i++)
            _workers[i].Join();
    }

    private void WorkerLoop()
    {
        while (true)
        {
            Action? workItem;

            lock (_lock)
            {
                while (_count == 0)
                {
                    if (_disposed)
                        return;
                    Monitor.Wait(_lock);
                }

                workItem = _workItems[_head];
                _workItems[_head] = null!;
                _head = (_head + 1) % _workItems.Length;
                _count--;
            }

            try
            {
                workItem();
            }
            finally
            {
                lock (_lock)
                {
                    _activeTaskCount--;
                    if (_activeTaskCount == 0)
                        Monitor.PulseAll(_lock);
                }
            }
        }
    }

    private void GrowQueue()
    {
        var newCapacity = checked(_workItems.Length * 2);
        var resized = new Action[newCapacity];

        for (var i = 0; i < _count; i++)
            resized[i] = _workItems[(_head + i) % _workItems.Length];

        _workItems = resized;
        _head = 0;
        _tail = _count;
    }
}
