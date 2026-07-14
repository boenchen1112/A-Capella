// v7 Phase 2A: Melodyne-specific ARA hosting surface. Kept separate from HostBridge.cpp (the
// generic plain-VST3 bridge from v6 P3a) since everything here depends on the ARA SDK
// (JUCE_PLUGINHOST_ARA, ARA_SDK/ cloned alongside JUCE/ -- see CMakeLists.txt) and plain VST3
// hosting doesn't need it.
//
// Task 37 (this file, first slice): ARA-capability detection. A plugin can be a valid, loadable
// VST3 (HostBridge.cpp's aca_scan_plugin already proves that) while still not exposing the ARA
// factory extension -- Melodyne's tier determines this (per CLAUDE.md's Environment section, tier
// is "unconfirmed at runtime" as of Q0). JUCE's VST3PluginFormat already computes this per audit
// of the plugin's factory classes (PluginDescription::hasARAExtension) independently of whether
// JUCE_PLUGINHOST_ARA is even defined -- so this check works even before the Document Controller
// bridge (task 38) exists.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include <juce_audio_processors/juce_audio_processors.h>

using namespace juce;

namespace
{
    // Mirrors HostBridge.cpp's copyToBuffer (kept private to this translation unit rather than
    // shared across a header, matching the existing HostBridge.cpp/Probe.cpp split in this module).
    void copyToBuffer(char* dest, int destSize, const String& value)
    {
        if (dest == nullptr || destSize <= 0) return;
        auto utf8 = value.toRawUTF8();
        size_t len = std::min(static_cast<size_t>(destSize - 1), strlen(utf8));
        memcpy(dest, utf8, len);
        dest[len] = '\0';
    }
}

extern "C"
{
    // Task 37: ARA-capability scan. Returns 0 if no plugin type was found at pluginPath (mirrors
    // aca_scan_plugin's not-found contract), 1 if found but not ARA-capable (Melodyne installed
    // without an ARA-enabled tier, or any plain VST3), 2 if found and ARA-capable.
    //
    // Deliberately does NOT require aca_initialize() to have run first (unlike aca_scan_plugin) --
    // findAllTypesForFile only inspects the plugin's factory metadata, it doesn't instantiate the
    // plugin or touch JUCE's MessageManager, so it's safe to call from any thread at any time. This
    // matters for Q3 task 32's off-critical-path scanning: the app can know whether Melodyne's ARA
    // tier is available before deciding whether to route a layer's pitch stage through it.
    __declspec(dllexport) int aca_scan_ara_capability(const char* pluginPath,
                                                        char* outName, int outNameSize,
                                                        char* outVersion, int outVersionSize)
    {
        VST3PluginFormat format;
        OwnedArray<PluginDescription> descriptions;
        format.findAllTypesForFile(descriptions, String(pluginPath));

        if (descriptions.isEmpty())
            return 0;

        copyToBuffer(outName, outNameSize, descriptions[0]->name);
        copyToBuffer(outVersion, outVersionSize, descriptions[0]->version);
        return descriptions[0]->hasARAExtension ? 2 : 1;
    }
}
