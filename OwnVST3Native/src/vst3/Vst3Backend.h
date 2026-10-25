#pragma once

#include "core/IPluginBackend.h"
#include "core/MpscQueue.h"
#include "HostContext.h"
#include "MidiToVst.h"
#include "ui/EditorWindow.h"
#include "ui/UiThread.h"

#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivstcomponent.h"
#include "pluginterfaces/vst/ivstmessage.h"
#include "public.sdk/source/vst/hosting/eventlist.h"
#include "public.sdk/source/vst/hosting/module.h"

#include <atomic>
#include <memory>
#include <string>
#include <unordered_map>
#include <vector>

/**
 * A VST3 plugin hosted straight on the SDK. Built from the 1.5 SDK host (public domain)
 * plus what the JUCE era taught: IComponentHandler2, IMidiMapping, main-thread hasEditor,
 * content scale after attached(), no idle driven from C#.
 */
class Vst3Backend final : public IPluginBackend, private Vst3HostCallbacks
{
public:
    static std::unique_ptr<Vst3Backend> load(const std::string& path, int subIndex);
    ~Vst3Backend() override;

    const char*        formatName() const noexcept override { return "VST3"; }
    const std::string& identifier() const noexcept override { return _identifier; }
    const PluginMeta&  meta() const noexcept override { return _meta; }

    bool acceptsMidi() const override { return _hasEventInput; }
    int  inputChannels() const override { return _mainIn; }
    int  outputChannels() const override { return _mainOut; }
    int  latencySamples() const override { return _latency.load(std::memory_order_relaxed); }

    bool prepare(double sampleRate, int maxBlockSize) override;

    int    parameterCount() const override { return static_cast<int>(_params.size()); }
    bool   parameterInfo(int index, std::string& name, double& defaultValue) override;
    double parameterValue(int index) const override;
    void   setParameterCache(int index, double value) override;
    int    bypassParameter() const override { return _bypassIndex; }

    int process(float* const* in, float* const* out, int numChannels, int numSamples,
                const ParamChange* params, int numParams, const MidiEventC* midi, int numMidi,
                const BlockContext& ctx) override;

    bool hasEditor() const override { return _hasEditor; }
    bool openEditor(void* ownerWindow) override;
    void closeEditor() override;
    void resizeEditor(int width, int height) override;
    bool editorSize(int& width, int& height) override;
    bool isEditorOpen() const override { return _editorOpen.load(std::memory_order_acquire); }

    bool getState(std::vector<uint8_t>& out) override;
    bool setState(const uint8_t* data, size_t size) override;

private:
    Vst3Backend() = default;

    struct Param
    {
        Steinberg::Vst::ParamID id;
        std::string             name;
        double                  defaultValue;
        Steinberg::int32        flags;
    };

    bool loadOnUi(const std::string& path, int subIndex);
    void unloadOnUi();
    bool setupBuses();
    void readParameters();
    void refreshValues();
    void buildMidiMap();
    void stopProcessing();
    void onIdle();

    void openEditorOnUi(void* owner, bool& ok);
    void closeEditorOnUi();
    void onUserResize(int width, int height);

    int  indexOf(Steinberg::Vst::ParamID id) const;

    void beginEdit(Steinberg::Vst::ParamID) override {}
    void performEdit(Steinberg::Vst::ParamID id, Steinberg::Vst::ParamValue value) override;
    void endEdit(Steinberg::Vst::ParamID) override {}
    void restartComponent(Steinberg::int32 flags) override;
    bool resizeView(Steinberg::IPlugView* view, Steinberg::ViewRect* rect) override;

    VST3::Hosting::Module::Ptr                       _module;
    Steinberg::IPtr<Steinberg::Vst::IComponent>      _component;
    Steinberg::IPtr<Steinberg::Vst::IAudioProcessor> _processor;
    Steinberg::IPtr<Steinberg::Vst::IEditController> _controller;
    Steinberg::IPtr<Steinberg::Vst::IConnectionPoint> _componentCP, _controllerCP;
    bool              _singleComponent = false;
    ComponentHandler* _handler         = nullptr;

    std::string _identifier;
    PluginMeta  _meta;
    bool        _hasEventInput = false;
    bool        _hasEditor     = false;
    int         _mainIn        = 0;
    int         _mainOut       = 0;
    std::atomic<int> _latency { 0 };

    std::vector<Param>                           _params;
    std::unordered_map<Steinberg::Vst::ParamID, int> _idToIndex;
    std::unique_ptr<std::atomic<double>[]>        _values;
    std::unique_ptr<std::atomic<bool>[]>          _controllerDirty;
    std::atomic<bool>                             _anyDirty { false };
    int                                           _bypassIndex = -1;
    MidiMapTable                                  _midiMap;

    /* Everything process() touches, sized in prepare(). */
    std::vector<float>                           _scratch;
    std::vector<float*>                          _channelPtrs;
    std::vector<Steinberg::Vst::AudioBusBuffers> _inBuses, _outBuses;
    FixedParameterChanges                        _inChanges, _outChanges;
    Steinberg::Vst::EventList                    _events { 1024 };
    MpscQueue<ParamChange, 512>                  _editQueue;
    int  _maxBlock   = 0;
    bool _active     = false;
    bool _processing = false;

    /* Editor – UI thread only, except the flag. */
    Steinberg::IPtr<Steinberg::IPlugView> _view;
    PlugFrame*                            _frame = nullptr;
    std::unique_ptr<EditorWindow>         _window;
    std::atomic<bool>                     _editorOpen   { false };
    std::atomic<bool>                     _closePending { false };
    bool                                  _inResize     = false;
    bool                                  _attached     = false;

    std::atomic<Steinberg::int32> _restartFlags { 0 };
    UiThread::TimerId             _timer = 0;
};
