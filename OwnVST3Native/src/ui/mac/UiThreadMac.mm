#include "ui/UiThread.h"

#import <Foundation/Foundation.h>
#include <CoreFoundation/CoreFoundation.h>
#include <dispatch/dispatch.h>
#include <dlfcn.h>
#include <pthread.h>

#include <atomic>
#include <chrono>
#include <mutex>
#include <thread>
#include <unordered_map>

namespace
{
    // 0 unknown, 1 main queue pumped, 2 headless
    std::atomic<int> s_mainLoop { 0 };
    std::mutex       s_probeMutex;

    bool mainLoopAlive()
    {
        if (s_mainLoop.load(std::memory_order_acquire) == 1) return true;

        std::lock_guard<std::mutex> lock(s_probeMutex);
        if (s_mainLoop.load(std::memory_order_acquire) == 0)
        {
            dispatch_async(dispatch_get_main_queue(), ^{ s_mainLoop.store(1, std::memory_order_release); });

            const auto until = std::chrono::steady_clock::now() + std::chrono::milliseconds(1500);
            while (s_mainLoop.load(std::memory_order_acquire) != 1 && std::chrono::steady_clock::now() < until)
                std::this_thread::sleep_for(std::chrono::milliseconds(2));

            int expected = 0;
            s_mainLoop.compare_exchange_strong(expected, 2, std::memory_order_acq_rel);
        }

        return s_mainLoop.load(std::memory_order_acquire) == 1;
    }

    struct Timer
    {
        CFRunLoopTimerRef     ref = nullptr;
        std::function<void()> fn;
    };

    std::mutex                                              s_timerMutex;
    std::unordered_map<UiThread::TimerId, std::unique_ptr<Timer>> s_timers;
    UiThread::TimerId                                       s_nextTimer = 1;

    void timerFired(CFRunLoopTimerRef, void* info)
    {
        auto* t = static_cast<Timer*>(info);
        if (t->fn) t->fn();
    }
}

namespace UiThread
{
    bool available()
    {
        return isCurrent() || mainLoopAlive();
    }

    bool isCurrent()
    {
        return pthread_main_np() != 0;
    }

    void call(const std::function<void()>& fn)
    {
        if (isCurrent() || !mainLoopAlive())
        {
            fn();
            return;
        }

        dispatch_sync_f(dispatch_get_main_queue(), const_cast<std::function<void()>*>(&fn),
                        [](void* ctx) { (*static_cast<std::function<void()>*>(ctx))(); });
    }

    void post(std::function<void()> fn)
    {
        auto* heap = new std::function<void()>(std::move(fn));
        dispatch_async_f(dispatch_get_main_queue(), heap, [](void* ctx) {
            auto* f = static_cast<std::function<void()>*>(ctx);
            (*f)();
            delete f;
        });
    }

    TimerId startTimer(int intervalMs, std::function<void()> fn)
    {
        auto t = std::make_unique<Timer>();
        t->fn = std::move(fn);

        const CFTimeInterval interval = intervalMs / 1000.0;
        CFRunLoopTimerContext ctx { 0, t.get(), nullptr, nullptr, nullptr };
        t->ref = CFRunLoopTimerCreate(kCFAllocatorDefault, CFAbsoluteTimeGetCurrent() + interval,
                                      interval, 0, 0, timerFired, &ctx);
        if (!t->ref) return 0;

        CFRunLoopAddTimer(CFRunLoopGetMain(), t->ref, kCFRunLoopCommonModes);

        std::lock_guard<std::mutex> lock(s_timerMutex);
        const TimerId id = s_nextTimer++;
        s_timers.emplace(id, std::move(t));
        return id;
    }

    void stopTimer(TimerId id)
    {
        std::unique_ptr<Timer> t;
        {
            std::lock_guard<std::mutex> lock(s_timerMutex);
            auto it = s_timers.find(id);
            if (it == s_timers.end()) return;
            t = std::move(it->second);
            s_timers.erase(it);
        }

        CFRunLoopTimerInvalidate(t->ref);
        CFRelease(t->ref);
    }

    void pinLibrary()
    {
        static std::once_flag once;
        std::call_once(once, [] {
            Dl_info info {};
            if (dladdr(reinterpret_cast<const void*>(&pinLibrary), &info) && info.dli_fname)
                dlopen(info.dli_fname, RTLD_NOW | RTLD_NODELETE | RTLD_NOLOAD);
        });
    }

    bool guarded(void (*fn)(void*), void* ctx)
    {
        fn(ctx);
        return true;
    }
}
