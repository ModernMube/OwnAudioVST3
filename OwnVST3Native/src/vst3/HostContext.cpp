#include "HostContext.h"

#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/utility/stringconvert.h"

#if defined(__linux__)
#include "ui/linux/RunLoopLinux.h"
#endif

#include <algorithm>
#include <cstring>

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace
{
    class OwnHostApplication final : public HostApplication
    {
    public:
        tresult PLUGIN_API getName(String128 name) override
        {
            return StringConvert::convert("OwnAudio", name) ? kResultTrue : kInternalError;
        }
    };
}

FUnknown* ownvst3_hostApplication()
{
    static OwnHostApplication* app = new OwnHostApplication();
    return app;
}

tresult PLUGIN_API ComponentHandler::beginEdit(ParamID id)
{
    if (auto* o = _owner.load(std::memory_order_acquire)) o->beginEdit(id);
    return kResultOk;
}

tresult PLUGIN_API ComponentHandler::performEdit(ParamID id, ParamValue value)
{
    if (auto* o = _owner.load(std::memory_order_acquire)) o->performEdit(id, value);
    return kResultOk;
}

tresult PLUGIN_API ComponentHandler::endEdit(ParamID id)
{
    if (auto* o = _owner.load(std::memory_order_acquire)) o->endEdit(id);
    return kResultOk;
}

tresult PLUGIN_API ComponentHandler::restartComponent(int32 flags)
{
    if (auto* o = _owner.load(std::memory_order_acquire)) o->restartComponent(flags);
    return kResultOk;
}

tresult PLUGIN_API ComponentHandler::queryInterface(const TUID iid, void** obj)
{
    if (FUnknownPrivate::iidEqual(iid, IComponentHandler::iid) || FUnknownPrivate::iidEqual(iid, FUnknown::iid))
    {
        addRef();
        *obj = static_cast<IComponentHandler*>(this);
        return kResultOk;
    }
    if (FUnknownPrivate::iidEqual(iid, IComponentHandler2::iid))
    {
        addRef();
        *obj = static_cast<IComponentHandler2*>(this);
        return kResultOk;
    }
    *obj = nullptr;
    return kNoInterface;
}

uint32 PLUGIN_API ComponentHandler::release()
{
    const uint32 r = --_refs;
    if (r == 0) delete this;
    return r;
}

PlugFrame::PlugFrame(Vst3HostCallbacks* owner) : _owner(owner)
{
#if defined(__linux__)
    _runLoop = new Vst3RunLoop();
#endif
}

PlugFrame::~PlugFrame()
{
#if defined(__linux__)
    _runLoop->release();
#endif
}

void PlugFrame::detach() noexcept
{
    _owner.store(nullptr, std::memory_order_release);
#if defined(__linux__)
    _runLoop->clear();
#endif
}

tresult PLUGIN_API PlugFrame::resizeView(IPlugView* view, ViewRect* newSize)
{
    if (!view || !newSize) return kInvalidArgument;

    auto* o = _owner.load(std::memory_order_acquire);
    return o && o->resizeView(view, newSize) ? kResultTrue : kResultFalse;
}

tresult PLUGIN_API PlugFrame::queryInterface(const TUID iid, void** obj)
{
    if (FUnknownPrivate::iidEqual(iid, IPlugFrame::iid) || FUnknownPrivate::iidEqual(iid, FUnknown::iid))
    {
        addRef();
        *obj = static_cast<IPlugFrame*>(this);
        return kResultOk;
    }
#if defined(__linux__)
    if (FUnknownPrivate::iidEqual(iid, Linux::IRunLoop::iid))
        return _runLoop->queryInterface(iid, obj);
#endif
    *obj = nullptr;
    return kNoInterface;
}

uint32 PLUGIN_API PlugFrame::release()
{
    const uint32 r = --_refs;
    if (r == 0) delete this;
    return r;
}

tresult PLUGIN_API VectorStream::read(void* buffer, int32 numBytes, int32* numBytesRead)
{
    const int64_t avail = static_cast<int64_t>(_data.size()) - _pos;
    const int32 n = static_cast<int32>(std::max<int64_t>(0, std::min<int64_t>(numBytes, avail)));

    if (n > 0) std::memcpy(buffer, _data.data() + _pos, static_cast<size_t>(n));
    _pos += n;
    if (numBytesRead) *numBytesRead = n;
    return kResultOk;
}

