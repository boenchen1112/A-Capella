// v6 Phase P3a probe 2 (go/no-go gate): proves a real commercial FabFilter plugin can be scanned,
// instantiated, prepared, processed, and released with no GUI session -- the load-bearing
// assumption for every [auto] acceptance criterion in P3a/P3 (a commercial plugin could refuse to
// activate/process headlessly due to a license/GUI check). Standalone console app, not yet part
// of AcapellaHostNative.dll -- folding this into the real C-ABI bridge is a separate step once
// this is confirmed working.
#include <juce_audio_processors/juce_audio_processors.h>
#include <juce_events/juce_events.h>
#include <iostream>

using namespace juce;

static bool probePlugin(const String& pluginPath)
{
    std::cout << "== Probing: " << pluginPath << " ==" << std::endl;

    VST3PluginFormat format;
    OwnedArray<PluginDescription> descriptions;
    format.findAllTypesForFile(descriptions, pluginPath);

    if (descriptions.isEmpty())
    {
        std::cout << "  SCAN: no plugin types found." << std::endl;
        return false;
    }

    for (auto* description : descriptions)
    {
        std::cout << "  SCAN: found \"" << description->name << "\" v" << description->version
                   << " (isInstrument=" << description->isInstrument << ")" << std::endl;
    }

    const double sampleRate = 44100.0;
    const int blockSize = 512;

    String errorMessage;
    std::unique_ptr<AudioPluginInstance> instance =
        format.createInstanceFromDescription(*descriptions[0], sampleRate, blockSize, errorMessage);

    if (instance == nullptr)
    {
        std::cout << "  INSTANTIATE: FAILED - " << errorMessage << std::endl;
        return false;
    }
    std::cout << "  INSTANTIATE: ok" << std::endl;

    instance->prepareToPlay(sampleRate, blockSize);
    std::cout << "  PREPARE: ok (reported latency samples = " << instance->getLatencySamples()
               << ", tail seconds = " << instance->getTailLengthSeconds() << ")" << std::endl;
    std::cout << "  CHANNELS: in=" << instance->getTotalNumInputChannels()
               << " out=" << instance->getTotalNumOutputChannels() << std::endl;
    std::cout.flush();

    int numChannels = juce::jmax(instance->getTotalNumInputChannels(), instance->getTotalNumOutputChannels(), 1);
    AudioBuffer<float> buffer(numChannels, blockSize);
    buffer.clear();
    for (int ch = 0; ch < numChannels; ++ch)
        buffer.setSample(ch, 0, 1.0f); // impulse
    MidiBuffer midi;

    std::cout << "  (about to processBlock)" << std::endl;
    std::cout.flush();
    instance->processBlock(buffer, midi);

    float peakAfter = buffer.getMagnitude(0, blockSize);
    std::cout << "  PROCESS: ok (output peak magnitude = " << peakAfter << ")" << std::endl;

    instance->releaseResources();
    instance.reset();
    std::cout << "  RELEASE: ok" << std::endl;
    return true;
}

int main()
{
    ScopedJuceInitialiser_GUI juceInit;

    bool okL2 = probePlugin("C:\\Program Files\\Common Files\\VST3\\FabFilter Pro-L 2.vst3");
    bool okQ4 = probePlugin("C:\\Program Files\\Common Files\\VST3\\FabFilter Pro-Q 4.vst3");
    bool okC3 = probePlugin("C:\\Program Files\\Common Files\\VST3\\FabFilter Pro-C 3.vst3");
    bool okG  = probePlugin("C:\\Program Files\\Common Files\\VST3\\FabFilter Pro-G.vst3");
    bool okR2 = probePlugin("C:\\Program Files\\Common Files\\VST3\\FabFilter Pro-R 2.vst3");

    bool ok = okL2 && okQ4 && okC3 && okG && okR2;
    std::cout << (ok ? "PROBE RESULT: SUCCESS" : "PROBE RESULT: FAILURE") << std::endl;
    return ok ? 0 : 1;
}
