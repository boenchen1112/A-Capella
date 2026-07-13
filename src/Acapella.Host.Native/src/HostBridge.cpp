// v6 Phase P3a tasks 1-6, 8, 10: the real C-ABI hosting bridge, built on the sequence proven by
// JuceHostProbe.cpp (probe 2). Editor windows (task 7) and polled state-change detection (task 9)
// are deliberately deferred -- both are [human]-tagged UI concerns, not on the critical path for
// P3's mix-chain integration.
//
// Handle model: opaque void* -> AcaPluginInstance*, one per hosted plugin instance. No callbacks
// across the boundary; caller (C#) owns all buffers passed in.
//
// Bus-layout normalization: every instance is forced to plain stereo-in/stereo-out at instantiation
// time (extra buses, e.g. Pro-Q 4/Pro-L 2's sidechain input, disabled). JuceHostProbe.cpp's first
// run crashed inside processBlock because the probe's buffer was sized for 2 channels while
// getTotalNumInputChannels() reported 4 (main + sidechain) -- forcing the layout here means every
// downstream caller (P3's HostedPluginSampleProvider) can always assume exactly 2-in/2-out and never
// has to special-case a plugin's bus layout.
#include <juce_audio_processors/juce_audio_processors.h>
#include <juce_events/juce_events.h>
#include <memory>
#include <cstring>

#if defined(_DEBUG) && defined(_MSC_VER)
#define _CRTDBG_MAP_ALLOC
#include <crtdbg.h>
#endif

using namespace juce;

namespace
{
    // ScopedJuceInitialiser_GUI must exist for the lifetime of any plugin instance (VST3 hosting
    // relies on the JUCE message thread being initialised). Created lazily on first use, held for
    // the lifetime of the DLL -- there is no explicit shutdown hook available from a plain C ABI.
    ScopedJuceInitialiser_GUI& juceInit()
    {
#if defined(_DEBUG) && defined(_MSC_VER)
        // Debug-CRT assertions (e.g. _CrtIsValidHeapPointer on a corrupt heap) default to a modal
        // dialog box. In a non-interactive test-runner process there is nobody to click it, so the
        // process just hangs forever with no diagnostic in the log. Route assertions to stderr
        // instead so a heap bug fails the process loudly and visibly, regardless of root cause.
        //
        // Investigated once (see Message_to_Claude_Code.md / commit history): the full 96-test
        // suite was re-run with _CRTDBG_CHECK_ALWAYS_DF also enabled (full heap walk on every
        // alloc/free -- reliably catches overruns/double-frees at the exact call site, not just at
        // exit). It passed clean with no corruption report, so that expensive flag is NOT left on
        // permanently here (it made the suite ~10x slower); re-enable it locally if this class of
        // bug resurfaces.
        static bool crtConfigured = []
        {
            _CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_FILE | _CRTDBG_MODE_DEBUG);
            _CrtSetReportFile(_CRT_ASSERT, _CRTDBG_FILE_STDERR);
            _CrtSetReportMode(_CRT_ERROR, _CRTDBG_MODE_FILE | _CRTDBG_MODE_DEBUG);
            _CrtSetReportFile(_CRT_ERROR, _CRTDBG_FILE_STDERR);
            return true;
        }();
        (void) crtConfigured;
#endif
        static ScopedJuceInitialiser_GUI init;
        return init;
    }

    struct AcaPluginInstance
    {
        std::unique_ptr<AudioPluginInstance> instance;
        AudioBuffer<float> scratch; // 2-channel scratch buffer reused across processBlock calls
        MidiBuffer midi;
    };

    void copyToBuffer(char* outBuffer, int outBufferSize, const String& value)
    {
        if (outBuffer == nullptr || outBufferSize <= 0)
            return;
        auto utf8 = value.toRawUTF8();
        auto len = (int) strlen(utf8);
        auto n = jmin(len, outBufferSize - 1);
        memcpy(outBuffer, utf8, (size_t) n);
        outBuffer[n] = '\0';
    }

    // Force plain stereo in/out; disable every other bus (sidechains etc.) so callers never need
    // to reason about a plugin-specific channel count.
    void normalizeToStereo(AudioPluginInstance& instance)
    {
        auto layout = instance.getBusesLayout();
        for (int i = 0; i < layout.inputBuses.size(); ++i)
            layout.inputBuses.getReference(i) = (i == 0) ? AudioChannelSet::stereo() : AudioChannelSet::disabled();
        for (int i = 0; i < layout.outputBuses.size(); ++i)
            layout.outputBuses.getReference(i) = (i == 0) ? AudioChannelSet::stereo() : AudioChannelSet::disabled();

        if (instance.checkBusesLayoutSupported(layout))
            instance.setBusesLayout(layout);
        // If the forced layout isn't supported, leave the plugin's default layout in place --
        // ProcessBlock below always allocates/copies against a 2-channel buffer regardless, so a
        // plugin that insists on more main-bus channels than stereo would simply not be supported
        // by this bridge (none of the five target FabFilter plugins hit this case).
    }
}