tresult PLUGIN_API VectorStream::write(void* buffer, int32 numBytes, int32* numBytesWritten)
{
    if (numBytes < 0 || !buffer) return kInvalidArgument;

    const size_t end = static_cast<size_t>(_pos) + static_cast<size_t>(numBytes);
    if (end > _data.size()) _data.resize(end);
    std::memcpy(_data.data() + _pos, buffer, static_cast<size_t>(numBytes));
    _pos += numBytes;
    if (numBytesWritten) *numBytesWritten = numBytes;
    return kResultOk;
}

tresult PLUGIN_API VectorStream::seek(int64 pos, int32 mode, int64* result)
{
    int64_t next;
    switch (mode)
    {
        case kIBSeekSet: next = pos; break;
        case kIBSeekCur: next = _pos + pos; break;
        case kIBSeekEnd: next = static_cast<int64_t>(_data.size()) + pos; break;
        default:         return kInvalidArgument;
    }
    if (next < 0) return kResultFalse;

    _pos = next;
    if (result) *result = _pos;
    return kResultOk;
}

tresult PLUGIN_API VectorStream::tell(int64* pos)
{
    if (pos) *pos = _pos;
    return kResultOk;
}

tresult PLUGIN_API VectorStream::getStreamSize(int64& size)
{
    size = static_cast<int64>(_data.size());
    return kResultOk;
}

tresult PLUGIN_API VectorStream::setStreamSize(int64 size)
{
    if (size < 0) return kInvalidArgument;
    _data.resize(static_cast<size_t>(size));
    return kResultOk;
}

tresult PLUGIN_API VectorStream::queryInterface(const TUID iid, void** obj)
{
    if (FUnknownPrivate::iidEqual(iid, IBStream::iid) || FUnknownPrivate::iidEqual(iid, FUnknown::iid))
    {
        *obj = static_cast<IBStream*>(this);
        return kResultOk;
    }
    if (FUnknownPrivate::iidEqual(iid, ISizeableStream::iid))
    {
        *obj = static_cast<ISizeableStream*>(this);
        return kResultOk;
    }
    *obj = nullptr;
    return kNoInterface;
}

tresult PLUGIN_API FixedParamQueue::getPoint(int32 index, int32& sampleOffset, ParamValue& value)
{
    if (index < 0 || index >= _count) return kResultFalse;
    sampleOffset = _points[index].offset;
    value        = _points[index].value;
    return kResultTrue;
}

tresult PLUGIN_API FixedParamQueue::addPoint(int32 sampleOffset, ParamValue value, int32& index)
{
    if (_count > 0 && _points[_count - 1].offset >= sampleOffset)
    {
        _points[_count - 1].value = value;
        index = _count - 1;
        return kResultTrue;
    }
    if (_count == kMaxPoints)
    {
        _points[_count - 1] = { sampleOffset, value };
        index = _count - 1;
        return kResultTrue;
    }

    _points[_count] = { sampleOffset, value };
    index = _count++;
    return kResultTrue;
}

tresult PLUGIN_API FixedParamQueue::queryInterface(const TUID iid, void** obj)
{
    if (FUnknownPrivate::iidEqual(iid, IParamValueQueue::iid) || FUnknownPrivate::iidEqual(iid, FUnknown::iid))
    {
        *obj = static_cast<IParamValueQueue*>(this);
        return kResultOk;
    }
    *obj = nullptr;
    return kNoInterface;
}

bool FixedParameterChanges::add(ParamID id, int32 sampleOffset, ParamValue value) noexcept
{
    int32 index = 0;
    auto* q = addParameterData(id, index);
    if (!q) return false;

    int32 point = 0;
    q->addPoint(sampleOffset, value, point);
    return true;
}

IParamValueQueue* PLUGIN_API FixedParameterChanges::getParameterData(int32 index)
{
    return index >= 0 && index < _used ? &_queues[static_cast<size_t>(index)] : nullptr;
}

IParamValueQueue* PLUGIN_API FixedParameterChanges::addParameterData(const ParamID& id, int32& index)
{
    for (int32 i = 0; i < _used; ++i)
    {
        if (_queues[static_cast<size_t>(i)].getParameterId() == id)
        {
            index = i;
            return &_queues[static_cast<size_t>(i)];
        }
    }

    if (_used >= static_cast<int32>(_queues.size())) return nullptr;

    index = _used;
    auto& q = _queues[static_cast<size_t>(_used++)];
    q.reset(id);
    return &q;
}

tresult PLUGIN_API FixedParameterChanges::queryInterface(const TUID iid, void** obj)
{
    if (FUnknownPrivate::iidEqual(iid, IParameterChanges::iid) || FUnknownPrivate::iidEqual(iid, FUnknown::iid))
    {
        *obj = static_cast<IParameterChanges*>(this);
        return kResultOk;
    }
    *obj = nullptr;
    return kNoInterface;
}
