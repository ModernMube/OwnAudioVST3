// No framework: TEST registers itself, CHECK records and goes on. Exit code = failures.

#include "core/AuIdentifier.h"
#include "core/DelayLine.h"
#include "core/MpscQueue.h"
#include "core/StateCodec.h"
#include "scan/CacheXml.h"
#include "vst3/MidiToVst.h"

#include "public.sdk/source/vst/utility/uid.h"

#include <cmath>
#include <cstdio>
#include <cstring>
#include <functional>
#include <string>
#include <thread>
#include <vector>

namespace
{
    struct TestCase { const char* name; std::function<void()> fn; };
    std::vector<TestCase>& registry() { static std::vector<TestCase> r; return r; }
    int g_failures = 0;

    struct Registrar { Registrar(const char* n, std::function<void()> f) { registry().push_back({ n, std::move(f) }); } };
}

#define TEST(name) static void name(); static Registrar reg_##name(#name, name); static void name()
#define CHECK(cond) do { if (!(cond)) { std::printf("    FAILED %s:%d  %s\n", __FILE__, __LINE__, #cond); ++g_failures; } } while (0)

using namespace Steinberg;
using namespace Steinberg::Vst;

TEST(base64_round_trips_every_length)
{
    for (size_t n = 0; n < 70; ++n)
    {
        std::vector<uint8_t> in(n);
        for (size_t i = 0; i < n; ++i) in[i] = static_cast<uint8_t>(i * 37 + 11);

        const auto text = StateCodec::encodeBase64Lsb(in.data(), in.size());
        CHECK(text.rfind(std::to_string(n) + ".", 0) == 0);

        std::vector<uint8_t> out;
        CHECK(StateCodec::decodeBase64Lsb(text, out));
        CHECK(out == in);
    }
}

TEST(base64_is_low_bit_first)
{
    const uint8_t one = 0x01;
    CHECK(StateCodec::encodeBase64Lsb(&one, 1) == "1.A.");

    const uint8_t ff[3] = { 0xff, 0xff, 0xff };
    CHECK(StateCodec::encodeBase64Lsb(ff, 3) == "3.++++");
}

TEST(base64_decodes_reference_blob)
{
    std::vector<uint8_t> out;
    CHECK(StateCodec::decodeBase64Lsb("8.yLyL+......", out));
    CHECK(out.size() == 8);
    CHECK(StateCodec::encodeBase64Lsb(out.data(), out.size()) == "8.yLyL+......");
}

TEST(base64_rejects_garbage)
{
    std::vector<uint8_t> out;
    CHECK(!StateCodec::decodeBase64Lsb("", out));
    CHECK(!StateCodec::decodeBase64Lsb("abc", out));
    CHECK(!StateCodec::decodeBase64Lsb("x.AAAA", out));
    CHECK(!StateCodec::decodeBase64Lsb("4.AA*A", out));
}

TEST(vst3_container_layout)
{
    StateCodec::Vst3State s;
    s.hasComponent = true;
    s.component    = { 1, 2, 3, 250 };

    const auto blob = StateCodec::writeVst3(s);
    CHECK(blob.size() > 9);
    CHECK(blob[0] == 'V' && blob[1] == 'C' && blob[2] == '2' && blob[3] == '!');

    const uint32_t len = blob[4] | (blob[5] << 8) | (blob[6] << 16) | (static_cast<uint32_t>(blob[7]) << 24);
    CHECK(len == blob.size() - 9);
    CHECK(blob.back() == 0);

    const std::string xml(reinterpret_cast<const char*>(blob.data() + 8), len);
    CHECK(xml.rfind("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", 0) == 0);
    CHECK(xml.find("<VST3PluginState><IComponent>4.") != std::string::npos);
    CHECK(xml.find("IEditController") == std::string::npos);
}

