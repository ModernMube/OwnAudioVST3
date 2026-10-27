cmake_minimum_required(VERSION 3.22)

# iOS: -DCMAKE_SYSTEM_NAME=iOS. AudioUnits only (no VST3 on iOS), built as ownvst3.framework.
if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
    set(OWNVST3_IOS ON)
endif()

if(NOT DEFINED CMAKE_OSX_DEPLOYMENT_TARGET)
    if(OWNVST3_IOS)
        set(CMAKE_OSX_DEPLOYMENT_TARGET "15.0" CACHE STRING "iOS deployment target")
    else()
        set(CMAKE_OSX_DEPLOYMENT_TARGET "11.0" CACHE STRING "macOS deployment target")
    endif()
endif()

project(ownvst3
    VERSION 2.0.0
    DESCRIPTION "VST3 / AudioUnit host library exposing a stable C API for .NET P/Invoke."
    LANGUAGES C CXX
)

if(APPLE)
    enable_language(OBJCXX)
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_OBJCXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

option(OWNVST3_BUILD_TESTS "Build the native unit tests" OFF)

# Every dependency is MIT compatible: VST3 SDK (MIT since 3.8.0), pugixml (MIT).
# Point FETCHCONTENT_SOURCE_DIR_VST3SDK / _PUGIXML at local checkouts to build offline.
include(FetchContent)

if(NOT OWNVST3_IOS)
    FetchContent_Declare(vst3sdk
        GIT_REPOSITORY https://github.com/steinbergmedia/vst3sdk.git
        GIT_TAG        v3.8.1_build_84
        GIT_SHALLOW    TRUE
        GIT_SUBMODULES base pluginterfaces public.sdk cmake
        SOURCE_SUBDIR  do-not-add
    )
    FetchContent_MakeAvailable(vst3sdk)
    include(cmake/Vst3Sdk.cmake)
endif()

FetchContent_Declare(pugixml
    GIT_REPOSITORY https://github.com/zeux/pugixml.git
    GIT_TAG        v1.15
    GIT_SHALLOW    TRUE
    SOURCE_SUBDIR  do-not-add
)
FetchContent_MakeAvailable(pugixml)

# ─── Portable core (no SDK, no OS UI) – shared with the unit tests ────────────

add_library(ownvst3_core STATIC
    src/core/StateCodec.cpp
    src/core/AuIdentifier.cpp
    src/core/StringCache.cpp
    src/scan/CacheXml.cpp
    ${pugixml_SOURCE_DIR}/src/pugixml.cpp
)
target_include_directories(ownvst3_core PUBLIC src include ${pugixml_SOURCE_DIR}/src)
target_compile_definitions(ownvst3_core PUBLIC PUGIXML_NO_EXCEPTIONS)

# ─── The shared library ───────────────────────────────────────────────────────

add_library(ownvst3 SHARED
    src/Exports.cpp
    src/core/PluginInstance.cpp
    src/core/BackendFactory.cpp
    src/scan/Scanner.cpp
    src/au/AudioUnitRegistry.cpp
)
target_include_directories(ownvst3 PRIVATE include src)
target_link_libraries(ownvst3 PRIVATE ownvst3_core)

if(NOT OWNVST3_IOS)
    target_sources(ownvst3 PRIVATE
        src/vst3/Vst3Backend.cpp
        src/vst3/HostContext.cpp
        src/vst3/MidiToVst.cpp
        src/scan/Vst3ModuleInfo.cpp
    )
    target_link_libraries(ownvst3 PRIVATE ownvst3_vst3sdk)
endif()

