#include "ui/UiThread.h"
#include "Win32Common.h"

#include <ole2.h>

#include <atomic>
#include <condition_variable>
#include <future>
#include <memory>
#include <mutex>
#include <thread>
#include <unordered_map>

namespace
{
    constexpr UINT WM_OWN_TASK = WM_APP + 0x31;

    std::once_flag   s_startOnce;
    DWORD            s_threadId = 0;
    HWND             s_sink     = nullptr;

    std::mutex                                              s_timerMutex;
    std::unordered_map<UINT_PTR, std::function<void()>>      s_timers;
    UINT_PTR                                                s_nextTimer = 1;

    struct Task
    {
        std::function<void()>   fn;
        std::mutex              mutex;
        std::condition_variable done;
        bool                    finished = false;
        bool                    waited   = false;
    };

    void runTask(Task* t)
    {
        t->fn();
        if (!t->waited) { delete t; return; }

        std::lock_guard<std::mutex> lock(t->mutex);
        t->finished = true;
        t->done.notify_one();
    }

    LRESULT CALLBACK sinkProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
    {
        if (msg == WM_OWN_TASK)
        {
            runTask(reinterpret_cast<Task*>(lp));
            return 0;
        }
        if (msg == WM_TIMER)
        {
            std::function<void()> fn;
            {
                std::lock_guard<std::mutex> lock(s_timerMutex);
                auto it = s_timers.find(static_cast<UINT_PTR>(wp));
                if (it != s_timers.end()) fn = it->second;
            }
            if (fn) fn();
            return 0;
        }
        return DefWindowProcW(hwnd, msg, wp, lp);
    }

    // A crashing plugin WndProc must not take the UI thread down. No C++ objects with
    // destructors may live in a function that uses __try.
    void dispatchGuarded(const MSG* msg)
    {
        __try
        {
            DispatchMessageW(msg);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
        }
    }

    void setPerMonitorDpi()
    {
        using Fn = DPI_AWARENESS_CONTEXT (WINAPI*)(DPI_AWARENESS_CONTEXT);
        if (auto user32 = GetModuleHandleW(L"user32.dll"))
            if (auto fn = reinterpret_cast<Fn>(GetProcAddress(user32, "SetThreadDpiAwarenessContext")))
                fn(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }

    void threadMain(std::promise<void>* ready)
    {
        OleInitialize(nullptr);
        setPerMonitorDpi();

        WNDCLASSEXW wc { sizeof(wc) };
        wc.lpfnWndProc   = sinkProc;
        wc.hInstance     = ownvst3_moduleInstance();
        wc.lpszClassName = L"OwnVst3UiSink";
        RegisterClassExW(&wc);

        s_sink     = CreateWindowExW(0, wc.lpszClassName, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, wc.hInstance, nullptr);
        s_threadId = GetCurrentThreadId();
        ready->set_value();

        MSG msg;
        while (GetMessageW(&msg, nullptr, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            dispatchGuarded(&msg);
        }

        OleUninitialize();
    }

    void ensureStarted()
    {
        std::call_once(s_startOnce, [] {
            UiThread::pinLibrary();

            std::promise<void> ready;
            auto fut = ready.get_future();
            std::thread(threadMain, &ready).detach();
            fut.wait();
        });
    }
}

HINSTANCE ownvst3_moduleInstance()
{
    // Our own HINSTANCE, not the host exe's: window classes registered with the exe's
    // handle collide with JUCE-based plugins doing the same (TDR Nova, e60afc3).
    static HMODULE self = [] {
        HMODULE m = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(&ownvst3_moduleInstance), &m);
        return m;
    }();
    return self;
}

namespace UiThread
{
    bool available()
    {
        return true;
    }

    bool isCurrent()
    {
        ensureStarted();
        return GetCurrentThreadId() == s_threadId;
    }

    void call(const std::function<void()>& fn)
    {
        if (isCurrent()) { fn(); return; }

        auto* t = new Task;
        t->fn     = fn;
        t->waited = true;

        if (!PostMessageW(s_sink, WM_OWN_TASK, 0, reinterpret_cast<LPARAM>(t)))
        {
            delete t;
            return;
        }

        {
            std::unique_lock<std::mutex> lock(t->mutex);
            t->done.wait(lock, [t] { return t->finished; });
        }
        delete t;
    }

    void post(std::function<void()> fn)
    {
        ensureStarted();

        auto* t = new Task;
        t->fn = std::move(fn);
        if (!PostMessageW(s_sink, WM_OWN_TASK, 0, reinterpret_cast<LPARAM>(t)))
            delete t;
    }

    TimerId startTimer(int intervalMs, std::function<void()> fn)
    {
        ensureStarted();

        UINT_PTR id;
        {
            std::lock_guard<std::mutex> lock(s_timerMutex);
            id = s_nextTimer++;
            s_timers.emplace(id, std::move(fn));
        }

        call([&] { SetTimer(s_sink, id, static_cast<UINT>(intervalMs), nullptr); });
        return static_cast<TimerId>(id);
    }

    void stopTimer(TimerId id)
    {
        call([&] { KillTimer(s_sink, static_cast<UINT_PTR>(id)); });

        std::lock_guard<std::mutex> lock(s_timerMutex);
        s_timers.erase(static_cast<UINT_PTR>(id));
    }

    void pinLibrary()
    {
        HMODULE self = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                           reinterpret_cast<LPCWSTR>(&pinLibrary), &self);
    }

    bool guarded(void (*fn)(void*), void* ctx)
    {
        __try
        {
            fn(ctx);
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
    }
}