TEST(vst3_container_round_trips)
{
    StateCodec::Vst3State s;
    s.hasComponent  = true;
    s.hasController = true;
    for (int i = 0; i < 1000; ++i) s.component.push_back(static_cast<uint8_t>(i));
    s.controller = { 9, 8, 7 };

    const auto blob = StateCodec::writeVst3(s);
    StateCodec::Vst3State back;
    CHECK(StateCodec::readVst3(blob.data(), blob.size(), back));
    CHECK(back.hasComponent && back.hasController);
    CHECK(back.component == s.component);
    CHECK(back.controller == s.controller);
}

TEST(vst3_container_reads_reference_header)
{
    const char xml[] = "<?xml version=\"1.0\" encoding=\"UTF-8\"?> <VST3PluginState><IComponent>8.yLyL+......</IComponent></VST3PluginState>";
    std::vector<uint8_t> blob = { 0x56, 0x43, 0x32, 0x21, 0, 0, 0, 0 };
    const uint32_t n = sizeof(xml) - 1;
    blob[4] = static_cast<uint8_t>(n);
    blob.insert(blob.end(), xml, xml + n);
    blob.push_back(0);

    StateCodec::Vst3State s;
    CHECK(StateCodec::readVst3(blob.data(), blob.size(), s));
    CHECK(s.hasComponent && !s.hasController);
    CHECK(s.component.size() == 8);
}

TEST(vst3_legacy_blob_is_not_a_container)
{
    const uint8_t raw[] = { 'L', 'P', 'X', 'X', 1, 2, 3, 4, 5 };
    StateCodec::Vst3State s;
    CHECK(!StateCodec::hasVst3Header(raw, sizeof(raw)));
    CHECK(!StateCodec::readVst3(raw, sizeof(raw), s));
}

TEST(plist_detection)
{
    const char bin[] = "bplist00\xd0\x08";
    const char xml[] = "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict/></plist>";
    const char vc2[] = "VC2!\x00\x00\x00\x00";
    CHECK(StateCodec::looksLikePlist(reinterpret_cast<const uint8_t*>(bin), sizeof(bin) - 1));
    CHECK(StateCodec::looksLikePlist(reinterpret_cast<const uint8_t*>(xml), sizeof(xml) - 1));
    CHECK(!StateCodec::looksLikePlist(reinterpret_cast<const uint8_t*>(vc2), 8));
}

TEST(cache_round_trips)
{
    std::vector<PluginRecord> in(2);
    in[0] = { "ADelay", "Steinberg Media Technologies", "3.8.1.0", "Fx|Delay", "/Library/Audio/Plug-Ins/VST3/adelay.vst3", "VST3", false, 2, 2, 0x777bacbf };
    in[1] = { "DLS & \"Synth\"", "Apple", "1.6.0", "Synths", "AudioUnit:Synths/aumu,dls ,appl", "AudioUnit", true, 0, 4, -5 };

    std::vector<PluginRecord> out;
    CHECK(ownvst3_readCacheXml(ownvst3_writeCacheXml(in).c_str(), out));
    CHECK(out.size() == 2);
    for (size_t i = 0; i < out.size() && i < in.size(); ++i)
    {
        CHECK(out[i].name == in[i].name);
        CHECK(out[i].vendor == in[i].vendor);
        CHECK(out[i].version == in[i].version);
        CHECK(out[i].category == in[i].category);
        CHECK(out[i].identifier == in[i].identifier);
        CHECK(out[i].formatName == in[i].formatName);
        CHECK(out[i].isInstrument == in[i].isInstrument);
        CHECK(out[i].numInputs == in[i].numInputs);
        CHECK(out[i].numOutputs == in[i].numOutputs);
        CHECK(out[i].uniqueId == in[i].uniqueId);
    }
}

