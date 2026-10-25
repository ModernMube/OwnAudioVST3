#pragma once

#include "IPluginBackend.h"

#include <memory>
#include <string>

enum class PluginKind { Unknown, Vst3, AudioUnit };

PluginKind ownvst3_kindOf(const std::string& identifier);

// blocks, may hop to the UI thread
std::unique_ptr<IPluginBackend> ownvst3_createBackend(const std::string& identifier, int subIndex);
