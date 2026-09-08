#pragma once

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>

/**
 * Lock-free multi-producer / single-consumer ring buffer (Vyukov sequence scheme).
 *
 * Capacity must be a power of two.  Any number of threads may call tryEnqueue();
 * exactly one thread — the audio thread — may call tryDequeue().
 */
template<typename T, std::size_t Capacity>
class MpscQueue
{
    static_assert((Capacity & (Capacity - 1u)) == 0u,
                  "MpscQueue: Capacity must be a power of two.");

public:
    MpscQueue() noexcept
    {
        for (std::size_t i = 0; i < Capacity; ++i)
            _slots[i].seq.store(i, std::memory_order_relaxed);
    }

    /** Enqueues item. Returns false if the queue is full. Safe from any thread. */
    bool tryEnqueue(const T& item) noexcept
    {
        std::size_t pos = _write.load(std::memory_order_relaxed);

        for (;;)
        {
            auto& slot = _slots[pos & (Capacity - 1u)];
            const auto diff = static_cast<std::intptr_t>(slot.seq.load(std::memory_order_acquire))
                            - static_cast<std::intptr_t>(pos);

            if (diff == 0)
            {
                if (_write.compare_exchange_weak(pos, pos + 1u, std::memory_order_relaxed))
                {
                    slot.value = item;
                    slot.seq.store(pos + 1u, std::memory_order_release);
                    return true;
                }
            }
            else if (diff < 0)
            {
                return false;
            }
            else
            {
                pos = _write.load(std::memory_order_relaxed);
            }
        }
    }

    /** Dequeues into item. Consumer thread only. Returns false if the queue is empty. */
    bool tryDequeue(T& item) noexcept
    {
        const std::size_t pos = _read.load(std::memory_order_relaxed);
        auto& slot = _slots[pos & (Capacity - 1u)];

        if (static_cast<std::intptr_t>(slot.seq.load(std::memory_order_acquire))
            - static_cast<std::intptr_t>(pos + 1u) != 0)
            return false;

        item = slot.value;
        slot.seq.store(pos + Capacity, std::memory_order_release);
        _read.store(pos + 1u, std::memory_order_relaxed);
        return true;
    }

private:
    struct Slot
    {
        std::atomic<std::size_t> seq;
        T                        value{};
    };

    alignas(64) std::array<Slot, Capacity> _slots{};
    alignas(64) std::atomic<std::size_t>   _write{ 0u };
    alignas(64) std::atomic<std::size_t>   _read { 0u };
};
