#include "MidiToVst.h"

#include <algorithm>
#include <cstring>

using namespace Steinberg;
using namespace Steinberg::Vst;

MidiConversion ownvst3_convertMidi(const MidiEventC& in, int blockSize, const MidiMapTable& map,
                                   Event& event, ParamID& param, double& value) noexcept
{
    const int type    = in.status & 0xF0;
    const int channel = in.status & 0x0F;
    const int d1      = in.data1 & 0x7F;
    const int d2      = in.data2 & 0x7F;

    auto mapped = [&](int controller, double v) {
        param = map.lookup(channel, controller);
        value = v;
        return param == kNoParamId ? MidiConversion::None : MidiConversion::Parameter;
    };

    std::memset(&event, 0, sizeof(event));
    event.busIndex     = 0;
    event.sampleOffset = std::clamp(in.sampleOffset, 0, std::max(0, blockSize - 1));
    event.flags        = Event::kIsLive;

    switch (type)
    {
        case 0x90:
            if (d2 > 0)
            {
                event.type             = Event::kNoteOnEvent;
                event.noteOn.channel   = static_cast<int16>(channel);
                event.noteOn.pitch     = static_cast<int16>(d1);
                event.noteOn.velocity  = d2 / 127.0f;
                event.noteOn.noteId    = -1;
                return MidiConversion::Event;
            }
            [[fallthrough]];

        case 0x80:
            event.type              = Event::kNoteOffEvent;
            event.noteOff.channel   = static_cast<int16>(channel);
            event.noteOff.pitch     = static_cast<int16>(d1);
            event.noteOff.velocity  = type == 0x80 ? d2 / 127.0f : 0.0f;
            event.noteOff.noteId    = -1;
            return MidiConversion::Event;

        case 0xA0:
            event.type                  = Event::kPolyPressureEvent;
            event.polyPressure.channel  = static_cast<int16>(channel);
            event.polyPressure.pitch    = static_cast<int16>(d1);
            event.polyPressure.pressure = d2 / 127.0f;
            event.polyPressure.noteId   = -1;
            return MidiConversion::Event;

        case 0xB0:
            return mapped(d1, d2 / 127.0);

        case 0xD0:
            return mapped(kAfterTouch, d1 / 127.0);

        case 0xE0:
            return mapped(kPitchBend, ((d2 << 7) | d1) / 16383.0);

        default:
            return MidiConversion::None;
    }
}
