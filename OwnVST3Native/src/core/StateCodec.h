#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

/**
 * Plugin state blobs as projects already store them (see the JUCE removal plan, 5.1):
 *   - VST3, written by 1.6.0 … today: "VC2!" + uint32 LE length + UTF-8 XML + '\0', where the XML
 *     is <VST3PluginState><IComponent>…</IComponent><IEditController>…</IEditController></VST3PluginState>
 *     and each child holds "<byteCount>.<chars>" in the 6-bit, low-bit-first base64 below;
 *   - VST3, written by ≤ 1.5.21: the raw component (or controller) stream, no header;
 *   - AudioUnit: a property list, binary when written, XML accepted.
 */
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

    /** False when data is not a VC2! container (then it is a raw ≤ 1.5.21 stream) or is damaged. */
    bool readVst3(const uint8_t* data, size_t size, Vst3State& out);

    bool hasVst3Header(const uint8_t* data, size_t size) noexcept;

    /** "bplist00…" or an XML plist. */
    bool looksLikePlist(const uint8_t* data, size_t size) noexcept;
}
