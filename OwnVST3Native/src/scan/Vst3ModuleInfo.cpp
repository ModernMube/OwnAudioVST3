#include "Vst3ModuleInfo.h"

#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/moduleinfo/moduleinfoparser.h"
#include "public.sdk/source/vst/utility/uid.h"

#include <algorithm>
#include <fstream>
#include <sstream>

namespace
{
    std::string stem(const std::string& path)
    {
        auto p = path;
        while (!p.empty() && (p.back() == '/' || p.back() == '\\')) p.pop_back();

        const auto slash = p.find_last_of("/\\");
        if (slash != std::string::npos) p = p.substr(slash + 1);

        const auto dot = p.rfind('.');
        return dot == std::string::npos ? p : p.substr(0, dot);
    }
}

bool ownvst3_applyModuleInfo(const std::string& json, PluginRecord& record)
{
    auto info = Steinberg::ModuleInfoLib::parseJson(json, nullptr);
    if (!info) return false;

    std::vector<const Steinberg::ModuleInfo::ClassInfo*> audio;
    for (const auto& c : info->classes)
        if (c.category == kVstAudioEffectClass) audio.push_back(&c);
    if (audio.empty()) return false;

    const auto& cls = *audio.front();

    if (audio.size() == 1 && !cls.name.empty()) record.name = cls.name;
    record.vendor  = cls.vendor.empty() ? info->factoryInfo.vendor : cls.vendor;
    record.version = cls.version;

    std::string cat;
    for (const auto& s : cls.subCategories) cat += (cat.empty() ? "" : "|") + s;
    record.category     = cat;
    record.isInstrument = std::find(cls.subCategories.begin(), cls.subCategories.end(), "Instrument") != cls.subCategories.end();

    if (auto uid = VST3::UID::fromString(cls.cid))
        record.uniqueId = ownvst3_hash31(reinterpret_cast<const uint8_t*>(uid->data()), 16);

    return true;
}

PluginRecord ownvst3_describeVst3Bundle(const std::string& bundlePath)
{
    PluginRecord r;
    r.name       = stem(bundlePath);
    r.identifier = bundlePath;
    r.formatName = "VST3";

    if (auto path = VST3::Hosting::Module::getModuleInfoPath(bundlePath))
    {
        std::ifstream in(*path, std::ios::binary);
        std::stringstream text;
        text << in.rdbuf();
        ownvst3_applyModuleInfo(text.str(), r);
    }

    return r;
}
