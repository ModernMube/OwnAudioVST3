#pragma once

#include "CacheXml.h"
#include "../../include/ownvst3_exports.h"

#include <array>
#include <atomic>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

// fast mode loads nothing, full mode loads every plugin on the UI thread
class Scanner
{
public:
    static Scanner& instance();

    bool  start(int formatMask, int mode);
    bool  resolve(const char* identifier, PluginDescriptorC* out);

    bool  isRunning() const noexcept { return _running.load(std::memory_order_acquire); }
    float progress() const noexcept;
    void  cancel() noexcept          { _cancelled.store(true, std::memory_order_release); }

    const char* currentItem() const;

    int  resultCount() const;
    bool resultAt(int index, PluginDescriptorC* out) const;

    const char* cacheXml();
    bool        restoreCache(const char* xml);

private:
    Scanner() = default;
    ~Scanner();

    struct Entry
    {
        PluginRecord record;
        std::string  filePath;
    };

    void worker(int formatMask, int mode);
    void fastScan(int formatMask);
    void fullScan(int formatMask);
    void add(const PluginRecord& r);
    void sortEntries();
    void joinWorker();
    void setCurrentItem(const std::string& text);

    static bool describe(const std::string& identifier, PluginRecord& out);
    static void copyOut(const Entry& e, PluginDescriptorC* out);

    mutable std::mutex                  _mutex;
    std::vector<std::unique_ptr<Entry>> _entries;
    std::vector<std::unique_ptr<Entry>> _retired;
    std::string                         _cacheXml;

    std::array<char, 512> _currentItem { '\0' };

    std::thread       _worker;
    std::atomic<bool> _running   { false };
    std::atomic<bool> _cancelled { false };
    std::atomic<int>  _done      { 0 };
    std::atomic<int>  _total     { 0 };
};
