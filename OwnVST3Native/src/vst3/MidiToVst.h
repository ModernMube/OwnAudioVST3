#pragma once

#include "../../include/ownvst3_exports.h"

#include "pluginterfaces/vst/ivstevents.h"
#include "pluginterfaces/vst/ivstmidicontrollers.h"
#include "pluginterfaces/vst/vsttypes.h"

#include <array>

struct MidiMapTable
{
    static constexpr int kControllers = Steinberg::Vst::kCountCtrlNumber;

    std::array<std::array<Steinberg::Vst::ParamID, kControllers>, 16> ids;

    MidiMapTable() { clear(); }

    void clear()
    {
        for (auto& ch : ids) ch.fill(Steinberg::Vst::kNoParamId);
    }

    Steinberg::Vst::ParamID lookup(int channel, int controller) const noexcept
    {
        if (channel < 0 || channel > 15 || controller < 0 || controller >= kControllers)
            return Steinberg::Vst::kNoParamId;
        return ids[static_cast<size_t>(channel)][static_cast<size_t>(controller)];
    }
};

enum class MidiConversion { None, Event, Parameter };

MidiConversion ownvst3_convertMidi(const MidiEventC& in, int blockSize, const MidiMapTable& map,
                                   Steinberg::Vst::Event& event,
                                   Steinberg::Vst::ParamID& param, double& value) noexcept;
