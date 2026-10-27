// iOS smoke test: Apple's AUDelay through the C ABI, run in a simulator by run-ios-smoke.sh.
#include "ownvst3_exports.h"
#include <cmath>
#include <cstdio>
#include <cstring>
#include <vector>

static int fails = 0;
#define CHECK(c) do { if (!(c)) { printf("  FAIL %s:%d %s\n", __FILE__, __LINE__, #c); ++fails; } else printf("  ok   %s\n", #c); } while (0)

int main()
{
    const char* id = "AudioUnit:Effects/aufx,dely,appl";
    auto h = VST3Plugin_Create();
    CHECK(VST3Plugin_LoadPlugin(h, id));
    printf("  name=%s vendor=%s ver=%s fmt=%s id=%s\n", VST3Plugin_GetName(h), VST3Plugin_GetVendor(h),
           VST3Plugin_GetVersion(h), OwnPlugin_GetFormatName(h), OwnPlugin_GetIdentifier(h));
    CHECK(strcmp(OwnPlugin_GetFormatName(h), "AudioUnit") == 0);
    CHECK(strcmp(OwnPlugin_GetIdentifier(h), id) == 0);
    CHECK(VST3Plugin_Initialize(h, 48000, 256));
    CHECK(VST3Plugin_GetActualInputChannels(h) == 2 && VST3Plugin_GetActualOutputChannels(h) == 2);
    CHECK(VST3Plugin_IsEffect(h));
    int n = VST3Plugin_GetParameterCount(h);
    printf("  params=%d\n", n);
    CHECK(n > 0);

    VST3ParameterC p0; VST3Plugin_GetParameterAt(h, 0, &p0); printf("  p0=%s\n", p0.name);
    VST3Plugin_SetParameter(h, 0, 0.0);
    std::vector<float> l(256), r(256), ol(256), orr(256);
    for (int i = 0; i < 256; ++i) l[i] = r[i] = 0.5f * std::sin(i * 0.1f);
    float* in[2] = { l.data(), r.data() }; float* out[2] = { ol.data(), orr.data() };
    AudioBufferC b { in, out, 2, 256 };
    for (int k = 0; k < 4; ++k) CHECK(VST3Plugin_ProcessAudio(h, &b));
    float maxDiff = 0; for (int i = 0; i < 256; ++i) maxDiff = std::fmax(maxDiff, std::fabs(ol[i] - l[i]));
    printf("  dry max diff %g\n", maxDiff);
    CHECK(maxDiff < 1e-3f);

    uint8_t* st = nullptr; int len = 0;
    CHECK(VST3Plugin_GetState(h, &st, &len));
    CHECK(len > 8 && memcmp(st, "bplist00", 8) == 0);
    auto h2 = VST3Plugin_Create();
    VST3Plugin_LoadPlugin(h2, id);
    CHECK(VST3Plugin_SetState(h2, st, len));
    CHECK(std::fabs(VST3Plugin_GetParameter(h2, 0) - 0.0) < 1e-4);
    VST3Plugin_FreeStateData(st);

    CHECK(!VST3Plugin_CreateEditor(h, nullptr));
    CHECK(!OwnPlugin_AttachEditorToView(h, nullptr));
    CHECK(!VST3Plugin_LoadPlugin(h2, "/some/where/x.vst3"));

    OwnPlugin_ScanStartMode(OWNPLUGIN_FORMAT_ALL, OWNPLUGIN_SCAN_FAST);
    while (OwnPlugin_ScanIsRunning()) {}
    int found = OwnPlugin_GetScannedCount(), aus = 0;
    for (int i = 0; i < found; ++i) { PluginDescriptorC d; OwnPlugin_GetScannedAt(i, &d); if (!strcmp(d.formatName, "AudioUnit")) ++aus; }
    printf("  fast scan: %d plugins, %d AudioUnits\n", found, aus);
    CHECK(aus > 0 && aus == found);

    VST3Plugin_Destroy(h2);
    VST3Plugin_Destroy(h);
    printf("%s (%d failures)\n", fails ? "FAILED" : "PASSED", fails);
    return fails;
}
