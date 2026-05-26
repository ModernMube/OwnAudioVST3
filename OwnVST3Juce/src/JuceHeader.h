#pragma once

/**
 * Single include point for all JUCE headers used by this library.
 * Including through this file guarantees that JUCE compile-time feature flags
 * defined in CMakeLists.txt (via target_compile_definitions) are visible before
 * the first JUCE header is parsed.
 *
 * Do NOT include individual JUCE headers directly elsewhere in the project.
 */

#include <juce_audio_processors/juce_audio_processors.h>
#include <juce_gui_basics/juce_gui_basics.h>
#include <juce_events/juce_events.h>
