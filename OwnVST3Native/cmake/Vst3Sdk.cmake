# The few VST3 SDK sources a host needs, compiled into one static library.
# The SDK's own CMake (smtg_*) is only used for building the sample plugins the tests load.

set(_sdk ${vst3sdk_SOURCE_DIR})

add_library(ownvst3_vst3sdk STATIC
    ${_sdk}/pluginterfaces/base/conststringtable.cpp
    ${_sdk}/pluginterfaces/base/coreiids.cpp
    ${_sdk}/pluginterfaces/base/funknown.cpp
    ${_sdk}/pluginterfaces/base/ustring.cpp
    ${_sdk}/base/source/baseiids.cpp
    ${_sdk}/base/source/fbuffer.cpp
    ${_sdk}/base/source/fdebug.cpp
    ${_sdk}/base/source/fobject.cpp
    ${_sdk}/base/source/fstreamer.cpp
    ${_sdk}/base/source/fstring.cpp
    ${_sdk}/base/source/updatehandler.cpp
    ${_sdk}/base/thread/source/fcondition.cpp
    ${_sdk}/base/thread/source/flock.cpp
    ${_sdk}/public.sdk/source/common/commoniids.cpp
    ${_sdk}/public.sdk/source/common/commonstringconvert.cpp
    ${_sdk}/public.sdk/source/common/memorystream.cpp
    ${_sdk}/public.sdk/source/vst/hosting/eventlist.cpp
    ${_sdk}/public.sdk/source/vst/hosting/hostclasses.cpp
    ${_sdk}/public.sdk/source/vst/hosting/module.cpp
    ${_sdk}/public.sdk/source/vst/hosting/pluginterfacesupport.cpp
    ${_sdk}/public.sdk/source/vst/moduleinfo/moduleinfoparser.cpp
    ${_sdk}/public.sdk/source/vst/utility/stringconvert.cpp
    ${_sdk}/public.sdk/source/vst/vstinitiids.cpp
)

if(WIN32)
    target_sources(ownvst3_vst3sdk PRIVATE ${_sdk}/public.sdk/source/vst/hosting/module_win32.cpp)
elseif(APPLE)
    target_sources(ownvst3_vst3sdk PRIVATE ${_sdk}/public.sdk/source/vst/hosting/module_mac.mm)
    set_source_files_properties(${_sdk}/public.sdk/source/vst/hosting/module_mac.mm
        PROPERTIES COMPILE_FLAGS "-fobjc-arc")
    target_link_libraries(ownvst3_vst3sdk PUBLIC "-framework Foundation" "-framework CoreFoundation")
else()
    target_sources(ownvst3_vst3sdk PRIVATE ${_sdk}/public.sdk/source/vst/hosting/module_linux.cpp)
    target_link_libraries(ownvst3_vst3sdk PUBLIC dl)
endif()

target_include_directories(ownvst3_vst3sdk PUBLIC ${_sdk})
target_compile_definitions(ownvst3_vst3sdk PUBLIC
    $<IF:$<CONFIG:Debug>,DEVELOPMENT=1,RELEASE=1>
)

if(MSVC)
    target_compile_options(ownvst3_vst3sdk PRIVATE /wd4996)
else()
    target_compile_options(ownvst3_vst3sdk PRIVATE -w)
endif()
