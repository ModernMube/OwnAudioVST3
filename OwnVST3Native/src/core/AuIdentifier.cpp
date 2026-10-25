#include "AuIdentifier.h"

namespace
{
    bool parseCode(const std::string& s, uint32_t& out)
    {
        if (s.size() != 4) return false;

        out = 0;
        for (char c : s) out = (out << 8) | static_cast<uint8_t>(c);
        return true;
    }
}

std::string ownvst3_fourCCString(uint32_t code)
{
    const char s[4] = { static_cast<char>((code >> 24) & 0xff), static_cast<char>((code >> 16) & 0xff),
                        static_cast<char>((code >> 8) & 0xff),  static_cast<char>(code & 0xff) };
    return std::string(s, 4);
}

std::string ownvst3_auCategoryFolder(uint32_t type)
{
    switch (type)
    {
        case ownvst3_fourCC("aumu"): return "Synths/";
        case ownvst3_fourCC("aumf"):
        case ownvst3_fourCC("aufx"): return "Effects/";
        case ownvst3_fourCC("augn"): return "Generators/";
        case ownvst3_fourCC("aupn"): return "Panners/";
        case ownvst3_fourCC("aumx"): return "Mixers/";
        case ownvst3_fourCC("aumi"): return "MidiEffects/";
        default:                     return "";
    }
}

std::string ownvst3_makeAuIdentifier(const AuComponentId& id)
{
    return "AudioUnit:" + ownvst3_auCategoryFolder(id.type)
         + ownvst3_fourCCString(id.type) + ","
         + ownvst3_fourCCString(id.subtype) + ","
         + ownvst3_fourCCString(id.manufacturer);
}

bool ownvst3_parseAuIdentifier(const std::string& identifier, AuComponentId& out)
{
    static const std::string prefix = "AudioUnit:";
    if (identifier.compare(0, prefix.size(), prefix) != 0) return false;

    std::string rest = identifier.substr(prefix.size());
    const auto slash = rest.rfind('/');
    if (slash != std::string::npos) rest = rest.substr(slash + 1);

    const auto c1 = rest.find(',');
    const auto c2 = c1 == std::string::npos ? std::string::npos : rest.find(',', c1 + 1);
    if (c2 == std::string::npos) return false;

    return parseCode(rest.substr(0, c1), out.type)
        && parseCode(rest.substr(c1 + 1, c2 - c1 - 1), out.subtype)
        && parseCode(rest.substr(c2 + 1), out.manufacturer);
}