TEST(cache_reads_reference_format)
{
    const char* xml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n\n<KNOWNPLUGINS>\n"
        "  <PLUGIN name=\"ADelay\" format=\"VST3\" category=\"Fx|Delay\" manufacturer=\"Steinberg Media Technologies\"\n"
        "          version=\"3.8.1.0\" file=\"/Library/Audio/Plug-Ins/VST3/adelay.vst3\"\n"
        "          uniqueId=\"fed6ff1a\" isInstrument=\"0\" fileTime=\"1a173f4f12d\" infoUpdateTime=\"1a174100368\"\n"
        "          numInputs=\"0\" numOutputs=\"0\" isShell=\"0\" hasARAExtension=\"0\"\n"
        "          uid=\"777bacbf\"/>\n"
        "  <SOMETHINGELSE/>\n"
        "</KNOWNPLUGINS>\n";

    std::vector<PluginRecord> out;
    CHECK(ownvst3_readCacheXml(xml, out));
    CHECK(out.size() == 1);
    if (out.size() == 1)
    {
        CHECK(out[0].name == "ADelay");
        CHECK(out[0].uniqueId == static_cast<int32_t>(0xfed6ff1au));
        CHECK(out[0].numInputs == 0);
        CHECK(out[0].category == "Fx|Delay");
    }
}

TEST(cache_rejects_malformed)
{
    std::vector<PluginRecord> out;
    CHECK(!ownvst3_readCacheXml("<KNOWNPLUGINS><PLUGIN", out));
    CHECK(!ownvst3_readCacheXml("<OTHER/>", out));
    CHECK(!ownvst3_readCacheXml("", out));
    CHECK(!ownvst3_readCacheXml(nullptr, out));
}

TEST(uid_hash_matches_cache)
{
#if !defined(_WIN32)
    auto uid = VST3::UID::fromString(std::string("0CDBB66985D548A9BFD8371909D24BB3"));
    CHECK(static_cast<bool>(uid));
    if (uid) CHECK(ownvst3_hash31(reinterpret_cast<const uint8_t*>(uid->data()), 16) == static_cast<int32_t>(0x777bacbfu));
#endif
}

TEST(absolute_paths)
{
    CHECK(ownvst3_isAbsolutePath("/x/y.vst3"));
    CHECK(ownvst3_isAbsolutePath("C:\\Program Files\\Common Files\\VST3\\a.vst3"));
    CHECK(ownvst3_isAbsolutePath("\\\\server\\share\\a.vst3"));
    CHECK(!ownvst3_isAbsolutePath("AudioUnit:Effects/aufx,dely,appl"));
    CHECK(!ownvst3_isAbsolutePath(""));
}

TEST(au_identifier_round_trips)
{
    const AuComponentId dls { ownvst3_fourCC("aumu"), ownvst3_fourCC("dls "), ownvst3_fourCC("appl") };
    CHECK(ownvst3_makeAuIdentifier(dls) == "AudioUnit:Synths/aumu,dls ,appl");

    AuComponentId back;
    CHECK(ownvst3_parseAuIdentifier("AudioUnit:Synths/aumu,dls ,appl", back));
    CHECK(back.type == dls.type && back.subtype == dls.subtype && back.manufacturer == dls.manufacturer);
}

TEST(au_identifier_folders)
{
    CHECK(ownvst3_auCategoryFolder(ownvst3_fourCC("aufx")) == "Effects/");
    CHECK(ownvst3_auCategoryFolder(ownvst3_fourCC("aumf")) == "Effects/");
    CHECK(ownvst3_auCategoryFolder(ownvst3_fourCC("augn")) == "Generators/");
    CHECK(ownvst3_auCategoryFolder(ownvst3_fourCC("aupn")) == "Panners/");
    CHECK(ownvst3_auCategoryFolder(ownvst3_fourCC("aumx")) == "Mixers/");
    CHECK(ownvst3_auCategoryFolder(ownvst3_fourCC("aumi")) == "MidiEffects/");
    CHECK(ownvst3_auCategoryFolder(ownvst3_fourCC("auou")).empty());
}

