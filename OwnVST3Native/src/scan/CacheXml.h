#pragma once

#include <cstdint>
#include <string>
#include <vector>

struct PluginRecord
{
    std::string name, vendor, version, category, identifier, formatName;
    bool    isInstrument = false;
    int     numInputs    = -1;
    int     numOutputs   = -1;
    int32_t uniqueId     = 0;
};

// plugin-cache.xml, same KNOWNPLUGINS shape as the 1.6/1.7 builds
std::string ownvst3_writeCacheXml(const std::vector<PluginRecord>& records);
bool        ownvst3_readCacheXml(const char* xml, std::vector<PluginRecord>& out);

int32_t ownvst3_hash31(const uint8_t* data, size_t size);

bool ownvst3_isAbsolutePath(const std::string& s);
