#pragma once

#include <string>
#include <vector>

struct AudioUnitRegistryEntry
{
    std::string name, vendor, version, category, identifier;
    bool isInstrument = false;
};

/** Lists every hostable AudioUnit without loading any. Empty on non-Apple. */
std::vector<AudioUnitRegistryEntry> ownvst3_listAudioUnits();
