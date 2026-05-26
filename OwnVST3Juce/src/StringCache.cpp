#include "StringCache.h"

const char* StringCache::store(const std::string& key, const std::string& value)
{
    std::lock_guard<std::mutex> lock(_mutex);
    _cache[key] = value;
    return _cache.at(key).c_str();
}

const char* StringCache::get(const std::string& key) const
{
    std::lock_guard<std::mutex> lock(_mutex);
    const auto it = _cache.find(key);
    return (it != _cache.cend()) ? it->second.c_str() : "";
}

void StringCache::clear()
{
    std::lock_guard<std::mutex> lock(_mutex);
    _cache.clear();
}
