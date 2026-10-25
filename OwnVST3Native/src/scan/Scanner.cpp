#include "Scanner.h"
#include "Vst3ModuleInfo.h"
#include "au/AudioUnitRegistry.h"
#include "core/BackendFactory.h"

#include "public.sdk/source/vst/hosting/module.h"

#include <algorithm>
#include <cstring>

namespace
{
    int formatBit(const std::string& formatName)
    {
        if (formatName == "VST3")      return OWNPLUGIN_FORMAT_VST3;
        if (formatName == "AudioUnit") return OWNPLUGIN_FORMAT_AUDIOUNIT;
        return 0;
    }
}

Scanner& Scanner::instance()
{
    static Scanner scanner;
    return scanner;
}

Scanner::~Scanner()
{
    cancel();
    joinWorker();
}

void Scanner::joinWorker()
{
    if (_worker.joinable()) _worker.join();
}

float Scanner::progress() const noexcept
{
    const int total = _total.load(std::memory_order_relaxed);
    if (total <= 0) return _running.load(std::memory_order_relaxed) ? 0.0f : 1.0f;

    return std::clamp(static_cast<float>(_done.load(std::memory_order_relaxed)) / total, 0.0f, 1.0f);
}

const char* Scanner::currentItem() const
{
    return _currentItem.data();
}

void Scanner::setCurrentItem(const std::string& text)
{
    const auto n = std::min(text.size(), _currentItem.size() - 1u);
    _currentItem[n] = '\0';
    std::memcpy(_currentItem.data(), text.data(), n);
}

bool Scanner::start(int formatMask, int mode)
{
    if (_running.exchange(true, std::memory_order_acq_rel))
        return false;

    joinWorker();

    _cancelled.store(false, std::memory_order_release);
    _done.store(0, std::memory_order_release);
    _total.store(0, std::memory_order_release);

    {
        std::lock_guard<std::mutex> lock(_mutex);
        _entries.clear();
        _retired.clear();
        setCurrentItem({});
    }

    _worker = std::thread(&Scanner::worker, this, formatMask, mode);
    return true;
}

void Scanner::worker(int formatMask, int mode)
{
    if (mode == OWNPLUGIN_SCAN_FULL) fullScan(formatMask);
    else                             fastScan(formatMask);

    {
        std::lock_guard<std::mutex> lock(_mutex);
        setCurrentItem({});
        sortEntries();
    }

    _running.store(false, std::memory_order_release);
}

void Scanner::fastScan(int formatMask)
{
    if (formatMask & OWNPLUGIN_FORMAT_AUDIOUNIT)
    {
        for (const auto& au : ownvst3_listAudioUnits())
        {
            PluginRecord r;
            r.name         = au.name;
            r.vendor       = au.vendor;
            r.version      = au.version;
            r.category     = au.category;
            r.identifier   = au.identifier;
            r.formatName   = "AudioUnit";
            r.isInstrument = au.isInstrument;

            std::lock_guard<std::mutex> lock(_mutex);
            add(r);
        }
    }

    if (formatMask & OWNPLUGIN_FORMAT_VST3)
    {
        for (const auto& path : VST3::Hosting::Module::getModulePaths())
        {
            if (_cancelled.load(std::memory_order_acquire)) break;

            const auto r = ownvst3_describeVst3Bundle(path);
            std::lock_guard<std::mutex> lock(_mutex);
            add(r);
        }
    }

    _total.store(1, std::memory_order_release);
    _done.store(1, std::memory_order_release);
}

void Scanner::fullScan(int formatMask)
{
    std::vector<std::string> jobs;

    if (formatMask & OWNPLUGIN_FORMAT_AUDIOUNIT)
        for (const auto& au : ownvst3_listAudioUnits()) jobs.push_back(au.identifier);
    if (formatMask & OWNPLUGIN_FORMAT_VST3)
        for (const auto& path : VST3::Hosting::Module::getModulePaths()) jobs.push_back(path);

    _total.store(static_cast<int>(jobs.size()), std::memory_order_release);

    for (size_t i = 0; i < jobs.size(); ++i)
    {
        if (_cancelled.load(std::memory_order_acquire)) break;

        {
            std::lock_guard<std::mutex> lock(_mutex);
            setCurrentItem(jobs[i]);
        }

        PluginRecord r;
        if (describe(jobs[i], r))
        {
            std::lock_guard<std::mutex> lock(_mutex);
            add(r);
        }

        _done.store(static_cast<int>(i) + 1, std::memory_order_release);
    }
}

