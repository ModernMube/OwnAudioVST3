#pragma once

#include "core/IPluginBackend.h"

#include <memory>
#include <string>

// "AudioUnit:..." token or a .component path
std::unique_ptr<IPluginBackend> ownvst3_createAuBackend(const std::string& identifier, int subIndex);
