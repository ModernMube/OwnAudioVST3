#pragma once

#include "CacheXml.h"

#include <string>

/**
 * A VST3 bundle described without loading it: moduleinfo.json when the bundle ships one
 * (name, vendor, category, instrument flag of its first audio class), else just the bundle name.
 */
PluginRecord ownvst3_describeVst3Bundle(const std::string& bundlePath);

/** The moduleinfo.json part on its own. False when the text is not a module info. */
bool ownvst3_applyModuleInfo(const std::string& json, PluginRecord& record);