bool Scanner::describe(const std::string& identifier, PluginRecord& out)
{
    auto backend = ownvst3_createBackend(identifier, 0);
    if (!backend) return false;

    const auto& m = backend->meta();
    out.name         = m.name;
    out.vendor       = m.vendor;
    out.version      = m.version;
    out.category     = m.category;
    out.identifier   = backend->identifier();
    out.formatName   = backend->formatName();
    out.numInputs    = backend->inputChannels();
    out.numOutputs   = backend->outputChannels();
    out.uniqueId     = m.uniqueId;
    out.isInstrument = (backend->acceptsMidi() && out.numInputs == 0 && out.numOutputs > 0)
                    || m.category.find("Instrument") != std::string::npos
                    || m.category == "Synths";
    return true;
}

bool Scanner::resolve(const char* identifier, PluginDescriptorC* out)
{
    if (!identifier || !out) return false;

    PluginRecord r;
    if (!describe(identifier, r)) return false;

    std::lock_guard<std::mutex> lock(_mutex);
    add(r);
    sortEntries();

    for (const auto& e : _entries)
    {
        if (e->record.identifier == r.identifier)
        {
            copyOut(*e, out);
            return true;
        }
    }
    return false;
}

void Scanner::copyOut(const Entry& e, PluginDescriptorC* out)
{
    out->name         = e.record.name.c_str();
    out->vendor       = e.record.vendor.c_str();
    out->version      = e.record.version.c_str();
    out->category     = e.record.category.c_str();
    out->identifier   = e.record.identifier.c_str();
    out->formatName   = e.record.formatName.c_str();
    out->fileOrPath   = e.filePath.c_str();
    out->isInstrument = e.record.isInstrument ? 1 : 0;
    out->numInputs    = e.record.numInputs;
    out->numOutputs   = e.record.numOutputs;
    out->uniqueId     = e.record.uniqueId;
    out->_reserved    = 0;
}

void Scanner::add(const PluginRecord& r)
{
    auto slot = std::make_unique<Entry>();
    slot->record = r;
    if (ownvst3_isAbsolutePath(r.identifier)) slot->filePath = r.identifier;

    auto it = std::find_if(_entries.begin(), _entries.end(),
                           [&](const auto& e) { return e->record.identifier == r.identifier; });

    if (it == _entries.end())
    {
        _entries.push_back(std::move(slot));
        return;
    }

    _retired.push_back(std::move(*it));
    *it = std::move(slot);
}

void Scanner::sortEntries()
{
    std::stable_sort(_entries.begin(), _entries.end(),
                     [](const auto& a, const auto& b) { return a->record.name < b->record.name; });
}

int Scanner::resultCount() const
{
    std::lock_guard<std::mutex> lock(_mutex);
    return static_cast<int>(_entries.size());
}

bool Scanner::resultAt(int index, PluginDescriptorC* out) const
{
    if (!out) return false;

    std::lock_guard<std::mutex> lock(_mutex);
    if (index < 0 || index >= static_cast<int>(_entries.size())) return false;

    copyOut(*_entries[static_cast<size_t>(index)], out);
    return true;
}

const char* Scanner::cacheXml()
{
    std::lock_guard<std::mutex> lock(_mutex);

    std::vector<PluginRecord> records;
    records.reserve(_entries.size());
    for (const auto& e : _entries) records.push_back(e->record);

    _cacheXml = ownvst3_writeCacheXml(records);
    return _cacheXml.c_str();
}

bool Scanner::restoreCache(const char* xml)
{
    if (!xml || *xml == '\0' || isRunning()) return false;

    std::vector<PluginRecord> records;
    if (!ownvst3_readCacheXml(xml, records)) return false;

    std::lock_guard<std::mutex> lock(_mutex);
    _entries.clear();
    _retired.clear();
    for (const auto& r : records)
        if (formatBit(r.formatName) != 0) add(r);
    sortEntries();
    return true;
}
