#pragma once

#include <array>
#include <atomic>
#include <cstddef>

/**
 * Lock-free single-producer / single-consumer ring buffer.
 *
 * Capacity must be a power of two.  All operations are wait-free.
 * The producer (UI thread) calls tryEnqueue(); the consumer (audio thread)
 * calls tryDequeue().  Crossing those roles is a data race.
 */
template<typename T, std::size_t Capacity>
class SpscQueue
{
    static_assert((Capacity & (Capacity - 1u)) == 0u,
                  "SpscQueue: Capacity must be a power of two.");

public:
    /** Enqueues item. Returns false if the queue is full. */
    bool tryEnqueue(const T& item) noexcept
    {
        const std::size_t write = _write.load(std::memory_order_relaxed);
        const std::size_t next  = (write + 1u) & (Capacity - 1u);

        if (next == _read.load(std::memory_order_acquire))
            return false;

        _buffer[write] = item;
        _write.store(next, std::memory_order_release);
        return true;
    }

    /** Dequeues into item. Returns false if the queue is empty. */
    bool tryDequeue(T& item) noexcept
    {
        const std::size_t read = _read.load(std::memory_order_relaxed);

        if (read == _write.load(std::memory_order_acquire))
            return false;

        item = _buffer[read];
        _read.store((read + 1u) & (Capacity - 1u), std::memory_order_release);
        return true;
    }

    /** Returns true if the queue contains no items. */
    bool isEmpty() const noexcept
    {
        return _read.load(std::memory_order_acquire)
            == _write.load(std::memory_order_acquire);
    }

private:
    std::array<T, Capacity> _buffer{};
    alignas(64) std::atomic<std::size_t> _write{ 0u };
    alignas(64) std::atomic<std::size_t> _read { 0u };
};
