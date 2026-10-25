#include "StateCodec.h"

#include <pugixml.hpp>

#include <cctype>
#include <cstring>
#include <sstream>

namespace
{
    const char kAlphabet[] = ".ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+";

    int alphabetIndex(char c)
    {
        const char* p = std::strchr(kAlphabet, c);
        return (p && c != '\0') ? static_cast<int>(p - kAlphabet) : -1;
    }

    void writeU32(std::vector<uint8_t>& out, size_t at, uint32_t v)
    {
        out[at + 0] = static_cast<uint8_t>(v);
        out[at + 1] = static_cast<uint8_t>(v >> 8);
        out[at + 2] = static_cast<uint8_t>(v >> 16);
        out[at + 3] = static_cast<uint8_t>(v >> 24);
    }

    uint32_t readU32(const uint8_t* p)
    {
        return static_cast<uint32_t>(p[0]) | (static_cast<uint32_t>(p[1]) << 8)
             | (static_cast<uint32_t>(p[2]) << 16) | (static_cast<uint32_t>(p[3]) << 24);
    }
}

namespace StateCodec
{
    std::string encodeBase64Lsb(const uint8_t* data, size_t size)
    {
        const size_t bits  = size * 8;
        const size_t chars = (bits + 5) / 6;

        std::string out = std::to_string(size);
        out.reserve(out.size() + 1 + chars);
        out.push_back('.');

        for (size_t i = 0; i < chars; ++i)
        {
            int v = 0;
            for (int b = 0; b < 6; ++b)
            {
                const size_t pos = i * 6 + static_cast<size_t>(b);
                if (pos < bits && ((data[pos >> 3] >> (pos & 7)) & 1))
                    v |= 1 << b;
            }
            out.push_back(kAlphabet[v]);
        }

        return out;
    }

    bool decodeBase64Lsb(const std::string& text, std::vector<uint8_t>& out)
    {
        const auto dot = text.find('.');
        if (dot == std::string::npos || dot == 0) return false;

        size_t size = 0;
        for (size_t i = 0; i < dot; ++i)
        {
            const char c = text[i];
            if (std::isspace(static_cast<unsigned char>(c))) continue;
            if (c < '0' || c > '9') return false;
            size = size * 10 + static_cast<size_t>(c - '0');
            if (size > (1u << 30)) return false;
        }

        out.assign(size, 0);
        const size_t bits = size * 8;
        size_t pos = 0;

        for (size_t i = dot + 1; i < text.size(); ++i)
        {
            const char c = text[i];
            if (std::isspace(static_cast<unsigned char>(c))) continue;

            const int v = alphabetIndex(c);
            if (v < 0) return false;

            for (int b = 0; b < 6; ++b, ++pos)
                if (pos < bits && ((v >> b) & 1))
                    out[pos >> 3] |= static_cast<uint8_t>(1u << (pos & 7));
        }

        return true;
    }

    std::vector<uint8_t> writeVst3(const Vst3State& state)
    {
        pugi::xml_document doc;
        auto root = doc.append_child("VST3PluginState");

        if (state.hasComponent)
            root.append_child("IComponent").text().set(
                encodeBase64Lsb(state.component.data(), state.component.size()).c_str());
        if (state.hasController)
            root.append_child("IEditController").text().set(
                encodeBase64Lsb(state.controller.data(), state.controller.size()).c_str());

        std::ostringstream xml;
        xml << "<?xml version=\"1.0\" encoding=\"UTF-8\"?> ";
        doc.save(xml, "", pugi::format_raw | pugi::format_no_declaration, pugi::encoding_utf8);
        const std::string text = xml.str();

        std::vector<uint8_t> out(8 + text.size() + 1, 0);
        writeU32(out, 0, kVst3Magic);
        writeU32(out, 4, static_cast<uint32_t>(text.size()));
        std::memcpy(out.data() + 8, text.data(), text.size());
        return out;
    }

    bool hasVst3Header(const uint8_t* data, size_t size) noexcept
    {
        return data && size >= 8 && readU32(data) == kVst3Magic;
    }

    bool readVst3(const uint8_t* data, size_t size, Vst3State& out)
    {
        if (!hasVst3Header(data, size)) return false;

        size_t length = readU32(data + 4);
        if (length > size - 8) length = size - 8;
        while (length > 0 && data[8 + length - 1] == '\0') --length;

        pugi::xml_document doc;
        if (!doc.load_buffer(data + 8, length, pugi::parse_default, pugi::encoding_utf8))
            return false;

        auto root = doc.child("VST3PluginState");
        if (!root) return false;

        out = Vst3State{};

        if (auto c = root.child("IComponent"))
        {
            if (!decodeBase64Lsb(c.text().as_string(), out.component)) return false;
            out.hasComponent = true;
        }
        if (auto e = root.child("IEditController"))
        {
            if (!decodeBase64Lsb(e.text().as_string(), out.controller)) return false;
            out.hasController = true;
        }

        return true;
    }

    bool looksLikePlist(const uint8_t* data, size_t size) noexcept
    {
        if (!data || size < 8) return false;
        if (std::memcmp(data, "bplist00", 8) == 0) return true;

        size_t i = 0;
        if (size >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) i = 3;
        while (i < size && std::isspace(data[i])) ++i;

        static const char decl[] = "<?xml";
        static const char plist[] = "<plist";
        return (size - i >= 5 && std::memcmp(data + i, decl, 5) == 0)
            || (size - i >= 6 && std::memcmp(data + i, plist, 6) == 0);
    }
}
