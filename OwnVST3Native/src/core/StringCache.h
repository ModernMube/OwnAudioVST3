#pragma once

#include <mutex>
#include <string>
#include <unordered_map>

// keeps the strings we hand out over P/Invoke alive
class StringCache
{
public:
    const char* store(const std::string& key, const std::string& value);

    const char* get(const std::string& key) const;

    void clear();

private:
    mutable std::mutex                            _mutex;
    std::unordered_map<std::string, std::string>  _cache;
};
