namespace GseTechPro.Scope;

/// <summary>
/// The scope's waveform buffer (Phase 10D.1, PicoScope 7 workflow): keeps the newest captures so the technician
/// can step back through them. Bounded by a count (default 64, as PicoScope) and a memory budget, because the
/// target tablet has 8 GB and captures can be up to <see cref="WaveformFile.MaxTotalSamples"/> doubles each.
/// When either limit is exceeded the oldest captures are dropped. Not thread-safe: use it from the UI thread.
/// </summary>
public sealed class WaveformBuffer<T>
{
    public const int DefaultCapacity = 64;
    public const long DefaultMemoryBudgetBytes = 256L * 1024 * 1024;

    private readonly List<(T Item, long Bytes)> _items = new();
    private long _bytes;

    public WaveformBuffer(int capacity = DefaultCapacity, long memoryBudgetBytes = DefaultMemoryBudgetBytes)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (memoryBudgetBytes < 1) throw new ArgumentOutOfRangeException(nameof(memoryBudgetBytes));
        Capacity = capacity;
        MemoryBudgetBytes = memoryBudgetBytes;
    }

    public int Capacity { get; }

    public long MemoryBudgetBytes { get; }

    public int Count => _items.Count;

    /// <summary>Approximate bytes held (what the caller reported per item).</summary>
    public long BytesHeld => _bytes;

    /// <summary>Index of the capture on screen (0 = oldest), or -1 when empty.</summary>
    public int Position { get; private set; } = -1;

    public bool IsEmpty => _items.Count == 0;

    public T? Current => Position >= 0 ? _items[Position].Item : default;

    public bool CanStepBack => Position > 0;

    public bool CanStepForward => Position >= 0 && Position < _items.Count - 1;

    /// <summary>"n / N" with 1-based n, or "0 / 0".</summary>
    public string PositionText => $"{Position + 1} / {_items.Count}";

    /// <summary>
    /// Adds a capture as the newest entry and shows it. Drops the oldest entries while over the count or the memory
    /// budget, but always keeps the new one (a single capture larger than the budget is still shown).
    /// </summary>
    public void Add(T item, long approximateBytes)
    {
        var bytes = Math.Max(0, approximateBytes);
        _items.Add((item, bytes));
        _bytes += bytes;
        while (_items.Count > 1 && (_items.Count > Capacity || _bytes > MemoryBudgetBytes))
        {
            _bytes -= _items[0].Bytes;
            _items.RemoveAt(0);
        }
        Position = _items.Count - 1;
    }

    public bool StepBack()
    {
        if (!CanStepBack) return false;
        Position--;
        return true;
    }

    public bool StepForward()
    {
        if (!CanStepForward) return false;
        Position++;
        return true;
    }

    public bool MoveFirst()
    {
        if (_items.Count == 0 || Position == 0) return false;
        Position = 0;
        return true;
    }

    public bool MoveLast()
    {
        if (_items.Count == 0 || Position == _items.Count - 1) return false;
        Position = _items.Count - 1;
        return true;
    }

    public void Clear()
    {
        _items.Clear();
        _bytes = 0;
        Position = -1;
    }

    /// <summary>Bytes a capture's samples take: channels × samples × 8 (doubles), plus a small fixed overhead.</summary>
    public static long EstimateCaptureBytes(int channelCount, int sampleCount) =>
        (long)Math.Max(0, channelCount) * Math.Max(0, sampleCount) * sizeof(double) + 1024;
}
