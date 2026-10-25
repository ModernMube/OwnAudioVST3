#pragma once

#include "core/IPluginBackend.h"

#include <memory>
#include <string>

/** identifier: "AudioUnit:Type/type,subtype,manu", or a .component path (subIndex picks the component). */
std::unique_ptr<IPluginBackend> ownvst3_createAuBackend(const std::string& identifier, int subIndex);
