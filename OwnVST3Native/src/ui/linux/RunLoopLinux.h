#pragma once

#include "pluginterfaces/gui/iplugview.h"

#include <atomic>
#include <cstdint>
#include <vector>

class Vst3RunLoop final : public Steinberg::Linux::IRunLoop
{
public:
    Vst3RunLoop() = default;
    ~Vst3RunLoop();

    Steinberg::tresult PLUGIN_API registerEventHandler(Steinberg::Linux::IEventHandler* handler,
                                                       Steinberg::Linux::FileDescriptor fd) override;
    Steinberg::tresult PLUGIN_API unregisterEventHandler(Steinberg::Linux::IEventHandler* handler) override;
    Steinberg::tresult PLUGIN_API registerTimer(Steinberg::Linux::ITimerHandler* handler,
                                                Steinberg::Linux::TimerInterval milliseconds) override;
    Steinberg::tresult PLUGIN_API unregisterTimer(Steinberg::Linux::ITimerHandler* handler) override;

    void clear();

    Steinberg::tresult PLUGIN_API queryInterface(const Steinberg::TUID iid, void** obj) override;
    Steinberg::uint32 PLUGIN_API addRef() override { return ++_refs; }
    Steinberg::uint32 PLUGIN_API release() override;

private:
    struct FdEntry    { Steinberg::Linux::IEventHandler* handler; uint64_t id; };
    struct TimerEntry { Steinberg::Linux::ITimerHandler* handler; uint64_t id; };

    std::vector<FdEntry>    _fds;
    std::vector<TimerEntry> _timers;
    std::atomic<Steinberg::uint32> _refs { 1 };
};
