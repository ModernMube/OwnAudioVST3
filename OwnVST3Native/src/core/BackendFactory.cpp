#include "BackendFactory.h"
#include "../vst3/Vst3Backend.h"

#if defined(__APPLE__)
#include "../au/AuBackend.h"
#endif

#include <algorithm>
#include <cctype>

namespace
{
    bool endsWithNoCase(const std::string& s, const char* suffix)
    {
        const std::string tail(suffix);
        if (s.size() < tail.size()) return false;

        return std::equal(tail.rbegin(), tail.rend(), s.rbegin(), [](char a, char b) {
            return std::tolower(static_cast<unsigned char>(a)) == std::tolower(static_cast<unsigned char>(b));
        });
    }

    std::string withoutTrailingSlash(std::string s)
    {
        while (s.size() > 1 && (s.back() == '/' || s.back() == '\\')) s.pop_back();
        return s;
    }
}

PluginKind ownvst3_kindOf(const std::string& identifier)
{
    const auto id = withoutTrailingSlash(identifier);

    if (id.rfind("AudioUnit:", 0) == 0 || endsWithNoCase(id, ".component"))
        return PluginKind::AudioUnit;
    if (endsWithNoCase(id, ".vst3"))
        return PluginKind::Vst3;
    return PluginKind::Unknown;
}

std::unique_ptr<IPluginBackend> ownvst3_createBackend(const std::string& identifier, int subIndex)
{
    switch (ownvst3_kindOf(identifier))
    {
        case PluginKind::Vst3:
            return Vst3Backend::load(withoutTrailingSlash(identifier), subIndex);
#if defined(__APPLE__)
        case PluginKind::AudioUnit:
            return ownvst3_createAuBackend(withoutTrailingSlash(identifier), subIndex);
#endif
        default:
            return nullptr;
    }
}