if(OWNVST3_IOS)
    # A dynamic framework: the C# side looks exports up by name, which a stripped
    # static archive linked into the app would not survive.
    target_sources(ownvst3 PRIVATE
        src/ui/mac/UiThreadMac.mm
        src/au/AuBackend.mm
    )
    set_source_files_properties(src/ui/mac/UiThreadMac.mm src/au/AuBackend.mm
        PROPERTIES COMPILE_FLAGS "-fobjc-arc")
    target_compile_definitions(ownvst3 PRIVATE OWNVST3_NO_VST3)
    # App Store validation wants MinimumOSVersion and the platform in an embedded framework.
    if(CMAKE_OSX_SYSROOT MATCHES "[Ss]imulator")
        set(OWNVST3_IOS_PLATFORM "iPhoneSimulator")
    else()
        set(OWNVST3_IOS_PLATFORM "iPhoneOS")
    endif()
    configure_file(cmake/iOSFramework.plist.in ${CMAKE_CURRENT_BINARY_DIR}/ownvst3-Info.plist @ONLY)

    set_target_properties(ownvst3 PROPERTIES
        FRAMEWORK                   TRUE
        OUTPUT_NAME                 "ownvst3"
        MACOSX_FRAMEWORK_IDENTIFIER "com.modernmube.ownvst3"
        MACOSX_FRAMEWORK_INFO_PLIST ${CMAKE_CURRENT_BINARY_DIR}/ownvst3-Info.plist
    )
    target_link_libraries(ownvst3 PRIVATE
        "-framework UIKit"
        "-framework CoreAudioKit"
        "-framework AudioToolbox"
        "-framework AVFoundation"
        "-framework CoreFoundation"
        "-framework Foundation"
    )

elseif(WIN32)
    target_sources(ownvst3 PRIVATE
        src/ui/win32/UiThreadWin.cpp
        src/ui/win32/EditorWindowWin.cpp
    )
    target_compile_definitions(ownvst3 PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN UNICODE _UNICODE)
    target_link_options(ownvst3 PRIVATE "/DEF:${CMAKE_CURRENT_SOURCE_DIR}/ownvst3.def")
    target_link_libraries(ownvst3 PRIVATE ole32 user32 gdi32)
    set_target_properties(ownvst3 PROPERTIES OUTPUT_NAME "ownvst3" PREFIX "" SUFFIX ".dll")
    if(MSVC)
        target_compile_options(ownvst3 PRIVATE /EHa)
    endif()

elseif(APPLE)
    target_sources(ownvst3 PRIVATE
        src/ui/mac/UiThreadMac.mm
        src/ui/mac/EditorWindowMac.mm
        src/au/AuBackend.mm
    )
    set_source_files_properties(
        src/ui/mac/UiThreadMac.mm
        src/ui/mac/EditorWindowMac.mm
        src/au/AuBackend.mm
        PROPERTIES COMPILE_FLAGS "-fobjc-arc"
    )
    set_target_properties(ownvst3 PROPERTIES
        MACOSX_RPATH ON
        OUTPUT_NAME  "ownvst3"
        PREFIX       "lib"
        SUFFIX       ".dylib"
    )
    target_link_libraries(ownvst3 PRIVATE
        "-framework Cocoa"
        "-framework AudioToolbox"
        "-framework AVFoundation"
        "-framework CoreAudioKit"
        "-framework CoreFoundation"
    )

elseif(UNIX)
    target_sources(ownvst3 PRIVATE
        src/ui/linux/UiThreadLinux.cpp
        src/ui/linux/EditorWindowX11.cpp
        src/ui/linux/RunLoopLinux.cpp
    )
    set_target_properties(ownvst3 PROPERTIES OUTPUT_NAME "ownvst3" PREFIX "lib" SUFFIX ".so")
    find_package(Threads REQUIRED)
    if(CMAKE_CROSSCOMPILING)
        # Ubuntu multiarch: the arm64 X11 stubs live in /usr/lib/aarch64-linux-gnu.
        target_link_directories(ownvst3 PRIVATE /usr/lib/aarch64-linux-gnu)
        target_link_libraries(ownvst3 PRIVATE X11 dl Threads::Threads)
    else()
        find_package(X11 REQUIRED)
        target_link_libraries(ownvst3 PRIVATE X11::X11 dl Threads::Threads)
    endif()
    target_link_options(ownvst3 PRIVATE "-Wl,--exclude-libs,ALL" "-Wl,--no-undefined")
endif()

# ─── Native unit tests ────────────────────────────────────────────────────────

if(OWNVST3_BUILD_TESTS AND NOT OWNVST3_IOS)
    enable_testing()
    add_executable(ownvst3_tests tests/NativeTests.cpp src/vst3/MidiToVst.cpp)
    target_link_libraries(ownvst3_tests PRIVATE ownvst3_core ownvst3_vst3sdk)
    add_test(NAME ownvst3_tests COMMAND ownvst3_tests)
endif()
