#include "AuBackend.h"
#include "core/AuIdentifier.h"
#include "core/StateCodec.h"
#include "scan/CacheXml.h"
#include "ui/EditorWindow.h"
#include "ui/UiThread.h"

#include <TargetConditionals.h>

#import <AVFoundation/AVFoundation.h>
#import <AudioToolbox/AudioToolbox.h>
#import <CoreAudioKit/CoreAudioKit.h>

#if TARGET_OS_OSX
#import <Cocoa/Cocoa.h>
using PlatformViewController = NSViewController;
using PlatformView           = NSView;
#else
#import <UIKit/UIKit.h>
using PlatformViewController = UIViewController;
using PlatformView           = UIView;
#endif

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <vector>

namespace
{
#if TARGET_OS_OSX
    constexpr AudioComponentInstantiationOptions kInstantiation = kAudioComponentInstantiation_LoadInProcess;
#else
    // third-party AUv3 on iOS only ever run out of process
    constexpr AudioComponentInstantiationOptions kInstantiation = 0;
#endif

    struct HostTime
    {
        double  sampleRate = 44100.0;
        double  bpm        = 120.0;
        bool    playing    = false;
        int64_t samplePos  = 0;
    };

    std::string toStd(NSString* s)
    {
        return s ? std::string(s.UTF8String ?: "") : std::string();
    }

    std::string trim(const std::string& s)
    {
        const auto a = s.find_first_not_of(" \t\r\n");
        if (a == std::string::npos) return "";
        return s.substr(a, s.find_last_not_of(" \t\r\n") - a + 1);
    }

    bool componentFromBundle(const std::string& path, int subIndex, AuComponentId& out)
    {
        NSBundle* bundle = [NSBundle bundleWithPath:[NSString stringWithUTF8String:path.c_str()]];
        NSArray* list = bundle.infoDictionary[@"AudioComponents"];
        if (![list isKindOfClass:[NSArray class]] || list.count == 0) return false;

        NSDictionary* d = list[(subIndex >= 0 && subIndex < static_cast<int>(list.count)) ? static_cast<NSUInteger>(subIndex) : 0];
        auto code = [&](NSString* key, uint32_t& v) {
            NSString* s = d[key];
            if (![s isKindOfClass:[NSString class]] || s.length != 4) return false;
            const char* c = s.UTF8String;
            v = (static_cast<uint32_t>(static_cast<uint8_t>(c[0])) << 24) | (static_cast<uint32_t>(static_cast<uint8_t>(c[1])) << 16)
              | (static_cast<uint32_t>(static_cast<uint8_t>(c[2])) << 8)  |  static_cast<uint32_t>(static_cast<uint8_t>(c[3]));
            return true;
        };
        return code(@"type", out.type) && code(@"subtype", out.subtype) && code(@"manufacturer", out.manufacturer);
    }

