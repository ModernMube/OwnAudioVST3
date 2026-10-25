#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

// VST3 1.6+: "VC2!" + u32 LE length + XML, children in low-bit-first base64
// VST3 <= 1.5.21: raw component stream. AU: plist
namespace StateCodec
{
    constexpr uint32_t kVst3Magic = 0x21324356u;

    std::string encodeBase64Lsb(const uint8_t* data, size_t size);
    bool        decodeBase64Lsb(const std::string& text, std::vector<uint8_t>& out);

    struct Vst3State
    {
        bool hasComponent  = false;
        bool hasController = false;
        std::vector<uint8_t> component;
        std::vector<uint8_t> controller;
    };

    std::vector<uint8_t> writeVst3(const Vst3State& state);

    bool readVst3(const uint8_t* data, size_t size, Vst3State& out);

    bool hasVst3Header(const uint8_t* data, size_t size) noexcept;

    bool looksLikePlist(const uint8_t* data, size_t size) noexcept;
}