extern "C"
{
    // Task 1: scan. Returns 1 if at least one plugin type was found in pluginPath, else 0.
    // Writes the first found type's name/version into caller-provided buffers (truncated, NUL-terminated).
    __declspec(dllexport) int aca_scan_plugin(const char* pluginPath,
                                               char* outName, int outNameSize,
                                               char* outVersion, int outVersionSize)
    {
        juceInit();
        VST3PluginFormat format;
        OwnedArray<PluginDescription> descriptions;
        format.findAllTypesForFile(descriptions, String(pluginPath));

        if (descriptions.isEmpty())
            return 0;

        copyToBuffer(outName, outNameSize, descriptions[0]->name);
        copyToBuffer(outVersion, outVersionSize, descriptions[0]->version);
        return 1;
    }

    // Task 2/3: instantiate + prepare. Returns an opaque handle, or nullptr on failure.
    // sampleRate/maxBlockSize are applied immediately via prepareToPlay (no separate prepare call --
    // VST3 plugins need a sample rate/block size at construction time for their own internal setup,
    // so splitting instantiate from prepare would just mean prepare is always called right after).
    __declspec(dllexport) void* aca_create_instance(const char* pluginPath,
                                                     double sampleRate, int maxBlockSize,
                                                     char* outError, int outErrorSize)
    {
        juceInit();
        VST3PluginFormat format;
        OwnedArray<PluginDescription> descriptions;
        format.findAllTypesForFile(descriptions, String(pluginPath));

        if (descriptions.isEmpty())
        {
            copyToBuffer(outError, outErrorSize, "no plugin types found at path");
            return nullptr;
        }

        String errorMessage;
        auto instance = format.createInstanceFromDescription(*descriptions[0], sampleRate, maxBlockSize, errorMessage);
        if (instance == nullptr)
        {
            copyToBuffer(outError, outErrorSize, errorMessage);
            return nullptr;
        }

        normalizeToStereo(*instance);
        instance->prepareToPlay(sampleRate, maxBlockSize);

        auto* handle = new AcaPluginInstance();
        handle->instance = std::move(instance);
        handle->scratch.setSize(2, maxBlockSize);
        return handle;
    }

    // Task 5: latency + tail queries.
    __declspec(dllexport) int aca_get_latency_samples(void* handle)
    {
        if (handle == nullptr) return 0;
        return static_cast<AcaPluginInstance*>(handle)->instance->getLatencySamples();
    }

    __declspec(dllexport) double aca_get_tail_seconds(void* handle)
    {
        if (handle == nullptr) return 0.0;
        return static_cast<AcaPluginInstance*>(handle)->instance->getTailLengthSeconds();
    }

    // Task 4: parameter enumerate/get/set.
    __declspec(dllexport) int aca_get_parameter_count(void* handle)
    {
        if (handle == nullptr) return 0;
        return static_cast<AcaPluginInstance*>(handle)->instance->getParameters().size();
    }

    __declspec(dllexport) int aca_get_parameter_name(void* handle, int index, char* outName, int outNameSize)
    {
        if (handle == nullptr) return 0;
        auto& params = static_cast<AcaPluginInstance*>(handle)->instance->getParameters();
        if (index < 0 || index >= params.size()) return 0;
        copyToBuffer(outName, outNameSize, params[index]->getName(512));
        return 1;
    }

    __declspec(dllexport) float aca_get_parameter_value(void* handle, int index)
    {
        if (handle == nullptr) return 0.0f;
        auto& params = static_cast<AcaPluginInstance*>(handle)->instance->getParameters();
        if (index < 0 || index >= params.size()) return 0.0f;
        return params[index]->getValue();
    }

    __declspec(dllexport) void aca_set_parameter_value(void* handle, int index, float value)
    {
        if (handle == nullptr) return;
        auto& params = static_cast<AcaPluginInstance*>(handle)->instance->getParameters();
        if (index < 0 || index >= params.size()) return;
        params[index]->setValue(value);
    }

    // Task 6: stereo-pair audio processing. inL/inR/outL/outR are caller-allocated, numSamples long.
    // out may alias in (processed in place via the internal scratch buffer).
    __declspec(dllexport) void aca_process_block(void* handle,
                                                  const float* inL, const float* inR,
                                                  float* outL, float* outR,
                                                  int numSamples)
    {
        if (handle == nullptr) return;
        auto* h = static_cast<AcaPluginInstance*>(handle);

        if (h->scratch.getNumSamples() < numSamples)
            h->scratch.setSize(2, numSamples, false, false, true);

        auto* l = h->scratch.getWritePointer(0);
        auto* r = h->scratch.getWritePointer(1);
        memcpy(l, inL, sizeof(float) * (size_t) numSamples);
        memcpy(r, inR, sizeof(float) * (size_t) numSamples);

        AudioBuffer<float> block(h->scratch.getArrayOfWritePointers(), 2, numSamples);
        h->midi.clear();
        h->instance->processBlock(block, h->midi);

        memcpy(outL, block.getReadPointer(0), sizeof(float) * (size_t) numSamples);
        memcpy(outR, block.getReadPointer(1), sizeof(float) * (size_t) numSamples);
    }

    // Task 8: state persistence (VST3 component+controller state chunk, as a raw byte blob).
    __declspec(dllexport) int aca_get_state_size(void* handle)
    {
        if (handle == nullptr) return 0;
        MemoryBlock block;
        static_cast<AcaPluginInstance*>(handle)->instance->getStateInformation(block);
        return (int) block.getSize();
    }

    // Returns the number of bytes written (0 on failure / no state / buffer too small).
    __declspec(dllexport) int aca_get_state(void* handle, unsigned char* outBuffer, int bufferSize)
    {
        if (handle == nullptr || outBuffer == nullptr) return 0;
        MemoryBlock block;
        static_cast<AcaPluginInstance*>(handle)->instance->getStateInformation(block);
        auto n = (int) block.getSize();
        if (n <= 0 || n > bufferSize) return 0;
        memcpy(outBuffer, block.getData(), (size_t) n);
        return n;
    }

    __declspec(dllexport) void aca_set_state(void* handle, const unsigned char* data, int dataSize)
    {
        if (handle == nullptr || data == nullptr || dataSize <= 0) return;
        static_cast<AcaPluginInstance*>(handle)->instance->setStateInformation(data, dataSize);
    }

    // Task 10: release. Safe to call with nullptr (no-op).
    __declspec(dllexport) void aca_release_instance(void* handle)
    {
        if (handle == nullptr) return;
        auto* h = static_cast<AcaPluginInstance*>(handle);
        h->instance->releaseResources();
        delete h;
    }
}
