#pragma once

#include <mutex>
#include <string>
#include <unordered_map>

/**
 * Thread-safe store for C-string pointers returned across the P/Invoke boundary.
 *
 * The native library returns raw const char* pointers which the .NET runtime
 * reads without taking ownership.  StringCache keeps the backing std::string
 * alive as long as the enclosing PluginInstance lives, ensuring the pointer
 * remains dereferenceable for the duration of any reasonable caller window.
 *
 * Each entry is keyed by a caller-supplied string key so that repeated calls
 * with the same key overwrite the value and keep the map compact rather than
 * growing without bound.
 */
class StringCache
{
public:
    /**
     * Stores value under key and returns a stable pointer to the stored string.
     * If an entry for key already exists it is replaced.
     */
    const char* store(const std::string& key, const std::string& value);

    /** Returns the cached pointer for key, or "" if the key is absent. */
    const char* get(const std::string& key) const;

    /** Removes all cached entries.  Existing pointers become dangling. */
    void clear();

private:
    mutable std::mutex                            _mutex;
    std::unordered_map<std::string, std::string>  _cache;
};
