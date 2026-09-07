#include "PluginScanner.h"
#include "PluginInstance.h"

#if defined(_WIN32)
    #define NOMINMAX
    #include <windows.h>
#endif

namespace
{
    /** Maps a JUCE format name onto the OWNPLUGIN_FORMAT_* bit the caller asked for. */
    int formatBit(const juce::String& formatName)
    {
        if (formatName == "VST3")      return OWNPLUGIN_FORMAT_VST3;
        if (formatName == "AudioUnit") return OWNPLUGIN_FORMAT_AUDIOUNIT;
        return 0;
    }

#if defined(_WIN32)
    // Same reasoning as loadPluginBody(): a broken plugin DllMain must not take
    // the whole scan (and the .NET host) down with it. No C++ objects with
    // destructors may live in a function containing __try/__except.
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

bool PluginScanner::start(int formatMask)
{
    if (_running.exchange(true, std::memory_order_acq_rel))
        return false;

    // A previous run may have finished without anyone joining it.
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

    _worker = std::thread(&PluginScanner::_scanWorker, this, formatMask);
    return true;
}

void PluginScanner::_scanWorker(int formatMask)
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

        // AUs come back as registry identifiers here, VST3s as bundle paths.
        // 'true' for async instantiation keeps out-of-process AUv3 units in the list.
        const auto ids = fmt->searchPathsForPlugins(fmt->getDefaultLocationsToSearch(), true, true);
        for (const auto& id : ids)
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

        if (!found.isEmpty())
        {
            std::lock_guard<std::mutex> lock(_mutex);
            for (auto* desc : found)
            {
                _list.addType(*desc);

                auto e = std::make_unique<Entry>();
                e->name         = desc->name.toStdString();
                e->vendor       = desc->manufacturerName.toStdString();
                e->version      = desc->version.toStdString();
                e->category     = desc->category.toStdString();
                e->identifier   = desc->fileOrIdentifier.toStdString();
                e->formatName   = desc->pluginFormatName.toStdString();
                e->isInstrument = desc->isInstrument ? 1 : 0;
                e->numInputs    = desc->numInputChannels;
                e->numOutputs   = desc->numOutputChannels;
                e->uniqueId     = desc->uniqueId;

                if (juce::File::isAbsolutePath(desc->fileOrIdentifier))
                    e->filePath = e->identifier;

                _entries.push_back(std::move(e));
            }
        }

        _done.store(static_cast<int>(i) + 1, std::memory_order_release);
    }

    {
        std::lock_guard<std::mutex> lock(_mutex);
        _currentItem.clear();
    }

    _running.store(false, std::memory_order_release);
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

    const auto& e = *_entries[static_cast<size_t>(index)];

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
    {
        auto e = std::make_unique<Entry>();
        e->name         = desc.name.toStdString();
        e->vendor       = desc.manufacturerName.toStdString();
        e->version      = desc.version.toStdString();
        e->category     = desc.category.toStdString();
        e->identifier   = desc.fileOrIdentifier.toStdString();
        e->formatName   = desc.pluginFormatName.toStdString();
        e->isInstrument = desc.isInstrument ? 1 : 0;
        e->numInputs    = desc.numInputChannels;
        e->numOutputs   = desc.numOutputChannels;
        e->uniqueId     = desc.uniqueId;

        if (juce::File::isAbsolutePath(desc.fileOrIdentifier))
            e->filePath = e->identifier;

        _entries.push_back(std::move(e));
    }
}
