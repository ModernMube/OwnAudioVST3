#pragma once

#include <cstdint>
#include <functional>

// owns plugins and editors. macOS: main thread, the caller when nobody pumps it
namespace UiThread
{
    bool isCurrent();

    bool available();

    void call(const std::function<void()>& fn);

    void post(std::function<void()> fn);

    using TimerId = uint64_t;

    TimerId startTimer(int intervalMs, std::function<void()> fn);

    void stopTimer(TimerId id);

    void pinLibrary();

    bool guarded(void (*fn)(void*), void* ctx);
}

template <typename T>
T ownvst3_onUi(const std::function<T()>& fn)
{
    T result{};
    UiThread::call([&] { result = fn(); });
    return result;
}
