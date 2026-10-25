#include "ui/UiThread.h"
#include "LinuxLoop.h"

#include <X11/Xlib.h>

#include <dlfcn.h>
#include <poll.h>
#include <sys/eventfd.h>
#include <unistd.h>

#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <deque>
#include <future>
#include <map>
#include <mutex>
#include <thread>
#include <vector>

namespace
{
    using Clock = std::chrono::steady_clock;

    struct FdHandler    { int fd; std::function<void()> fn; };
    struct TimerHandler { int intervalMs; Clock::time_point due; std::function<void()> fn; };

    std::once_flag  s_startOnce;
    std::thread::id s_threadId;
    int             s_wakeFd  = -1;
    Display*        s_display = nullptr;

    std::mutex                        s_taskMutex;
    std::deque<std::function<void()>> s_tasks;

    // Touched on the UI thread only.
    std::map<uint64_t, FdHandler>                     s_fds;
    std::map<Window, std::function<void(XEvent&)>>    s_windows;
    uint64_t                                          s_nextFd = 1;

    std::mutex                              s_timerMutex;
    std::map<UiThread::TimerId, TimerHandler> s_timers;
    UiThread::TimerId                       s_nextTimer = 1;

    void wake()
    {
        const uint64_t one = 1;
        (void)!write(s_wakeFd, &one, sizeof(one));
    }

    void runTasks()
    {
        uint64_t count;
        (void)!read(s_wakeFd, &count, sizeof(count));

        for (;;)
        {
            std::function<void()> fn;
            {
                std::lock_guard<std::mutex> lock(s_taskMutex);
                if (s_tasks.empty()) return;
                fn = std::move(s_tasks.front());
                s_tasks.pop_front();
            }
            fn();
        }
    }

    void runXEvents()
    {
        while (s_display && XPending(s_display) > 0)
        {
            XEvent ev;
            XNextEvent(s_display, &ev);

            auto it = s_windows.find(ev.xany.window);
            if (it != s_windows.end())
            {
                auto fn = it->second;
                fn(ev);
            }
        }
    }

    int runTimers()
    {
        const auto now = Clock::now();
        std::vector<std::pair<UiThread::TimerId, std::function<void()>>> due;
        int nextMs = 1000;

        {
            std::lock_guard<std::mutex> lock(s_timerMutex);
            for (auto& [id, t] : s_timers)
            {
                if (t.due <= now)
                {
                    due.emplace_back(id, t.fn);
                    t.due = now + std::chrono::milliseconds(t.intervalMs);
                }
                const auto left = std::chrono::duration_cast<std::chrono::milliseconds>(t.due - now).count();
                nextMs = std::min<int>(nextMs, static_cast<int>(std::max<long long>(0, left)));
            }
        }

        for (auto& [id, fn] : due)
        {
            bool alive;
            {
                std::lock_guard<std::mutex> lock(s_timerMutex);
                alive = s_timers.count(id) != 0;
            }
            if (alive) fn();
        }

        return nextMs;
    }

    void threadMain(std::promise<void>* ready)
    {
        s_display  = XOpenDisplay(nullptr);
        s_threadId = std::this_thread::get_id();
        ready->set_value();

        std::vector<pollfd> polls;
        std::vector<uint64_t> ids;

        for (;;)
        {
            const int timeout = runTimers();

            polls.clear();
            ids.clear();
            polls.push_back({ s_wakeFd, POLLIN, 0 });
            if (s_display) polls.push_back({ ConnectionNumber(s_display), POLLIN, 0 });
            for (auto& [id, h] : s_fds) { polls.push_back({ h.fd, POLLIN, 0 }); ids.push_back(id); }

            if (s_display) XFlush(s_display);
            if (!(s_display && XPending(s_display) > 0))
                poll(polls.data(), polls.size(), timeout);

            if (polls[0].revents & POLLIN) runTasks();
            runXEvents();

            const size_t first = s_display ? 2 : 1;
            for (size_t i = first; i < polls.size(); ++i)
            {
                if (!(polls[i].revents & (POLLIN | POLLHUP | POLLERR))) continue;

                auto it = s_fds.find(ids[i - first]);
                if (it == s_fds.end()) continue;
                auto fn = it->second.fn;
                fn();
            }
        }
    }

    void ensureStarted()
    {
        std::call_once(s_startOnce, [] {
            UiThread::pinLibrary();
            XInitThreads();

            s_wakeFd = eventfd(0, EFD_CLOEXEC | EFD_NONBLOCK);

            std::promise<void> ready;
            auto fut = ready.get_future();
            std::thread(threadMain, &ready).detach();
            fut.wait();
        });
    }
}

namespace LinuxLoop
{
    Display* display()
    {
        ensureStarted();
        return s_display;
    }

    uint64_t addFd(int fd, std::function<void()> onReadable)
    {
        const uint64_t id = s_nextFd++;
        s_fds.emplace(id, FdHandler { fd, std::move(onReadable) });
        return id;
    }

    void removeFd(uint64_t id)
    {
        s_fds.erase(id);
    }

    void addWindow(Window w, std::function<void(XEvent&)> onEvent)
    {
        s_windows[w] = std::move(onEvent);
    }

    void removeWindow(Window w)
    {
        s_windows.erase(w);
    }
}

namespace UiThread
{
    bool available()
    {
        return LinuxLoop::display() != nullptr;
    }

    bool isCurrent()
    {
        ensureStarted();
        return std::this_thread::get_id() == s_threadId;
    }

    void call(const std::function<void()>& fn)
    {
        if (isCurrent()) { fn(); return; }

        std::mutex m;
        std::condition_variable cv;
        bool done = false;

        post([&] {
            fn();
            std::lock_guard<std::mutex> lock(m);
            done = true;
            cv.notify_one();
        });

        std::unique_lock<std::mutex> lock(m);
        cv.wait(lock, [&] { return done; });
    }

    void post(std::function<void()> fn)
    {
        ensureStarted();
        {
            std::lock_guard<std::mutex> lock(s_taskMutex);
            s_tasks.push_back(std::move(fn));
        }
        wake();
    }

    TimerId startTimer(int intervalMs, std::function<void()> fn)
    {
        ensureStarted();

        TimerId id;
        {
            std::lock_guard<std::mutex> lock(s_timerMutex);
            id = s_nextTimer++;
            s_timers.emplace(id, TimerHandler { std::max(1, intervalMs),
                                                Clock::now() + std::chrono::milliseconds(intervalMs), std::move(fn) });
        }
        wake();
        return id;
    }

    void stopTimer(TimerId id)
    {
        std::lock_guard<std::mutex> lock(s_timerMutex);
        s_timers.erase(id);
    }

    void pinLibrary()
    {
        static std::once_flag once;
        std::call_once(once, [] {
            Dl_info info {};
            if (dladdr(reinterpret_cast<void*>(&pinLibrary), &info) && info.dli_fname)
                dlopen(info.dli_fname, RTLD_NOW | RTLD_NODELETE | RTLD_NOLOAD);
        });
    }

    bool guarded(void (*fn)(void*), void* ctx)
    {
        fn(ctx);
        return true;
    }
}
