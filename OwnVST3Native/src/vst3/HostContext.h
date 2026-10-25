#pragma once

#include "pluginterfaces/base/ibstream.h"
#include "pluginterfaces/gui/iplugview.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/ivstparameterchanges.h"

#include <atomic>
#include <cstdint>
#include <vector>

#if defined(__linux__)
class Vst3RunLoop;
#endif

/** Process-wide IHostApplication handed to every component and controller. */
Steinberg::FUnknown* ownvst3_hostApplication();

/** What the plugin asks of the host, routed back to the owning Vst3Backend. */
class Vst3HostCallbacks
{
public:
    virtual ~Vst3HostCallbacks() = default;
    virtual void beginEdit(Steinberg::Vst::ParamID id) = 0;
    virtual void performEdit(Steinberg::Vst::ParamID id, Steinberg::Vst::ParamValue value) = 0;
    virtual void endEdit(Steinberg::Vst::ParamID id) = 0;
    virtual void restartComponent(Steinberg::int32 flags) = 0;
    virtual bool resizeView(Steinberg::IPlugView* view, Steinberg::ViewRect* rect) = 0;
};

/**
 * IComponentHandler + IComponentHandler2 (T-RackS 6 dereferences the latter unchecked).
 * Heap, refcounted: a controller may outlive the backend, so detach() cuts the link first.
 */
class ComponentHandler final : public Steinberg::Vst::IComponentHandler, public Steinberg::Vst::IComponentHandler2
{
public:
    explicit ComponentHandler(Vst3HostCallbacks* owner) : _owner(owner) {}

    void detach() noexcept { _owner.store(nullptr, std::memory_order_release); }

    Steinberg::tresult PLUGIN_API beginEdit(Steinberg::Vst::ParamID id) override;
    Steinberg::tresult PLUGIN_API performEdit(Steinberg::Vst::ParamID id, Steinberg::Vst::ParamValue value) override;
    Steinberg::tresult PLUGIN_API endEdit(Steinberg::Vst::ParamID id) override;
    Steinberg::tresult PLUGIN_API restartComponent(Steinberg::int32 flags) override;

    Steinberg::tresult PLUGIN_API setDirty(Steinberg::TBool) override { return Steinberg::kResultOk; }
    Steinberg::tresult PLUGIN_API requestOpenEditor(Steinberg::FIDString) override { return Steinberg::kResultFalse; }
    Steinberg::tresult PLUGIN_API startGroupEdit() override { return Steinberg::kResultOk; }
    Steinberg::tresult PLUGIN_API finishGroupEdit() override { return Steinberg::kResultOk; }

    Steinberg::tresult PLUGIN_API queryInterface(const Steinberg::TUID iid, void** obj) override;
    Steinberg::uint32 PLUGIN_API addRef() override { return ++_refs; }
    Steinberg::uint32 PLUGIN_API release() override;

private:
    std::atomic<Vst3HostCallbacks*> _owner;
    std::atomic<Steinberg::uint32>  _refs { 1 };
};

/** IPlugFrame for one open editor; on Linux it also hands out the Linux::IRunLoop. */
class PlugFrame final : public Steinberg::IPlugFrame
{
public:
    explicit PlugFrame(Vst3HostCallbacks* owner);
    ~PlugFrame();

    void detach() noexcept;

    Steinberg::tresult PLUGIN_API resizeView(Steinberg::IPlugView* view, Steinberg::ViewRect* newSize) override;

    Steinberg::tresult PLUGIN_API queryInterface(const Steinberg::TUID iid, void** obj) override;
    Steinberg::uint32 PLUGIN_API addRef() override { return ++_refs; }
    Steinberg::uint32 PLUGIN_API release() override;

private:
    std::atomic<Vst3HostCallbacks*> _owner;
    std::atomic<Steinberg::uint32>  _refs { 1 };
#if defined(__linux__)
    Vst3RunLoop* _runLoop = nullptr;
#endif
};

/** IBStream over a byte vector, for get/setState. Lives on the stack for one call. */
class VectorStream final : public Steinberg::IBStream, public Steinberg::ISizeableStream
{
public:
    VectorStream() = default;
    VectorStream(const uint8_t* data, size_t size) : _data(data, data + size) {}

