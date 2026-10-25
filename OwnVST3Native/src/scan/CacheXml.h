#pragma once

#include <cstdint>
#include <string>
#include <vector>

/** One scanner result, independent of how it was found. */
struct PluginRecord
{
    std::string name, vendor, version, category, identifier, formatName;
    bool    isInstrument = false;
    int     numInputs    = -1;
    int     numOutputs   = -1;
    int32_t uniqueId     = 0;
};

/**
 * plugin-cache.xml in the KnownPluginList shape the 1.6/1.7 builds wrote:
 * <KNOWNPLUGINS><PLUGIN name format category manufacturer version file uniqueId isInstrument
 * numInputs numOutputs …/></KNOWNPLUGINS>. Unknown attributes and elements are ignored.
 */
std::string ownvst3_writeCacheXml(const std::vector<PluginRecord>& records);
bool        ownvst3_readCacheXml(const char* xml, std::vector<PluginRecord>& out);

/** 31-multiplier hash over a byte range, as the cache's uid attribute is computed. */
int32_t ownvst3_hash31(const uint8_t* data, size_t size);

/** True for "/x", "C:\x", "\\server\x" — a file path rather than an AudioUnit token. */
bool ownvst3_isAbsolutePath(const std::string& s);
