#include "AudioUnitRegistry.h"
#include "core/AuIdentifier.h"

#if defined(__APPLE__)

#include <AudioToolbox/AudioToolbox.h>
#include <CoreFoundation/CoreFoundation.h>

#include <cstring>

namespace
{
    std::string trim(const std::string& s)
    {
        const auto first = s.find_first_not_of(" \t\r\n");
        if (first == std::string::npos) return "";

        return s.substr(first, s.find_last_not_of(" \t\r\n") - first + 1);
    }

    std::string cfStringToStd(CFStringRef s)
    {
        if (!s) return "";

        const CFIndex maxLen =
            CFStringGetMaximumSizeForEncoding(CFStringGetLength(s), kCFStringEncodingUTF8) + 1;

        std::string out(static_cast<size_t>(maxLen), '\0');
        if (!CFStringGetCString(s, out.data(), maxLen, kCFStringEncodingUTF8))
            return "";

        out.resize(std::strlen(out.c_str()));
        return out;
    }
}

std::vector<AudioUnitRegistryEntry> ownvst3_listAudioUnits()
{
    std::vector<AudioUnitRegistryEntry> found;
    AudioComponent comp = nullptr;

    for (;;)
    {
        AudioComponentDescription desc {};

        comp = AudioComponentFindNext(comp, &desc);
        if (comp == nullptr) break;

        if (AudioComponentGetDescription(comp, &desc) != noErr) continue;

        const std::string folder = ownvst3_auCategoryFolder(desc.componentType);
        if (folder.empty()) continue;

        AudioUnitRegistryEntry e;
        e.identifier = ownvst3_makeAuIdentifier({ desc.componentType, desc.componentSubType, desc.componentManufacturer });

        e.category     = folder.substr(0, folder.size() - 1);
        e.isInstrument = desc.componentType == kAudioUnitType_MusicDevice;

        CFStringRef cfName = nullptr;
        if (AudioComponentCopyName(comp, &cfName) == noErr && cfName != nullptr)
        {
            e.name = cfStringToStd(cfName);
            CFRelease(cfName);
        }

        const auto colon = e.name.find(':');
        if (colon != std::string::npos)
        {
            e.vendor = trim(e.name.substr(0, colon));
            e.name   = trim(e.name.substr(colon + 1));
        }

        if (e.name.empty())   e.name = "<Unknown>";
        if (e.vendor.empty()) e.vendor = ownvst3_fourCCString(desc.componentManufacturer);

        UInt32 version = 0;
        if (AudioComponentGetVersion(comp, &version) == noErr)
        {
            e.version = std::to_string(version >> 16) + "."
                      + std::to_string((version >> 8) & 0xff) + "."
                      + std::to_string(version & 0xff);
        }

        found.push_back(std::move(e));
    }

    return found;
}

#else

std::vector<AudioUnitRegistryEntry> ownvst3_listAudioUnits()
{
    return {};
}

#endif
