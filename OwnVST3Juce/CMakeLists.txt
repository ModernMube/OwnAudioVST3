cmake_minimum_required(VERSION 3.22)

project(ownvst3
    VERSION 2.0.0
    DESCRIPTION "JUCE-based VST3 host library exposing a stable C API for .NET P/Invoke."
    LANGUAGES C CXX
)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# JUCE is fetched automatically at configure time via FetchContent.
# The downloaded source is cached by CMake in the build directory;
# the GitHub Actions cache step reuses it across CI runs.
include(FetchContent)

FetchContent_Declare(
    juce
    GIT_REPOSITORY https://github.com/juce-framework/JUCE.git
    GIT_TAG        8.0.4
    GIT_SHALLOW    TRUE
)
FetchContent_MakeAvailable(juce)

# Shared library – output name and prefix/suffix are set per platform below.
add_library(ownvst3 SHARED
    src/Exports.cpp
    src/PluginInstance.cpp
    src/EditorWindow.cpp
    src/StringCache.cpp
)

target_include_directories(ownvst3 PRIVATE
    include
    src
)

# JUCE compile-time feature flags.
# All flags must be defined before the JUCE headers are first included
# (CMake propagates them via the target's compile definitions).
target_compile_definitions(ownvst3 PRIVATE
    JUCE_PLUGINHOST_VST3=1
    JUCE_PLUGINHOST_AU=0
    JUCE_PLUGINHOST_LADSPA=0
    JUCE_VST3_CAN_REPLACE_VST2=0
    JUCE_USE_CURL=0
    JUCE_WEB_BROWSER=0
    JUCE_USE_CAMERA=0
    JUCE_MODAL_LOOPS_PERMITTED=1
    JUCE_STANDALONE_APPLICATION=0
    JUCE_DISABLE_JUCE_VERSION_PRINTING=1
    DONT_SET_USING_JUCE_NAMESPACE=1
)

# JUCE modules required for VST3 hosting and GUI editor windows.
target_link_libraries(ownvst3 PRIVATE
    juce::juce_audio_processors
    juce::juce_gui_basics
    juce::juce_events
)

# ─── Platform-specific settings ────────────────────────────────────────────────

if(WIN32)
    # DEF file prevents C++ name-mangling on MSVC; each exported symbol must
    # appear in ownvst3.def with exactly the name the C# side expects.
    target_link_options(ownvst3 PRIVATE
        "/DEF:${CMAKE_CURRENT_SOURCE_DIR}/ownvst3.def"
    )
    target_link_libraries(ownvst3 PRIVATE ole32 oleaut32 uuid)
    set_target_properties(ownvst3 PROPERTIES
        OUTPUT_NAME "ownvst3"
        PREFIX      ""
        SUFFIX      ".dll"
    )

elseif(APPLE)
    set_target_properties(ownvst3 PROPERTIES
        MACOSX_RPATH ON
        OUTPUT_NAME  "ownvst3"
        PREFIX       "lib"
        SUFFIX       ".dylib"
    )
    target_link_libraries(ownvst3 PRIVATE
        "-framework Cocoa"
        "-framework CoreAudio"
        "-framework CoreMIDI"
        "-framework AudioToolbox"
        "-framework CoreFoundation"
    )

elseif(UNIX)
    set_target_properties(ownvst3 PROPERTIES
        OUTPUT_NAME "ownvst3"
        PREFIX      "lib"
        SUFFIX      ".so"
    )
    find_package(Threads REQUIRED)

    if(CMAKE_CROSSCOMPILING)
        # Ubuntu multiarch cross-compile: CMake's find_package(X11) cannot locate
        # headers/libs under the FIND_ROOT_PATH because Ubuntu installs multiarch
        # packages to /usr/include and /usr/lib/<triple>, not /usr/<triple>/.
        # Explicitly add the multiarch library dir so the cross-linker finds the
        # ARM64 .so stubs installed by libx11-dev:arm64 etc.
        target_include_directories(ownvst3 PRIVATE /usr/include)
        target_link_directories(ownvst3 PRIVATE /usr/lib/aarch64-linux-gnu)
        target_link_libraries(ownvst3 PRIVATE
            X11 Xext freetype asound dl Threads::Threads
        )
    else()
        find_package(X11 REQUIRED)
        find_package(Freetype REQUIRED)
        target_link_libraries(ownvst3 PRIVATE
            X11::X11
            X11::Xext
            Freetype::Freetype
            asound
            dl
            Threads::Threads
        )
    endif()
endif()
