#pragma once

#include <string>
#include <vector>

struct AudioUnitRegistryEntry
{
    std::string name, vendor, version, category, identifier;
    bool isInstrument = false;
};

std::vector<AudioUnitRegistryEntry> ownvst3_listAudioUnits();
