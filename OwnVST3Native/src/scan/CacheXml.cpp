#include "CacheXml.h"

#include <pugixml.hpp>

#include <cctype>
#include <cstdio>
#include <cstdlib>
#include <sstream>

namespace
{
    std::string hex(int32_t v)
    {
        char buf[16];
        std::snprintf(buf, sizeof(buf), "%x", static_cast<uint32_t>(v));
        return buf;
    }

    int32_t parseHex(const char* s)
    {
        return static_cast<int32_t>(static_cast<uint32_t>(std::strtoul(s, nullptr, 16)));
    }
}

int32_t ownvst3_hash31(const uint8_t* data, size_t size)
{
    uint32_t v = 0;
    for (size_t i = 0; i < size; ++i)
        v = v * 31u + static_cast<uint32_t>(static_cast<int32_t>(static_cast<int8_t>(data[i])));
    return static_cast<int32_t>(v);
}

bool ownvst3_isAbsolutePath(const std::string& s)
{
    if (s.empty()) return false;
    if (s[0] == '/' || s[0] == '\\') return true;
    return s.size() > 2 && std::isalpha(static_cast<unsigned char>(s[0])) && s[1] == ':' && (s[2] == '\\' || s[2] == '/');
}

std::string ownvst3_writeCacheXml(const std::vector<PluginRecord>& records)
{
    pugi::xml_document doc;
    auto decl = doc.append_child(pugi::node_declaration);
    decl.append_attribute("version")  = "1.0";
    decl.append_attribute("encoding") = "UTF-8";

    auto root = doc.append_child("KNOWNPLUGINS");
    for (const auto& r : records)
    {
        auto p = root.append_child("PLUGIN");
        p.append_attribute("name")            = r.name.c_str();
        p.append_attribute("format")          = r.formatName.c_str();
        p.append_attribute("category")        = r.category.c_str();
        p.append_attribute("manufacturer")    = r.vendor.c_str();
        p.append_attribute("version")         = r.version.c_str();
        p.append_attribute("file")            = r.identifier.c_str();
        p.append_attribute("uniqueId")        = hex(r.uniqueId).c_str();
        p.append_attribute("isInstrument")    = r.isInstrument ? 1 : 0;
        p.append_attribute("numInputs")       = r.numInputs;
        p.append_attribute("numOutputs")      = r.numOutputs;
        p.append_attribute("isShell")         = 0;
        p.append_attribute("hasARAExtension") = 0;
        p.append_attribute("uid")             = hex(r.uniqueId).c_str();
    }

    std::ostringstream out;
    doc.save(out, "  ", pugi::format_default, pugi::encoding_utf8);
    return out.str();
}

bool ownvst3_readCacheXml(const char* xml, std::vector<PluginRecord>& out)
{
    if (!xml || !*xml) return false;

    pugi::xml_document doc;
    if (!doc.load_string(xml)) return false;

    auto root = doc.child("KNOWNPLUGINS");
    if (!root) return false;

    out.clear();
    for (auto p : root.children("PLUGIN"))
    {
        PluginRecord r;
        r.name         = p.attribute("name").as_string();
        r.formatName   = p.attribute("format").as_string();
        r.category     = p.attribute("category").as_string();
        r.vendor       = p.attribute("manufacturer").as_string();
        r.version      = p.attribute("version").as_string();
        r.identifier   = p.attribute("file").as_string();
        r.isInstrument = p.attribute("isInstrument").as_int() != 0;
        r.numInputs    = p.attribute("numInputs").as_int(0);
        r.numOutputs   = p.attribute("numOutputs").as_int(0);
        r.uniqueId     = parseHex(p.attribute("uniqueId").as_string("0"));

        if (r.identifier.empty() || r.formatName.empty()) continue;
        out.push_back(std::move(r));
    }

    return true;
}
