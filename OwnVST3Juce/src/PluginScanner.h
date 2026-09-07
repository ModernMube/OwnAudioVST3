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
 * Process-wide plugin discovery across every format JUCE was built with.
 * The scan runs on its own thread, never the JUCE message thread — JUCE silently
 * skips plugins needing an unblocked message thread when called from it.
 */
class PluginScanner
{
public:
    static PluginScanner& instance();

    bool  start(int formatMask, int mode);
    bool  resolve(const char* identifier, PluginDescriptorC* out);

    bool  isRunning() const noexcept   { return _running.load(std::memory_order_acquire); }
    float progress() const noexcept;
    void  cancel() noexcept            { _cancelled.store(true, std::memory_order_release); }

    const char* currentItem() const;

    int  resultCount() const;
    bool resultAt(int index, PluginDescriptorC* out) const;

    const char* cacheXml();
    bool        restoreCache(const char* xml);

private:
    PluginScanner() = default;
    ~PluginScanner();

    /** Owns its strings so the C pointers handed across P/Invoke stay put. */
    struct Entry
    {
        std::string name, vendor, version, category, identifier, formatName, filePath;
        int isInstrument = 0, numInputs = -1, numOutputs = -1, uniqueId = 0;
    };

    void _scanWorker(int formatMask, int mode);
    void _fastScan(int formatMask);
    void _fullScan(int formatMask);
    void _addDescription(const juce::PluginDescription& desc);
    void _rebuildEntries(const juce::Array<juce::PluginDescription>& types);
    void _sortEntries();
    void _joinWorker();

    static juce::PluginDescription _describeVst3Bundle(const juce::String& path);
    static void _copyOut(const Entry& e, PluginDescriptorC* out);

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
