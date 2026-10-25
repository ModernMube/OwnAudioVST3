#include "RunLoopLinux.h"
#include "LinuxLoop.h"
#include "ui/UiThread.h"

#include <algorithm>

using namespace Steinberg;

Vst3RunLoop::~Vst3RunLoop()
{
    clear();
}

tresult PLUGIN_API Vst3RunLoop::registerEventHandler(Linux::IEventHandler* handler, Linux::FileDescriptor fd)
{
    if (!handler) return kInvalidArgument;

    UiThread::call([&] {
        const uint64_t id = LinuxLoop::addFd(fd, [handler, fd] { handler->onFDIsSet(fd); });
        _fds.push_back({ handler, id });
    });
    return kResultTrue;
}

tresult PLUGIN_API Vst3RunLoop::unregisterEventHandler(Linux::IEventHandler* handler)
{
    UiThread::call([&] {
        for (auto& e : _fds)
            if (e.handler == handler) LinuxLoop::removeFd(e.id);
        _fds.erase(std::remove_if(_fds.begin(), _fds.end(), [&](const FdEntry& e) { return e.handler == handler; }), _fds.end());
    });
    return kResultTrue;
}

tresult PLUGIN_API Vst3RunLoop::registerTimer(Linux::ITimerHandler* handler, Linux::TimerInterval milliseconds)
{
    if (!handler) return kInvalidArgument;

    UiThread::call([&] {
        const auto id = UiThread::startTimer(static_cast<int>(milliseconds), [handler] { handler->onTimer(); });
        _timers.push_back({ handler, id });
    });
    return kResultTrue;
}

tresult PLUGIN_API Vst3RunLoop::unregisterTimer(Linux::ITimerHandler* handler)
{
    UiThread::call([&] {
        for (auto& t : _timers)
            if (t.handler == handler) UiThread::stopTimer(t.id);
        _timers.erase(std::remove_if(_timers.begin(), _timers.end(), [&](const TimerEntry& t) { return t.handler == handler; }), _timers.end());
    });
    return kResultTrue;
}

void Vst3RunLoop::clear()
{
    UiThread::call([&] {
        for (auto& e : _fds)    LinuxLoop::removeFd(e.id);
        for (auto& t : _timers) UiThread::stopTimer(t.id);
        _fds.clear();
        _timers.clear();
    });
}

tresult PLUGIN_API Vst3RunLoop::queryInterface(const TUID iid, void** obj)
{
    if (FUnknownPrivate::iidEqual(iid, Linux::IRunLoop::iid) || FUnknownPrivate::iidEqual(iid, FUnknown::iid))
    {
        addRef();
        *obj = static_cast<Linux::IRunLoop*>(this);
        return kResultOk;
    }
    *obj = nullptr;
    return kNoInterface;
}

uint32 PLUGIN_API Vst3RunLoop::release()
{
    const uint32 r = --_refs;
    if (r == 0) delete this;
    return r;
}