    // JUCE AUs bind to the creating thread, so this has to be the UI thread
    AUAudioUnit* instantiate(const AudioComponentDescription& desc, bool isV3)
    {
        if (!isV3)
        {
            AUAudioUnit* au = nil;
            UiThread::call([&] {
                au = [[AUAudioUnit alloc] initWithComponentDescription:desc
                                                               options:kInstantiation
                                                                 error:nil];
            });
            return au;
        }

        struct Box { AUAudioUnit* au = nil; };
        auto box = std::make_shared<Box>();
        dispatch_semaphore_t done = dispatch_semaphore_create(0);

        auto start = [box, done, desc] {
            [AUAudioUnit instantiateWithComponentDescription:desc
                                                     options:kInstantiation
                                           completionHandler:^(AUAudioUnit* au, NSError*) {
                box->au = au;
                dispatch_semaphore_signal(done);
            }];
        };

        if (UiThread::isCurrent() || !UiThread::available()) start();
        else                                                UiThread::post(start);

        if (pthread_main_np())
        {
            const CFAbsoluteTime until = CFAbsoluteTimeGetCurrent() + 10.0;
            while (dispatch_semaphore_wait(done, DISPATCH_TIME_NOW) != 0 && CFAbsoluteTimeGetCurrent() < until)
                CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.01, true);
        }
        else
        {
            dispatch_semaphore_wait(done, dispatch_time(DISPATCH_TIME_NOW, 10 * NSEC_PER_SEC));
        }
        return box->au;
    }

    class AuBackend final : public IPluginBackend
    {
    public:
        static std::unique_ptr<AuBackend> load(const std::string& identifier, int subIndex)
        {
            AuComponentId id;
            const bool parsed = identifier.rfind("AudioUnit:", 0) == 0
                              ? ownvst3_parseAuIdentifier(identifier, id)
                              : componentFromBundle(identifier, subIndex, id);
            if (!parsed) return nullptr;

            AudioComponentDescription desc { id.type, id.subtype, id.manufacturer, 0, 0 };
            AudioComponent comp = AudioComponentFindNext(nullptr, &desc);
            if (!comp) return nullptr;

            AudioComponentDescription found {};
            AudioComponentGetDescription(comp, &found);

            std::unique_ptr<AuBackend> self(new AuBackend());
            self->_au = instantiate(desc, (found.componentFlags & kAudioComponentFlag_IsV3AudioUnit) != 0);
            if (!self->_au) return nullptr;

            self->_identifier = ownvst3_makeAuIdentifier(id);
            self->_type       = id.type;

            UiThread::call([&] {
                self->readMeta(comp, id);
                self->readBuses();
                self->readParameters();

#if TARGET_OS_OSX
                if (@available(macOS 13.0, *)) self->_hasEditor = true;
                else                           self->_hasEditor = self->_au.providesUserInterface;
#else
                // Apple's built-in (v2 bridged) units claim a UI on iOS and then hand over none
                self->_hasEditor = self->_au.providesUserInterface && self->_v2 == nullptr;
#endif
            });

            return self;
        }

        ~AuBackend() override
        {
            *_alive = false;
            UiThread::call([this] {
                closeEditorOnUi();
                detachOnUi();
                if (_allocated) [_au deallocateRenderResources];
                _au = nil;
            });
        }

        const char*        formatName() const noexcept override { return "AudioUnit"; }
        const std::string& identifier() const noexcept override { return _identifier; }
        const PluginMeta&  meta() const noexcept override { return _meta; }

        bool acceptsMidi() const override
        {
            return _type == ownvst3_fourCC("aumu") || _type == ownvst3_fourCC("aumf") || _type == ownvst3_fourCC("aumi");
        }

        int inputChannels() const override  { return _totalIn; }
        int outputChannels() const override { return _totalOut; }
        int latencySamples() const override { return _latency; }

        bool prepare(double sampleRate, int maxBlockSize) override
        {
            bool ok = false;
            UiThread::call([&] { ok = prepareOnUi(sampleRate, maxBlockSize); });
            return ok;
        }

        bool prepareOnUi(double sampleRate, int maxBlockSize)
        {
            if (_allocated) { [_au deallocateRenderResources]; _allocated = false; }

            setBusFormats(sampleRate);
            readBuses();

            _au.maximumFramesToRender = static_cast<AUAudioFrameCount>(maxBlockSize);

            HostTime* host = &_host;
            _au.musicalContextBlock = ^BOOL(double* tempo, double* tsNum, NSInteger* tsDen, double* beat,
                                            NSInteger* toNextBeat, double* downbeat) {
                const double beats = host->sampleRate > 0 ? host->samplePos / host->sampleRate * host->bpm / 60.0 : 0.0;
                if (tempo)      *tempo      = host->bpm;
                if (tsNum)      *tsNum      = 4;
                if (tsDen)      *tsDen      = 4;
                if (beat)       *beat       = beats;
                if (toNextBeat) *toNextBeat = 0;
                if (downbeat)   *downbeat   = std::floor(beats / 4.0) * 4.0;
                return YES;
            };
            _au.transportStateBlock = ^BOOL(AUHostTransportStateFlags* flags, double* pos, double* cycleStart, double* cycleEnd) {
                if (flags)      *flags      = host->playing ? AUHostTransportStateMoving : static_cast<AUHostTransportStateFlags>(0);
                if (pos)        *pos        = static_cast<double>(host->samplePos);
                if (cycleStart) *cycleStart = 0;
                if (cycleEnd)   *cycleEnd   = 0;
                return YES;
            };

            NSError* error = nil;
            if (![_au allocateRenderResourcesAndReturnError:&error]) return false;
            _allocated = true;

            _renderBlock   = _au.renderBlock;
            _scheduleParam = _au.scheduleParameterBlock;
            _scheduleMidi  = _au.scheduleMIDIEventBlock;

            allocateBuffers(maxBlockSize);
            _maxBlock = maxBlockSize;
            _host.sampleRate = sampleRate;
            _latency = static_cast<int>(std::lround(_au.latency * sampleRate));
            return true;
        }

        int parameterCount() const override { return static_cast<int>(_names.size()); }

        bool parameterInfo(int index, std::string& name, double& defaultValue) override
        {
            if (index < 0 || index >= parameterCount()) return false;
            name         = _names[static_cast<size_t>(index)];
            defaultValue = _defaults[static_cast<size_t>(index)];
            return true;
        }

        double parameterValue(int index) const override
        {
            if (index < 0 || index >= parameterCount()) return 0.0;
            return _values[static_cast<size_t>(index)].load(std::memory_order_relaxed);
        }

        void setParameterCache(int index, double value) override
        {
            if (index >= 0 && index < parameterCount())
                _values[static_cast<size_t>(index)].store(value, std::memory_order_relaxed);
        }

        int process(float* const* in, float* const* out, int numChannels, int numSamples,
                    const ParamChange* params, int numParams, const MidiEventC* midi, int numMidi,
                    const BlockContext& ctx) override
        {
            if (!_allocated || !_renderBlock || numSamples > _maxBlock) return -1;

            _host.bpm       = ctx.bpm;
            _host.playing   = ctx.playing;
            _host.samplePos = ctx.samplePos;

            const size_t bytes = sizeof(float) * static_cast<size_t>(numSamples);

            for (int c = 0; c < _totalIn; ++c)
            {
                float* dst = _inPtrs[static_cast<size_t>(c)];
                if (c < numChannels && in && in[c]) std::memcpy(dst, in[c], bytes);
                else                                std::memset(dst, 0, bytes);
            }

            if (_scheduleParam)
            {
                for (int i = 0; i < numParams; ++i)
                {
                    const auto idx = static_cast<size_t>(params[i].index);
                    if (params[i].index < 0 || idx >= _addresses.size()) continue;
                    const AUValue v = static_cast<AUValue>(_mins[idx] + params[i].value * (_maxs[idx] - _mins[idx]));
                    _scheduleParam(AUEventSampleTimeImmediate, 0, _addresses[idx], v);
                }
            }

            if (_scheduleMidi)
            {
                for (int i = 0; i < numMidi; ++i)
                {
                    const uint8_t type = midi[i].status & 0xF0;
                    if (type < 0x80) continue;
                    const uint8_t bytesOut[3] = { midi[i].status, midi[i].data1, midi[i].data2 };
                    const NSInteger len = (type == 0xC0 || type == 0xD0) ? 2 : 3;
                    const AUEventSampleTime offset = std::clamp(midi[i].sampleOffset, 0, numSamples - 1);
                    _scheduleMidi(AUEventSampleTimeImmediate + offset, 0, len, bytesOut);
                }
            }

            AudioTimeStamp ts {};
            ts.mSampleTime = static_cast<Float64>(_renderPos);
            ts.mFlags      = kAudioTimeStampSampleTimeValid;

            size_t channel = 0;
            for (size_t b = 0; b < _outLists.size(); ++b)
            {
                auto* abl = reinterpret_cast<AudioBufferList*>(_outLists[b].data());
                const size_t first = channel;

                for (UInt32 i = 0; i < abl->mNumberBuffers; ++i, ++channel)
                {
                    abl->mBuffers[i].mNumberChannels = 1;
                    abl->mBuffers[i].mDataByteSize   = static_cast<UInt32>(bytes);
                    abl->mBuffers[i].mData           = _outPtrs[channel];
                }

                AudioUnitRenderActionFlags flags = 0;
                const AUAudioUnitStatus status = _renderBlock(&flags, &ts, static_cast<AUAudioFrameCount>(numSamples),
                                                              static_cast<NSInteger>(b), abl, _pullInput);

                for (UInt32 i = 0; i < abl->mNumberBuffers; ++i)
                {
                    float* mine = _outPtrs[first + i];
                    if (status != noErr)                     std::memset(mine, 0, bytes);
                    else if (abl->mBuffers[i].mData != mine) std::memcpy(mine, abl->mBuffers[i].mData, bytes);
                }
            }
            _renderPos += numSamples;

            const int written = std::min(numChannels, _totalOut);
            if (out)
                for (int c = 0; c < written; ++c)
                    if (out[c]) std::memcpy(out[c], _outPtrs[static_cast<size_t>(c)], bytes);
            return written;
        }

        bool hasEditor() const override { return _hasEditor; }

        bool openEditor(void* owner) override
        {
#if TARGET_OS_OSX
            if (!UiThread::available()) return false;

            bool ok = false;
            UiThread::call([&] { ok = openEditorOnUi(owner); });
            return ok;
#else
            // no top-level windows of our own on iOS: OwnPlugin_AttachEditorToView
            (void)owner;
            return false;
#endif
        }

        void closeEditor() override
        {
            UiThread::call([this] { closeEditorOnUi(); });
        }

        void resizeEditor(int width, int height) override
        {
#if TARGET_OS_OSX
            UiThread::call([&] { if (_window) _window->setContentSize(width, height); });
#else
            (void)width; (void)height;
#endif
        }

        bool editorSize(int& width, int& height) override
        {
            bool ok = false;
            UiThread::call([&] {
#if TARGET_OS_OSX
                if (_window)
                {
                    _window->contentSize(width, height);
                    ok = width > 0 && height > 0;
                    return;
                }
#endif
                if (!_embedded) return;
                const CGSize size = _embedded.view.bounds.size;
                width  = static_cast<int>(size.width);
                height = static_cast<int>(size.height);
                ok = width > 0 && height > 0;
            });
            return ok;
        }

        bool isEditorOpen() const override
        {
            return _editorOpen.load(std::memory_order_acquire) || _attached.load(std::memory_order_acquire);
        }

        bool attachEditor(void* parentView) override
        {
            if (!parentView || !_hasEditor || !UiThread::available()) return false;

            UiThread::call([&] {
                detachOnUi();

                _embedParent = (__bridge PlatformView*)parentView;
                _attached.store(true, std::memory_order_release);

                auto alive = _alive;
                const uint64_t generation = ++_embedGeneration;
                [_au requestViewControllerWithCompletionHandler:^(AUViewControllerBase* vc) {
                    auto embed = [this, alive, generation, vc] {
                        if (*alive && generation == _embedGeneration) embedInto(vc);
                    };
                    if (pthread_main_np()) embed();
                    else                   UiThread::post(embed);
                }];
            });
            return true;
        }

        void detachEditor() override
        {
            UiThread::call([this] { detachOnUi(); });
        }

        bool getState(std::vector<uint8_t>& out) override
        {
            bool ok = false;
            UiThread::call([&] {
                NSDictionary* state = _au.fullState;
                if (!state) return;

                NSData* data = [NSPropertyListSerialization dataWithPropertyList:state
                                                                          format:NSPropertyListBinaryFormat_v1_0
                                                                         options:0
                                                                           error:nil];
                if (!data) return;

                const auto* p = static_cast<const uint8_t*>(data.bytes);
                out.assign(p, p + data.length);
                ok = true;
            });
            return ok;
        }

        bool setState(const uint8_t* data, size_t size) override
        {
            if (!StateCodec::looksLikePlist(data, size)) return false;

            bool ok = false;
            UiThread::call([&] {
                NSData* blob = [NSData dataWithBytes:data length:size];
                id plist = [NSPropertyListSerialization propertyListWithData:blob options:NSPropertyListImmutable format:nil error:nil];
                if (![plist isKindOfClass:[NSDictionary class]]) return;

                _au.fullState = plist;
                refreshValues();
                ok = true;
            });
            return ok;
        }

    private:
        AuBackend() = default;

        void readMeta(AudioComponent comp, const AuComponentId& id)
        {
            CFStringRef cfName = nullptr;
            if (AudioComponentCopyName(comp, &cfName) == noErr && cfName)
            {
                _meta.name = toStd((__bridge NSString*)cfName);
                CFRelease(cfName);
            }

            const auto colon = _meta.name.find(':');
            if (colon != std::string::npos)
            {
                _meta.vendor = trim(_meta.name.substr(0, colon));
                _meta.name   = trim(_meta.name.substr(colon + 1));
            }
            if (_meta.name.empty())   _meta.name   = toStd(_au.audioUnitName);
            if (_meta.vendor.empty()) _meta.vendor = toStd(_au.manufacturerName);
            if (_meta.vendor.empty()) _meta.vendor = ownvst3_fourCCString(id.manufacturer);

            UInt32 v = 0;
            if (AudioComponentGetVersion(comp, &v) == noErr)
                _meta.version = std::to_string(v >> 16) + "." + std::to_string((v >> 8) & 0xff) + "." + std::to_string(v & 0xff);

            const auto folder = ownvst3_auCategoryFolder(id.type);
            _meta.category = folder.empty() ? "" : folder.substr(0, folder.size() - 1);

            const uint8_t raw[12] = {
                static_cast<uint8_t>(id.type >> 24), static_cast<uint8_t>(id.type >> 16), static_cast<uint8_t>(id.type >> 8), static_cast<uint8_t>(id.type),
                static_cast<uint8_t>(id.subtype >> 24), static_cast<uint8_t>(id.subtype >> 16), static_cast<uint8_t>(id.subtype >> 8), static_cast<uint8_t>(id.subtype),
                static_cast<uint8_t>(id.manufacturer >> 24), static_cast<uint8_t>(id.manufacturer >> 16), static_cast<uint8_t>(id.manufacturer >> 8), static_cast<uint8_t>(id.manufacturer) };
            _meta.uniqueId = ownvst3_hash31(raw, sizeof(raw));
        }

        void setBusFormats(double sampleRate)
        {
            auto apply = [&](AUAudioUnitBusArray* buses) {
                for (NSUInteger i = 0; i < buses.count; ++i)
                {
                    AUAudioUnitBus* bus = buses[i];
                    const AVAudioChannelCount current = bus.format.channelCount;
                    const AVAudioChannelCount wanted  = i == 0 ? 2 : (current ? current : 2);

                    AVAudioFormat* f = [[AVAudioFormat alloc] initStandardFormatWithSampleRate:sampleRate channels:wanted];
                    if (![bus setFormat:f error:nil] && wanted != current && current > 0)
                        [bus setFormat:[[AVAudioFormat alloc] initStandardFormatWithSampleRate:sampleRate channels:current] error:nil];

                    bus.enabled = YES;
                }
            };
            apply(_au.inputBusses);
            apply(_au.outputBusses);
        }

        void readBuses()
        {
            _inChannels.clear();
            _outChannels.clear();
            AUAudioUnitBusArray* ins  = _au.inputBusses;
            AUAudioUnitBusArray* outs = _au.outputBusses;
            for (NSUInteger i = 0; i < ins.count; ++i)  _inChannels.push_back(static_cast<int>(ins[i].format.channelCount));
            for (NSUInteger i = 0; i < outs.count; ++i) _outChannels.push_back(static_cast<int>(outs[i].format.channelCount));

            _totalIn = 0;
            _totalOut = 0;
            for (int c : _inChannels)  _totalIn += c;
            for (int c : _outChannels) _totalOut += c;
        }

        void readParameters()
        {
            NSArray<AUParameter*>* all = _au.parameterTree.allParameters;

            _params    = all ?: @[];
            _names.clear();
            _defaults.clear();
            _addresses.clear();
            _mins.clear();
            _maxs.clear();

            _v2 = nullptr;
            if (@available(macOS 11.0, iOS 14.0, *))
                if ([_au isKindOfClass:[AUAudioUnitV2Bridge class]]) _v2 = ((AUAudioUnitV2Bridge*)_au).audioUnit;

            for (AUParameter* p in _params)
            {
                _names.push_back(toStd(p.displayName));
                _addresses.push_back(p.address);

                AudioUnitParameterInfo info {};
                UInt32 size = sizeof(info);
                if (_v2 && AudioUnitGetProperty(_v2, kAudioUnitProperty_ParameterInfo, kAudioUnitScope_Global,
                                                static_cast<AudioUnitElement>(p.address), &info, &size) == noErr)
                {
                    _mins.push_back(info.minValue);
                    _maxs.push_back(info.maxValue);
                    _defaults.push_back(normalize(_names.size() - 1, info.defaultValue));
                }
                else
                {
                    _mins.push_back(p.minValue);
                    _maxs.push_back(p.maxValue);
                    _defaults.push_back(normalize(_names.size() - 1, p.value));
                }
            }

            _values.reset(new std::atomic<double>[_names.size()]);
            refreshValues();
        }

        void refreshValues()
        {
            NSUInteger i = 0;
            for (AUParameter* p in _params)
            {
                if (i >= _names.size()) break;

                AudioUnitParameterValue v = p.value;
                if (_v2) AudioUnitGetParameter(_v2, static_cast<AudioUnitParameterID>(_addresses[i]), kAudioUnitScope_Global, 0, &v);

                _values[i].store(normalize(i, v), std::memory_order_relaxed);
                ++i;
            }
        }

        double normalize(size_t i, AUValue v) const
        {
            const double range = static_cast<double>(_maxs[i]) - _mins[i];
            return range > 0 ? std::clamp((v - _mins[i]) / range, 0.0, 1.0) : 0.0;
        }

        void allocateBuffers(int maxBlock)
        {
            const size_t frames = static_cast<size_t>(maxBlock);

            _inScratch.assign(static_cast<size_t>(_totalIn) * frames, 0.0f);
            _outScratch.assign(static_cast<size_t>(_totalOut) * frames, 0.0f);
            _inPtrs.resize(static_cast<size_t>(_totalIn));
            _outPtrs.resize(static_cast<size_t>(_totalOut));
            for (int c = 0; c < _totalIn; ++c)  _inPtrs[static_cast<size_t>(c)]  = _inScratch.data() + static_cast<size_t>(c) * frames;
            for (int c = 0; c < _totalOut; ++c) _outPtrs[static_cast<size_t>(c)] = _outScratch.data() + static_cast<size_t>(c) * frames;

            _inFirst.assign(_inChannels.size(), 0);
            for (size_t b = 1; b < _inChannels.size(); ++b) _inFirst[b] = _inFirst[b - 1] + _inChannels[b - 1];

            _outLists.clear();
            for (int ch : _outChannels)
            {
                std::vector<uint8_t> mem(offsetof(AudioBufferList, mBuffers) + sizeof(AudioBuffer) * static_cast<size_t>(std::max(1, ch)), 0);
                reinterpret_cast<AudioBufferList*>(mem.data())->mNumberBuffers = static_cast<UInt32>(ch);
                _outLists.push_back(std::move(mem));
            }

            AuBackend* self = this;
            _pullInput = ^AUAudioUnitStatus(AudioUnitRenderActionFlags*, const AudioTimeStamp*, AUAudioFrameCount frames,
                                            NSInteger bus, AudioBufferList* data) {
                const size_t bytes = sizeof(float) * frames;
                const bool known = bus >= 0 && static_cast<size_t>(bus) < self->_inChannels.size();

                for (UInt32 i = 0; i < data->mNumberBuffers; ++i)
                {
                    float* src = known && static_cast<int>(i) < self->_inChannels[static_cast<size_t>(bus)]
                               ? self->_inPtrs[static_cast<size_t>(self->_inFirst[static_cast<size_t>(bus)] + static_cast<int>(i))]
                               : nullptr;

                    data->mBuffers[i].mDataByteSize = static_cast<UInt32>(bytes);
                    if (!data->mBuffers[i].mData)
                    {
                        if (src) data->mBuffers[i].mData = src;
                        else     return kAudioUnitErr_NoConnection;
                    }
                    else if (src) std::memcpy(data->mBuffers[i].mData, src, bytes);
                    else          std::memset(data->mBuffers[i].mData, 0, bytes);
                }
                return noErr;
            };
        }

        PlatformViewController* withFallback(PlatformViewController* vc)
        {
#if TARGET_OS_OSX
            if (!vc)
            {
                if (@available(macOS 13.0, *))
                {
                    AUGenericViewController* generic = [[AUGenericViewController alloc] init];
                    generic.auAudioUnit = _au;
                    vc = generic;
                }
            }
#endif
            return vc;
        }

        void embedInto(PlatformViewController* vc)
        {
            vc = withFallback(vc);
            if (!vc || !_embedParent) { detachOnUi(); return; }

            _embedded = vc;
            PlatformView* view = vc.view;
            view.frame = _embedParent.bounds;
#if TARGET_OS_OSX
            view.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
#else
            view.autoresizingMask = UIViewAutoresizingFlexibleWidth | UIViewAutoresizingFlexibleHeight;
#endif
            [_embedParent addSubview:view];
        }

        void detachOnUi()
        {
            ++_embedGeneration;
            _attached.store(false, std::memory_order_release);
            if (_embedded) [_embedded.view removeFromSuperview];
            _embedded    = nil;
            _embedParent = nil;
        }

#if TARGET_OS_OSX
        bool openEditorOnUi(void* owner)
        {
            if (_window) { _window->show(); return true; }
            if (!_hasEditor) return false;

            auto alive = _alive;
            EditorWindow::Callbacks cb;
            cb.closeRequested = [this, alive] {
                _editorOpen.store(false, std::memory_order_release);
                UiThread::post([this, alive] { if (*alive) closeEditorOnUi(); });
            };

            _window = EditorWindow::create(_meta.name, 400, 300, true, owner, std::move(cb));
            if (!_window) return false;
            _editorOpen.store(true, std::memory_order_release);

            const uint64_t generation = ++_editorGeneration;
            [_au requestViewControllerWithCompletionHandler:^(AUViewControllerBase* vc) {
                auto embed = [this, alive, generation, vc] {
                    if (*alive && generation == _editorGeneration) embedView(vc);
                };
                if (pthread_main_np()) embed();
                else                   UiThread::post(embed);
            }];
            return true;
        }

        void embedView(NSViewController* vc)
        {
            if (!_window) return;

            vc = withFallback(vc);
            if (!vc) { closeEditorOnUi(); return; }

            _viewController = vc;
            NSView* view = vc.view;

            NSSize size = vc.preferredContentSize;
            if (size.width < 1 || size.height < 1) size = view.frame.size;
            if (size.width < 1 || size.height < 1) size = view.fittingSize;
            if (size.width < 1 || size.height < 1) size = NSMakeSize(400, 300);

            NSView* content = (__bridge NSView*)_window->parentHandle();
            view.frame = NSMakeRect(0, 0, size.width, size.height);
            view.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
            [content addSubview:view];

            _window->setContentSize(static_cast<int>(size.width), static_cast<int>(size.height));
            _window->show();
        }

        void closeEditorOnUi()
        {
            ++_editorGeneration;
            _editorOpen.store(false, std::memory_order_release);
            if (_window) _window->prepareForViewRemoval();
            if (_viewController) [_viewController.view removeFromSuperview];
            _viewController = nil;
            _window.reset();
        }
#else
        void closeEditorOnUi() {}
#endif

        AUAudioUnit* _au = nil;
        std::string  _identifier;
        PluginMeta   _meta;
        uint32_t     _type      = 0;
        bool         _hasEditor = false;
        int          _latency   = 0;

        std::vector<int> _inChannels, _outChannels, _inFirst;
        int _totalIn = 0, _totalOut = 0;

        NSArray<AUParameter*>*               _params = nil;
        AudioUnit                            _v2     = nullptr;
        std::vector<std::string>             _names;
        std::vector<double>                  _defaults;
        std::vector<AUParameterAddress>      _addresses;
        std::vector<AUValue>                 _mins, _maxs;
        std::unique_ptr<std::atomic<double>[]> _values;

        bool                       _allocated = false;
        int                        _maxBlock  = 0;
        int64_t                    _renderPos = 0;
        HostTime                   _host;
        AURenderBlock              _renderBlock   = nil;
        AUScheduleParameterBlock   _scheduleParam = nil;
        AUScheduleMIDIEventBlock   _scheduleMidi  = nil;
        AURenderPullInputBlock     _pullInput     = nil;
        std::vector<float>         _inScratch, _outScratch;
        std::vector<float*>        _inPtrs, _outPtrs;
        std::vector<std::vector<uint8_t>> _outLists;

#if TARGET_OS_OSX
        std::unique_ptr<EditorWindow>      _window;
        NSViewController*                  _viewController = nil;
#endif
        std::atomic<bool>                  _editorOpen { false };
        uint64_t                           _editorGeneration = 0;

        PlatformViewController*            _embedded    = nil;
        PlatformView*                      _embedParent = nil;
        std::atomic<bool>                  _attached { false };
        uint64_t                           _embedGeneration = 0;
        std::shared_ptr<std::atomic<bool>> _alive = std::make_shared<std::atomic<bool>>(true);
    };
}

std::unique_ptr<IPluginBackend> ownvst3_createAuBackend(const std::string& identifier, int subIndex)
{
    return AuBackend::load(identifier, subIndex);
}
