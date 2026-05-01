using System.Runtime.CompilerServices;

namespace OwnVST3Host;

/// <summary>
/// Lock-free Single-Producer Single-Consumer (SPSC) ring buffer.
/// Thread-safe when exactly one thread calls TryEnqueue and one thread calls TryDequeue.
/// Capacity must be a power of two (e.g. 256, 512, 1024).
/// </summary>
public sealed class LockFreeQueue<T>
{
    private readonly T[] _buffer;
    private readonly int _mask;
    private volatile int _writeIndex;
    private volatile int _readIndex;

    public int Capacity => _buffer.Length;
    public bool IsEmpty => _readIndex == _writeIndex;

    public LockFreeQueue(int capacity = 256)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentException("Capacity must be a positive power of two.", nameof(capacity));
        _buffer = new T[capacity];
        _mask = capacity - 1;
    }

    /// <summary>
    /// Enqueues an item. Call only from the producer thread.
    /// Returns false if the queue is full (item is dropped).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in T item)
    {
        int write = _writeIndex;
        int next = (write + 1) & _mask;
        if (next == _readIndex)   // full check (volatile read of _readIndex)
            return false;

        _buffer[write] = item;
        _writeIndex = next;       // volatile write: acts as release fence, making _buffer[write] visible
        return true;
    }

    /// <summary>
    /// Dequeues an item. Call only from the consumer thread.
    /// Returns false if the queue is empty.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out T item)
    {
        int read = _readIndex;
        if (read == _writeIndex)  // empty check (volatile read of _writeIndex = acquire fence)
        {
            item = default!;
            return false;
        }

        item = _buffer[read];
        _buffer[read] = default!; // clear slot to release GC reference
        _readIndex = (read + 1) & _mask; // volatile write: release fence
        return true;
    }
}
