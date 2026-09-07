#include "PluginScanner.h"
#include "PluginInstance.h"
#include "AudioUnitRegistry.h"

#include <algorithm>

#if defined(_WIN32)
    #define NOMINMAX
    #include <windows.h>
#endif

namespace
{
    int formatBit(const juce::String& formatName)
    {
        if (formatName == "VST3")      return OWNPLUGIN_FORMAT_VST3;
        if (formatName == "AudioUnit") return OWNPLUGIN_FORMAT_AUDIOUNIT;
        return 0;
    }

#if defined(_WIN32)
    // A broken plugin DllMain must not take the scan down with it. No C++ objects
    // with destructors may live in a function containing __try/__except.
    bool probeGuarded(juce::AudioPluginFormat* fmt,
                      const juce::String* id,
                      juce::OwnedArray<juce::PluginDescription>* out)
    {
        __try
        {
            fmt->findAllTypesForFile(*out, *id);
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
    }
#endif
}

PluginScanner& PluginScanner::instance()
{
    static PluginScanner scanner;
    return scanner;
}

PluginScanner::~PluginScanner()
{
    cancel();
    _joinWorker();
}

void PluginScanner::_joinWorker()
{
    if (_worker.joinable())
        _worker.join();
}

float PluginScanner::progress() const noexcept
{
    const int total = _total.load(std::memory_order_relaxed);
    if (total <= 0) { return _running.load(std::memory_order_relaxed) ? 0.0f : 1.0f; }

    return juce::jlimit(0.0f, 1.0f,
                        static_cast<float>(_done.load(std::memory_order_relaxed)) / total);
}

const char* PluginScanner::currentItem() const
{
    std::lock_guard<std::mutex> lock(_mutex);
    return _currentItem.c_str();
}

bool PluginScanner::start(int formatMask, int mode)
{
    if (_running.exchange(true, std::memory_order_acq_rel))
        return false;

    _joinWorker();
    ownvst3_ensureJuceInitialised();

    _cancelled.store(false, std::memory_order_release);
    _done.store(0, std::memory_order_release);
    _total.store(0, std::memory_order_release);

    {
        std::lock_guard<std::mutex> lock(_mutex);
        _list.clear();
        _entries.clear();
        _currentItem.clear();
    }

    _worker = std::thread(&PluginScanner::_scanWorker, this, formatMask, mode);
    return true;
}

void PluginScanner::_scanWorker(int formatMask, int mode)
{
    if (mode == OWNPLUGIN_SCAN_FULL)
        _fullScan(formatMask);
    else
        _fastScan(formatMask);

    {
        std::lock_guard<std::mutex> lock(_mutex);
        _currentItem.clear();
        _sortEntries();
    }

    _running.store(false, std::memory_order_release);
}

juce::PluginDescription PluginScanner::_describeVst3Bundle(const juce::String& path)
{
    juce::PluginDescription desc;
    desc.name              = juce::File(path).getFileNameWithoutExtension();
    desc.fileOrIdentifier  = path;
    desc.pluginFormatName  = "VST3";
    desc.numInputChannels  = -1;
    desc.numOutputChannels = -1;
    return desc;
}

void PluginScanner::_fastScan(int formatMask)
{
    juce::AudioPluginFormatManager formats;
    formats.addDefaultFormats();

    if ((formatMask & OWNPLUGIN_FORMAT_AUDIOUNIT) != 0)
    {
        for (const auto& au : ownvst3_listAudioUnits())
        {
            juce::PluginDescription desc;
            desc.name              = au.name;
            desc.manufacturerName  = au.vendor;
            desc.version           = au.version;
            desc.category          = au.category;
            desc.fileOrIdentifier  = au.identifier;
            desc.pluginFormatName  = "AudioUnit";
            desc.isInstrument      = au.isInstrument;
            desc.numInputChannels  = -1;
            desc.numOutputChannels = -1;

            std::lock_guard<std::mutex> lock(_mutex);
            _addDescription(desc);
        }
    }

    if ((formatMask & OWNPLUGIN_FORMAT_VST3) != 0)
    {
        for (int i = 0; i < formats.getNumFormats(); ++i)
        {
            auto* fmt = formats.getFormat(i);
            if (fmt->getName() != "VST3") continue;

            for (const auto& path : fmt->searchPathsForPlugins(fmt->getDefaultLocationsToSearch(), true, true))
            {
                std::lock_guard<std::mutex> lock(_mutex);
                _addDescription(_describeVst3Bundle(path));
            }
        }
    }

    _total.store(1, std::memory_order_release);
    _done.store(1, std::memory_order_release);
}

void PluginScanner::_fullScan(int formatMask)
{
    juce::AudioPluginFormatManager formats;
    formats.addDefaultFormats();

    struct Job { juce::AudioPluginFormat* format; juce::String identifier; };
    std::vector<Job> jobs;

    for (int i = 0; i < formats.getNumFormats(); ++i)
    {
        auto* fmt = formats.getFormat(i);
        if ((formatBit(fmt->getName()) & formatMask) == 0)
            continue;

        for (const auto& id : fmt->searchPathsForPlugins(fmt->getDefaultLocationsToSearch(), true, true))
            jobs.push_back({ fmt, id });
    }

    _total.store(static_cast<int>(jobs.size()), std::memory_order_release);

    for (size_t i = 0; i < jobs.size(); ++i)
    {
        if (_cancelled.load(std::memory_order_acquire)) break;

        {
            std::lock_guard<std::mutex> lock(_mutex);
            _currentItem = jobs[i].identifier.toStdString();
        }

        juce::OwnedArray<juce::PluginDescription> found;
#if defined(_WIN32)
        probeGuarded(jobs[i].format, &jobs[i].identifier, &found);
#else
        jobs[i].format->findAllTypesForFile(found, jobs[i].identifier);
#endif

        {
            std::lock_guard<std::mutex> lock(_mutex);
            for (auto* desc : found)
                _addDescription(*desc);
        }

        _done.store(static_cast<int>(i) + 1, std::memory_order_release);
    }
}

bool PluginScanner::resolve(const char* identifier, PluginDescriptorC* out)
{
    if (!identifier || !out) return false;

    ownvst3_ensureJuceInitialised();

    const juce::String id = juce::String::fromUTF8(identifier);

    juce::AudioPluginFormatManager formats;
    formats.addDefaultFormats();

    for (int i = 0; i < formats.getNumFormats(); ++i)
    {
        auto* fmt = formats.getFormat(i);
        if (!fmt->fileMightContainThisPluginType(id)) continue;

        juce::OwnedArray<juce::PluginDescription> found;
#if defined(_WIN32)
        probeGuarded(fmt, &id, &found);
#else
        fmt->findAllTypesForFile(found, id);
#endif

        if (found.isEmpty()) continue;

        std::lock_guard<std::mutex> lock(_mutex);
        _addDescription(*found[0]);
        _sortEntries();

        for (const auto& e : _entries)
        {
            if (e->identifier == identifier)
            {
                _copyOut(*e, out);
                return true;
            }
        }
    }

    return false;
}

void PluginScanner::_copyOut(const Entry& e, PluginDescriptorC* out)
{
    out->name         = e.name.c_str();
    out->vendor       = e.vendor.c_str();
    out->version      = e.version.c_str();
    out->category     = e.category.c_str();
    out->identifier   = e.identifier.c_str();
    out->formatName   = e.formatName.c_str();
    out->fileOrPath   = e.filePath.c_str();
    out->isInstrument = e.isInstrument;
    out->numInputs    = e.numInputs;
    out->numOutputs   = e.numOutputs;
    out->uniqueId     = e.uniqueId;
    out->_reserved    = 0;
}

void PluginScanner::_addDescription(const juce::PluginDescription& desc)
{
    _list.addType(desc);

    const auto identifier = desc.fileOrIdentifier.toStdString();

    auto it = std::find_if(_entries.begin(), _entries.end(),
                           [&](const auto& e) { return e->identifier == identifier; });

    if (it == _entries.end())
    {
        _entries.push_back(std::make_unique<Entry>());
        it = _entries.end() - 1;
    }

    auto& slot = *it;

    slot->name       = desc.name.toStdString();
    slot->vendor       = desc.manufacturerName.toStdString();
    slot->version      = desc.version.toStdString();
    slot->category     = desc.category.toStdString();
    slot->identifier   = identifier;
    slot->formatName   = desc.pluginFormatName.toStdString();
    slot->isInstrument = desc.isInstrument ? 1 : 0;
    slot->numInputs    = desc.numInputChannels;
    slot->numOutputs   = desc.numOutputChannels;
    slot->uniqueId     = desc.uniqueId;

    if (juce::File::isAbsolutePath(desc.fileOrIdentifier))
        slot->filePath = identifier;
}

void PluginScanner::_sortEntries()
{
    std::sort(_entries.begin(), _entries.end(),
              [](const auto& a, const auto& b) { return a->name < b->name; });
}

int PluginScanner::resultCount() const
{
    std::lock_guard<std::mutex> lock(_mutex);
    return static_cast<int>(_entries.size());
}

bool PluginScanner::resultAt(int index, PluginDescriptorC* out) const
{
    if (!out) return false;

    std::lock_guard<std::mutex> lock(_mutex);
    if (index < 0 || index >= static_cast<int>(_entries.size()))
        return false;

    _copyOut(*_entries[static_cast<size_t>(index)], out);
    return true;
}

const char* PluginScanner::cacheXml()
{
    std::lock_guard<std::mutex> lock(_mutex);

    if (auto xml = _list.createXml())
        _cacheXml = xml->toString().toStdString();
    else
        _cacheXml.clear();

    return _cacheXml.c_str();
}

bool PluginScanner::restoreCache(const char* xml)
{
    if (!xml || *xml == '\0' || isRunning())
        return false;

    ownvst3_ensureJuceInitialised();

    auto parsed = juce::parseXML(juce::String::fromUTF8(xml));
    if (!parsed)
        return false;

    std::lock_guard<std::mutex> lock(_mutex);
    _list.clear();
    _list.recreateFromXml(*parsed);
    _rebuildEntries(_list.getTypes());
    return true;
}

void PluginScanner::_rebuildEntries(const juce::Array<juce::PluginDescription>& types)
{
    _entries.clear();

    for (const auto& desc : types)
        _addDescription(desc);

    _sortEntries();
}
