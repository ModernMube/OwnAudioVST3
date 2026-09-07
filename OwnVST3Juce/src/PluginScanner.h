#pragma once

#include "JuceHeader.h"
#include "../include/ownvst3_exports.h"

#include <atomic>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

/**
 * Process-wide plugin discovery across every format JUCE was compiled with.
 *
 * Exists because AudioUnits cannot be found by walking directories the way VST3
 * bundles can — they live in the AudioComponent registry and are addressed by an
 * "AudioUnit:Type/subtype,manufacturer" identifier.  The VST3 side of the host is
 * untouched by this class; it is an additional entry point, not a replacement.
 *
 * Threading: the scan runs on its own std::thread, never on the JUCE message
 * thread — JUCE's findAllTypesForFile() silently skips plugins that need an
 * unblocked message thread when called from it.
 */
class PluginScanner
{
public:
    static PluginScanner& instance();

    bool  start(int formatMask);
    bool  isRunning() const noexcept   { return _running.load(std::memory_order_acquire); }
    float progress() const noexcept;
    void  cancel() noexcept            { _cancelled.store(true, std::memory_order_release); }

    /** Name of whatever is being probed right now, for a progress label. */
    const char* currentItem() const;

    int  resultCount() const;
    bool resultAt(int index, PluginDescriptorC* out) const;

    const char* cacheXml();
    bool        restoreCache(const char* xml);

private:
    PluginScanner() = default;
    ~PluginScanner();

    /** One row of the result list; owns its strings so the C pointers stay put. */
    struct Entry
    {
        std::string name, vendor, version, category, identifier, formatName, filePath;
        int isInstrument = 0, numInputs = 0, numOutputs = 0, uniqueId = 0;
    };

    void _scanWorker(int formatMask);
    void _rebuildEntries(const juce::Array<juce::PluginDescription>& types);
    void _joinWorker();

    mutable std::mutex                   _mutex;
    juce::KnownPluginList                _list;
    std::vector<std::unique_ptr<Entry>>  _entries;
    std::string                          _cacheXml;
    std::string                          _currentItem;

    std::thread        _worker;
    std::atomic<bool>  _running   { false };
    std::atomic<bool>  _cancelled { false };
    std::atomic<int>   _done      { 0 };
    std::atomic<int>   _total     { 0 };
};
