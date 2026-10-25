#pragma once

#include "IPluginBackend.h"

#include <memory>
#include <string>

enum class PluginKind { Unknown, Vst3, AudioUnit };

/** Which backend an identifier belongs to, by its shape only — nothing is loaded. */
PluginKind ownvst3_kindOf(const std::string& identifier);

/** Loads the subIndex'th plugin behind identifier, or returns null. Blocks; may hop to the UI thread. */
std::unique_ptr<IPluginBackend> ownvst3_createBackend(const std::string& identifier, int subIndex);
