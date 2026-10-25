#pragma once

#include <cstdint>
#include <functional>

#include <X11/Xlib.h>

namespace LinuxLoop
{
    // null when headless
    Display* display();

    uint64_t addFd(int fd, std::function<void()> onReadable);
    void     removeFd(uint64_t id);

    void addWindow(Window w, std::function<void(XEvent&)> onEvent);
    void removeWindow(Window w);
}