    std::vector<uint8_t>&       data() noexcept       { return _data; }
    const std::vector<uint8_t>& data() const noexcept { return _data; }
    void rewind() noexcept { _pos = 0; }

    Steinberg::tresult PLUGIN_API read(void* buffer, Steinberg::int32 numBytes, Steinberg::int32* numBytesRead) override;
    Steinberg::tresult PLUGIN_API write(void* buffer, Steinberg::int32 numBytes, Steinberg::int32* numBytesWritten) override;
    Steinberg::tresult PLUGIN_API seek(Steinberg::int64 pos, Steinberg::int32 mode, Steinberg::int64* result) override;
    Steinberg::tresult PLUGIN_API tell(Steinberg::int64* pos) override;
    Steinberg::tresult PLUGIN_API getStreamSize(Steinberg::int64& size) override;
    Steinberg::tresult PLUGIN_API setStreamSize(Steinberg::int64 size) override;

    Steinberg::tresult PLUGIN_API queryInterface(const Steinberg::TUID iid, void** obj) override;
    Steinberg::uint32 PLUGIN_API addRef() override { return 1; }
    Steinberg::uint32 PLUGIN_API release() override { return 1; }

private:
    std::vector<uint8_t> _data;
    int64_t _pos = 0;
};

/** IParamValueQueue with a fixed number of points; addPoint past it keeps the last value. */
class FixedParamQueue final : public Steinberg::Vst::IParamValueQueue
{
public:
    static constexpr int kMaxPoints = 32;

    void reset(Steinberg::Vst::ParamID id) noexcept { _id = id; _count = 0; }

    Steinberg::Vst::ParamID PLUGIN_API getParameterId() override { return _id; }
    Steinberg::int32 PLUGIN_API getPointCount() override { return _count; }
    Steinberg::tresult PLUGIN_API getPoint(Steinberg::int32 index, Steinberg::int32& sampleOffset,
                                           Steinberg::Vst::ParamValue& value) override;
    Steinberg::tresult PLUGIN_API addPoint(Steinberg::int32 sampleOffset, Steinberg::Vst::ParamValue value,
                                           Steinberg::int32& index) override;

    Steinberg::tresult PLUGIN_API queryInterface(const Steinberg::TUID iid, void** obj) override;
    Steinberg::uint32 PLUGIN_API addRef() override { return 1; }
    Steinberg::uint32 PLUGIN_API release() override { return 1; }

private:
    struct Point { Steinberg::int32 offset; Steinberg::Vst::ParamValue value; };

    Steinberg::Vst::ParamID _id = Steinberg::Vst::kNoParamId;
    Point                   _points[kMaxPoints] {};
    Steinberg::int32        _count = 0;
};

/** IParameterChanges sized once in prepare(); clear() and add are allocation free. */
class FixedParameterChanges final : public Steinberg::Vst::IParameterChanges
{
public:
    void resize(int maxParameters) { _queues.resize(static_cast<size_t>(maxParameters < 1 ? 1 : maxParameters)); _used = 0; }
    void clear() noexcept { _used = 0; }

    /** Host side: one point at sampleOffset. Returns false when full. */
    bool add(Steinberg::Vst::ParamID id, Steinberg::int32 sampleOffset, Steinberg::Vst::ParamValue value) noexcept;

    Steinberg::int32 PLUGIN_API getParameterCount() override { return _used; }
    Steinberg::Vst::IParamValueQueue* PLUGIN_API getParameterData(Steinberg::int32 index) override;
    Steinberg::Vst::IParamValueQueue* PLUGIN_API addParameterData(const Steinberg::Vst::ParamID& id, Steinberg::int32& index) override;

    Steinberg::tresult PLUGIN_API queryInterface(const Steinberg::TUID iid, void** obj) override;
    Steinberg::uint32 PLUGIN_API addRef() override { return 1; }
    Steinberg::uint32 PLUGIN_API release() override { return 1; }

private:
    std::vector<FixedParamQueue> _queues;
    Steinberg::int32             _used = 0;
};