TEST(au_identifier_parse_variants)
{
    AuComponentId id;
    CHECK(ownvst3_parseAuIdentifier("AudioUnit:aufx,dely,appl", id));
    CHECK(id.subtype == ownvst3_fourCC("dely"));
    CHECK(ownvst3_parseAuIdentifier("AudioUnit:Effects/aufx,dely,appl", id));
    CHECK(!ownvst3_parseAuIdentifier("AudioUnit:Effects/aufx,dely", id));
    CHECK(!ownvst3_parseAuIdentifier("AudioUnit:Effects/aufx,delay,appl", id));
    CHECK(!ownvst3_parseAuIdentifier("/Library/Audio/Plug-Ins/Components/x.component", id));
}

TEST(midi_note_on_off)
{
    MidiMapTable map;
    Event ev; ParamID pid; double v;

    CHECK(ownvst3_convertMidi({ 0x93, 60, 127, 0, 10 }, 64, map, ev, pid, v) == MidiConversion::Event);
    CHECK(ev.type == Event::kNoteOnEvent);
    CHECK(ev.noteOn.channel == 3 && ev.noteOn.pitch == 60);
    CHECK(std::fabs(ev.noteOn.velocity - 1.0f) < 1e-6f);
    CHECK(ev.noteOn.noteId == -1);
    CHECK(ev.sampleOffset == 10);

    CHECK(ownvst3_convertMidi({ 0x80, 60, 64, 0, 0 }, 64, map, ev, pid, v) == MidiConversion::Event);
    CHECK(ev.type == Event::kNoteOffEvent);
    CHECK(std::fabs(ev.noteOff.velocity - 64 / 127.0f) < 1e-6f);

    CHECK(ownvst3_convertMidi({ 0x90, 61, 0, 0, 0 }, 64, map, ev, pid, v) == MidiConversion::Event);
    CHECK(ev.type == Event::kNoteOffEvent && ev.noteOff.pitch == 61);
}

TEST(midi_offset_is_clamped)
{
    MidiMapTable map;
    Event ev; ParamID pid; double v;

    ownvst3_convertMidi({ 0x90, 60, 100, 0, 500 }, 64, map, ev, pid, v);
    CHECK(ev.sampleOffset == 63);
    ownvst3_convertMidi({ 0x90, 60, 100, 0, -4 }, 64, map, ev, pid, v);
    CHECK(ev.sampleOffset == 0);
}

TEST(midi_controllers_go_through_the_map)
{
    MidiMapTable map;
    map.ids[0][1]           = 42;
    map.ids[2][kPitchBend]  = 7;
    map.ids[0][kAfterTouch] = 9;

    Event ev; ParamID pid = 0; double v = 0;

    CHECK(ownvst3_convertMidi({ 0xB0, 1, 127, 0, 0 }, 64, map, ev, pid, v) == MidiConversion::Parameter);
    CHECK(pid == 42 && std::fabs(v - 1.0) < 1e-9);

    CHECK(ownvst3_convertMidi({ 0xB0, 2, 64, 0, 0 }, 64, map, ev, pid, v) == MidiConversion::None);

    CHECK(ownvst3_convertMidi({ 0xE2, 0x00, 0x40, 0, 0 }, 64, map, ev, pid, v) == MidiConversion::Parameter);
    CHECK(pid == 7 && std::fabs(v - 8192 / 16383.0) < 1e-9);

    CHECK(ownvst3_convertMidi({ 0xE2, 0x7F, 0x7F, 0, 0 }, 64, map, ev, pid, v) == MidiConversion::Parameter);
    CHECK(std::fabs(v - 1.0) < 1e-9);

    CHECK(ownvst3_convertMidi({ 0xD0, 127, 0, 0, 0 }, 64, map, ev, pid, v) == MidiConversion::Parameter);
    CHECK(pid == 9 && std::fabs(v - 1.0) < 1e-9);

    CHECK(ownvst3_convertMidi({ 0xE5, 0, 64, 0, 0 }, 64, map, ev, pid, v) == MidiConversion::None);
}

