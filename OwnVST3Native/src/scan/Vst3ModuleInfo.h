#pragma once

#include "CacheXml.h"

#include <string>

PluginRecord ownvst3_describeVst3Bundle(const std::string& bundlePath);

bool ownvst3_applyModuleInfo(const std::string& json, PluginRecord& record);
