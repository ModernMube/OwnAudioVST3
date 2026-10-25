#pragma once

#include <cstdint>
#include <string>

struct AuComponentId
{
    uint32_t type = 0, subtype = 0, manufacturer = 0;
};

constexpr uint32_t ownvst3_fourCC(const char (&s)[5])
{
    return (static_cast<uint32_t>(static_cast<uint8_t>(s[0])) << 24)
         | (static_cast<uint32_t>(static_cast<uint8_t>(s[1])) << 16)
         | (static_cast<uint32_t>(static_cast<uint8_t>(s[2])) << 8)
         |  static_cast<uint32_t>(static_cast<uint8_t>(s[3]));
}

std::string ownvst3_fourCCString(uint32_t code);

// "Synths/", "Effects/" or ""
std::string ownvst3_auCategoryFolder(uint32_t type);

// "AudioUnit:Effects/aufx,dely,appl"
std::string ownvst3_makeAuIdentifier(const AuComponentId& id);

bool ownvst3_parseAuIdentifier(const std::string& identifier, AuComponentId& out);
