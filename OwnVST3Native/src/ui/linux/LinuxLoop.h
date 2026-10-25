#pragma once

#include <cstdint>
#include <functional>

#include <X11/Xlib.h>

/** The Linux UI thread's event loop: X11 events, plugin fds, timers. UI thread only. */
namespace LinuxLoop
{
    /** Null when there is no X server (headless CI); editors are unavailable then. */
    Display* display();

    uint64_t addFd(int fd, std::function<void()> onReadable);
    void     removeFd(uint64_t id);

    void addWindow(Window w, std::function<void(XEvent&)> onEvent);
    void removeWindow(Window w);
}
