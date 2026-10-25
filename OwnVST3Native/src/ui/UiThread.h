#pragma once

#include <cstdint>
#include <functional>

/**
 * The one thread that owns plugins (load, controller, state) and their editor windows.
 * Windows / Linux: a native thread with its own message / event loop.
 * macOS: the main thread. Calls from it run directly; other threads go through GCD, and if
 * nobody pumps the main queue (console app, test runner) everything runs on the caller instead.
 */
namespace UiThread
{
    bool isCurrent();

    /** False on macOS when we are off the main thread and nobody pumps it: no windows then. */
    bool available();

    /** Runs fn on the UI thread and waits. Directly when already there. */
    void call(const std::function<void()>& fn);

    /** Queues fn for the UI thread and returns. */
    void post(std::function<void()> fn);

    using TimerId = uint64_t;

    /** Repeats fn on the UI thread every intervalMs. Never a default-mode-only timer on macOS. */
    TimerId startTimer(int intervalMs, std::function<void()> fn);

    /** After this returns the callback will not run again. Call from the UI thread or via call(). */
    void stopTimer(TimerId id);

    /** Keeps this library mapped for the life of the process; a thread of ours may still be in it. */
    void pinLibrary();

    /** Runs fn(ctx); on Windows a structured exception inside it (a broken DllMain) returns false. */
    bool guarded(void (*fn)(void*), void* ctx);
}

template <typename T>
T ownvst3_onUi(const std::function<T()>& fn)
{
    T result{};
    UiThread::call([&] { result = fn(); });
    return result;
}