TEST(midi_poly_pressure_and_unknown)
{
    MidiMapTable map;
    Event ev; ParamID pid; double v;

    CHECK(ownvst3_convertMidi({ 0xA1, 64, 127, 0, 0 }, 64, map, ev, pid, v) == MidiConversion::Event);
    CHECK(ev.type == Event::kPolyPressureEvent && ev.polyPressure.channel == 1);
    CHECK(ownvst3_convertMidi({ 0xF8, 0, 0, 0, 0 }, 64, map, ev, pid, v) == MidiConversion::None);
    CHECK(ownvst3_convertMidi({ 0xC0, 5, 0, 0, 0 }, 64, map, ev, pid, v) == MidiConversion::None);
}

TEST(delay_line_delays_by_latency)
{
    DelayLine d;
    d.prepare(2, 5);

    std::vector<float> inL(16), inR(16), outL(16), outR(16);
    for (int i = 0; i < 16; ++i) { inL[static_cast<size_t>(i)] = static_cast<float>(i + 1); inR[static_cast<size_t>(i)] = -static_cast<float>(i + 1); }

    float* in[2]  = { inL.data(), inR.data() };
    float* out[2] = { outL.data(), outR.data() };

    d.process(in, out, 2, 7);
    float* in2[2]  = { inL.data() + 7, inR.data() + 7 };
    float* out2[2] = { outL.data() + 7, outR.data() + 7 };
    d.process(in2, out2, 2, 9);

    for (int i = 0; i < 16; ++i)
    {
        const float want = i < 5 ? 0.0f : static_cast<float>(i - 5 + 1);
        CHECK(outL[static_cast<size_t>(i)] == want);
        CHECK(outR[static_cast<size_t>(i)] == -want);
    }
}

TEST(delay_line_in_place_and_zero)
{
    DelayLine d;
    d.prepare(1, 0);
    std::vector<float> x = { 1, 2, 3 };
    float* p[1] = { x.data() };
    d.process(p, p, 1, 3);
    CHECK(x[0] == 1 && x[2] == 3);

    d.prepare(1, 2);
    d.process(p, p, 1, 3);
    CHECK(x[0] == 0 && x[1] == 0 && x[2] == 1);
}

TEST(mpsc_fifo_and_full)
{
    MpscQueue<int, 4> q;
    for (int i = 0; i < 4; ++i) CHECK(q.tryEnqueue(i));
    CHECK(!q.tryEnqueue(99));

    int v = -1;
    for (int i = 0; i < 4; ++i) { CHECK(q.tryDequeue(v)); CHECK(v == i); }
    CHECK(!q.tryDequeue(v));
}

TEST(mpsc_many_producers)
{
    MpscQueue<int, 1024> q;
    std::vector<std::thread> producers;
    for (int t = 0; t < 4; ++t)
        producers.emplace_back([&q, t] { for (int i = 0; i < 200; ++i) while (!q.tryEnqueue(t * 1000 + i)) {} });

    long long sum = 0;
    int got = 0, v = 0;
    while (got < 800)
        if (q.tryDequeue(v)) { sum += v; ++got; }

    for (auto& p : producers) p.join();

    long long want = 0;
    for (int t = 0; t < 4; ++t) for (int i = 0; i < 200; ++i) want += t * 1000 + i;
    CHECK(sum == want);
}

int main()
{
    int failedTests = 0;
    for (auto& t : registry())
    {
        const int before = g_failures;
        t.fn();
        const bool ok = g_failures == before;
        std::printf("%s %s\n", ok ? "[ OK ]" : "[FAIL]", t.name);
        if (!ok) ++failedTests;
    }
    std::printf("%zu tests, %d failed\n", registry().size(), failedTests);
    return failedTests;
}
