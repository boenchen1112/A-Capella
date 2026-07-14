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
#include <memory>
#include <vector>
#include <algorithm>
#include <mutex>
#include <map>

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

    // Mirrors HostBridge.cpp's normalizeToStereo (private to that translation unit, so duplicated
    // here rather than shared across a header -- same reasoning as copyToBuffer above). Forces the
    // ARA-hosted instance's main bus to plain stereo so aca_ara_render_block can always assume
    // exactly 2 output channels regardless of Melodyne's default layout.
    void normalizeToStereo(AudioPluginInstance& instance)
    {
        auto layout = instance.getBusesLayout();
        for (int i = 0; i < layout.inputBuses.size(); ++i)
            layout.inputBuses.getReference(i) = (i == 0) ? AudioChannelSet::stereo() : AudioChannelSet::disabled();
        for (int i = 0; i < layout.outputBuses.size(); ++i)
            layout.outputBuses.getReference(i) = (i == 0) ? AudioChannelSet::stereo() : AudioChannelSet::disabled();

        if (instance.checkBusesLayoutSupported(layout))
            instance.setBusesLayout(layout);
    }

    // aca_initialize() (HostBridge.cpp) is the only thing allowed to bind JUCE's MessageManager --
    // this module can't see HostBridge.cpp's private g_initialized flag (separate translation
    // unit), so it uses MessageManager::getInstanceWithoutCreating() as an equivalent proxy: the
    // ScopedJuceInitialiser_GUI held for the DLL's lifetime by aca_initialize() has already forced
    // creation by the time any real work should happen here.
    bool juceIsInitialized()
    {
        return MessageManager::getInstanceWithoutCreating() != nullptr;
    }

    // Task 38: raw sample storage an ARA audio source reads from. Not owned -- channelPointers
    // point into caller-supplied buffers (C#-owned float arrays, kept alive by the caller for the
    // audio source's lifetime, mirroring HostBridge.cpp's "caller owns all buffers passed in" rule).
    struct AraAudioSourceBuffer
    {
        std::vector<const float*> channelPointers;
        int64 numSamples = 0;
        int numChannels = 0;
        double sampleRate = 44100.0;
    };

    // Task 38: host-side implementation of ARA's mandatory AudioAccessControllerInterface. The
    // audioSourceHostRef passed to createAudioReaderForSource is exactly the AraAudioSourceBuffer*
    // this bridge chose when registering the audio source (see aca_ara_register_audio_source) --
    // no separate host-ref-to-buffer map needed, the host ref IS the buffer pointer.
    class AcaAudioAccessController : public ARA::Host::AudioAccessControllerInterface
    {
    public:
        ARA::ARAAudioReaderHostRef createAudioReaderForSource(ARA::ARAAudioSourceHostRef audioSourceHostRef, bool use64BitSamples) noexcept override
        {
            auto* reader = new ReaderState{ reinterpret_cast<AraAudioSourceBuffer*>(audioSourceHostRef), use64BitSamples };
            return reinterpret_cast<ARA::ARAAudioReaderHostRef>(reader);
        }

        bool readAudioSamples(ARA::ARAAudioReaderHostRef audioReaderHostRef, ARA::ARASamplePosition samplePosition,
                               ARA::ARASampleCount samplesPerChannel, void* const buffers[]) noexcept override
        {
            auto* reader = reinterpret_cast<ReaderState*>(audioReaderHostRef);
            auto* src = reader->source;
            int channels = jmin(src->numChannels, (int) src->channelPointers.size());

            for (int ch = 0; ch < channels; ++ch)
            {
                for (ARA::ARASampleCount i = 0; i < samplesPerChannel; ++i)
                {
                    int64 pos = samplePosition + i;
                    float sample = (pos >= 0 && pos < src->numSamples) ? src->channelPointers[(size_t) ch][pos] : 0.0f;
                    if (reader->use64BitSamples)
                        static_cast<double*>(buffers[ch])[i] = (double) sample;
                    else
                        static_cast<float*>(buffers[ch])[i] = sample;
                }
            }
            return true;
        }

        void destroyAudioReader(ARA::ARAAudioReaderHostRef audioReaderHostRef) noexcept override
        {
            delete reinterpret_cast<ReaderState*>(audioReaderHostRef);
        }

    private:
        struct ReaderState { AraAudioSourceBuffer* source; bool use64BitSamples; };
    };

    // Task 38 (minimal slice): mandatory ArchivingControllerInterface implementation. Real
    // save/load persistence keyed by (layerId, sourceAudioHash) is task 40's job -- this satisfies
    // ARAHostDocumentController::create()'s required parameter with a working-but-empty archive so
    // Document Controller creation and audio source registration (this task's actual scope) can be
    // exercised without task 40 existing yet. A plugin that never triggers document persistence
    // during analysis-only use (Melodyne doesn't) never calls into this in that flow.
    class AcaArchivingController : public ARA::Host::ArchivingControllerInterface
    {
    public:
        ARA::ARASize getArchiveSize(ARA::ARAArchiveReaderHostRef) noexcept override { return 0; }
        bool readBytesFromArchive(ARA::ARAArchiveReaderHostRef, ARA::ARASize, ARA::ARASize, ARA::ARAByte[]) noexcept override { return false; }
        bool writeBytesToArchive(ARA::ARAArchiveWriterHostRef, ARA::ARASize, ARA::ARASize, const ARA::ARAByte[]) noexcept override { return true; }
        void notifyDocumentArchivingProgress(float) noexcept override {}
        void notifyDocumentUnarchivingProgress(float) noexcept override {}
        ARA::ARAPersistentID getDocumentArchiveID(ARA::ARAArchiveReaderHostRef) noexcept override { return "com.acapella.ara.archive.v1"; }
    };

    // Task 39: ContentAccessControllerInterface and PlaybackControllerInterface are optional per
    // ARAHostDocumentController::create()'s signature (defaultable to nullptr), but Melodyne did
    // not begin analysis at all when they were omitted -- passing real (if minimal) stub
    // implementations, matching JUCE's own reference ARA host
    // (extras/AudioPluginHost/Source/Plugins/ARAPlugin.h), fixed it. Reports no musical-context
    // content available (tempo/bar signatures) since this host has none to offer; Melodyne's own
    // pitch analysis of the audio source itself doesn't depend on that.
    class AcaContentAccessController : public ARA::Host::ContentAccessControllerInterface
    {
    public:
        bool isMusicalContextContentAvailable(ARA::ARAMusicalContextHostRef, ARA::ARAContentType) noexcept override { return false; }
        ARA::ARAContentGrade getMusicalContextContentGrade(ARA::ARAMusicalContextHostRef, ARA::ARAContentType) noexcept override { return ARA::kARAContentGradeInitial; }
        ARA::ARAContentReaderHostRef createMusicalContextContentReader(ARA::ARAMusicalContextHostRef, ARA::ARAContentType, const ARA::ARAContentTimeRange*) noexcept override { return nullptr; }
        bool isAudioSourceContentAvailable(ARA::ARAAudioSourceHostRef, ARA::ARAContentType) noexcept override { return false; }
        ARA::ARAContentGrade getAudioSourceContentGrade(ARA::ARAAudioSourceHostRef, ARA::ARAContentType) noexcept override { return ARA::kARAContentGradeInitial; }
        ARA::ARAContentReaderHostRef createAudioSourceContentReader(ARA::ARAAudioSourceHostRef, ARA::ARAContentType, const ARA::ARAContentTimeRange*) noexcept override { return nullptr; }
        ARA::ARAInt32 getContentReaderEventCount(ARA::ARAContentReaderHostRef) noexcept override { return 0; }
        const void* getContentReaderDataForEvent(ARA::ARAContentReaderHostRef, ARA::ARAInt32) noexcept override { return nullptr; }
        void destroyContentReader(ARA::ARAContentReaderHostRef) noexcept override {}
    };

    class AcaPlaybackController : public ARA::Host::PlaybackControllerInterface
    {
    public:
        void requestStartPlayback() noexcept override {}
        void requestStopPlayback() noexcept override {}
        void requestSetPlaybackPosition(ARA::ARATimePosition) noexcept override {}
        void requestSetCycleRange(ARA::ARATimePosition, ARA::ARATimeDuration) noexcept override {}
        void requestEnableCycle(bool) noexcept override {}
    };

    // Task 39: mandatory ModelUpdateControllerInterface -- Melodyne reports analysis progress
    // through this as it processes a registered audio source (started -> updated* -> completed).
    // Keyed by the same host ref AcaAudioAccessController uses (the AraAudioSourceBuffer*), so
    // aca_ara_get_analysis_progress can look a source's progress up directly with no extra mapping.
    class AcaModelUpdateController : public ARA::Host::ModelUpdateControllerInterface
    {
    public:
        void notifyAudioSourceAnalysisProgress(ARA::ARAAudioSourceHostRef audioSourceHostRef, ARA::ARAAnalysisProgressState state, float value) noexcept override
        {
            const std::lock_guard<std::mutex> lock(mutex);
            float progress = (state == ARA::kARAAnalysisProgressCompleted) ? 1.0f : value;
            progressByHostRef[audioSourceHostRef] = progress;
        }

        void notifyAudioSourceContentChanged(ARA::ARAAudioSourceHostRef, const ARA::ARAContentTimeRange*, ARA::ContentUpdateScopes) noexcept override {}
        void notifyAudioModificationContentChanged(ARA::ARAAudioModificationHostRef, const ARA::ARAContentTimeRange*, ARA::ContentUpdateScopes) noexcept override {}
        void notifyPlaybackRegionContentChanged(ARA::ARAPlaybackRegionHostRef, const ARA::ARAContentTimeRange*, ARA::ContentUpdateScopes) noexcept override {}

        // -1 if this source has never reported progress yet (analysis not started/requested).
        float getProgress(ARA::ARAAudioSourceHostRef audioSourceHostRef) noexcept
        {
            const std::lock_guard<std::mutex> lock(mutex);
            auto it = progressByHostRef.find(audioSourceHostRef);
            return it != progressByHostRef.end() ? it->second : -1.0f;
        }

    private:
        std::mutex mutex;
        std::map<ARA::ARAAudioSourceHostRef, float> progressByHostRef;
    };

    // Task 39: minimal AudioPlayHead so Melodyne's playback renderer knows which sample range to
    // render on each processBlock call -- an ARA plugin in playback-renderer role generates output
    // purely from its model driven by playhead time, ignoring the input buffer entirely (matches
    // JUCE's own reference host's SimplePlayHead).
    struct AraPlayHead : public juce::AudioPlayHead
    {
        Optional<PositionInfo> getPosition() const override
        {
            PositionInfo info;
            info.setTimeInSamples(timeInSamples.load());
            info.setIsPlaying(true);
            return info;
        }

        std::atomic<int64> timeInSamples{ 0 };
    };

    // Task 38: one registered ARA audio source -- the model object plus the raw buffer it reads
    // from. Held alive by the owning AcaAraSession until aca_ara_release_audio_source or the
    // session itself is destroyed. Task 39 adds the optional playback-region chain (modification +
    // region), created lazily by aca_ara_add_playback_region once analysis/render is needed --
    // registering a source and rendering it are separate steps in the real ARA lifecycle.
    struct AcaAraAudioSourceHandle
    {
        std::unique_ptr<ARAHostModel::AudioSource> araSource;
        std::unique_ptr<AraAudioSourceBuffer> buffer;
        std::unique_ptr<ARAHostModel::AudioModification> modification;
        std::unique_ptr<ARAHostModel::PlaybackRegion> playbackRegion;
    };

    // Task 38: one ARA hosting session -- a Melodyne instance bound to its own DocumentController.
    // Member order matters for destruction (reverse declaration order) -- audioSources is declared
    // LAST specifically so it's destroyed FIRST: each AcaAraAudioSourceHandle's PlaybackRegion and
    // AudioModification reference this session's musicalContext/regionSequence (by ARA plugin ref,
    // not a live C++ pointer, but the plugin-side model graph still expects the referenced objects
    // to still exist), so those must outlive every audio source's model objects. playbackRenderer's
    // own destructor is safe regardless of whether it runs before or after a given PlaybackRegion's
    // (see PlaybackRegionRegistry's doc comment -- either order is a supported case), so its
    // position relative to audioSources doesn't matter, but everything must still precede
    // documentController (whose destructor calls back into the plugin) and instance (destroyed last
    // of all).
    struct AcaAraSession
    {
        std::unique_ptr<AudioPluginInstance> instance;
        std::unique_ptr<ARAHostDocumentController> documentController;
        ARAHostModel::PlugInExtensionInstance extensionInstance;
        ARAHostModel::PlaybackRendererInterface playbackRenderer;
        AraPlayHead playHead;
        bool prepared = false;
        double sampleRate = 44100.0;
        int maxBlockSize = 512;
        AcaModelUpdateController* modelUpdateController = nullptr; // owned by documentController, borrowed here for polling
        std::unique_ptr<ARAHostModel::MusicalContext> musicalContext;
        std::unique_ptr<ARAHostModel::RegionSequence> regionSequence;
        std::vector<std::unique_ptr<AcaAraAudioSourceHandle>> audioSources;
    };
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

    // Task 38: creates an ARA hosting session -- instantiates the plugin (mirrors HostBridge.cpp's
    // aca_create_instance), obtains its ARA factory (VST3's createARAFactoryAsync resolves
    // synchronously in-process -- see juce_VST3PluginFormat.cpp's Extensions::createARAFactoryAsync
    // -- so no callback-across-the-C-ABI-boundary is needed here), creates the DocumentController,
    // and binds the plugin instance to it in playback-renderer + editor-renderer roles (2A only
    // ever needs playback rendering; editor-renderer is requested too since Melodyne's own editor
    // window may expect it). Requires aca_initialize() to have already run on this thread, same as
    // aca_create_instance.
    __declspec(dllexport) void* aca_ara_create_session(const char* pluginPath,
                                                        double sampleRate, int maxBlockSize,
                                                        char* outError, int outErrorSize)
    {
        if (!juceIsInitialized())
        {
            copyToBuffer(outError, outErrorSize, "aca_initialize() was not called before aca_ara_create_session()");
            return nullptr;
        }

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

        if (!instance->getPluginDescription().hasARAExtension)
        {
            copyToBuffer(outError, outErrorSize, "plugin does not expose an ARA factory");
            return nullptr;
        }

        normalizeToStereo(*instance);

        ARAFactoryWrapper factory;
        createARAFactoryAsync(*instance, [&factory](ARAFactoryWrapper f) { factory = std::move(f); });
        if (factory.get() == nullptr)
        {
            copyToBuffer(outError, outErrorSize, "failed to obtain ARA factory from plugin");
            return nullptr;
        }

        // Task 39: modelUpdateController's raw pointer is captured before the unique_ptr is moved
        // into create() -- ARAHostDocumentController::Impl keeps the unique_ptr alive for the
        // DocumentController's whole lifetime (see juce_ARAHosting.cpp's Impl), so the raw pointer
        // stays valid for as long as the session's documentController does.
        auto modelUpdateControllerOwned = std::make_unique<AcaModelUpdateController>();
        auto* modelUpdateControllerRaw = modelUpdateControllerOwned.get();

        auto documentController = ARAHostDocumentController::create(
            factory, "Acapella Document",
            std::make_unique<AcaAudioAccessController>(),
            std::make_unique<AcaArchivingController>(),
            std::make_unique<AcaContentAccessController>(),
            std::move(modelUpdateControllerOwned),
            std::make_unique<AcaPlaybackController>());
        if (documentController == nullptr)
        {
            copyToBuffer(outError, outErrorSize, "ARAHostDocumentController::create failed");
            return nullptr;
        }

        // Matches JUCE's own reference ARA host (extras/AudioPluginHost/Source/Plugins/ARAPlugin.h)
        // exactly: request all three roles, both known and assigned. An earlier attempt at a
        // narrower kARAPlaybackRendererRole-only request failed to bind against Melodyne.
        const auto allRoles = ARA::kARAPlaybackRendererRole | ARA::kARAEditorRendererRole | ARA::kARAEditorViewRole;
        auto extensionInstance = documentController->bindDocumentToPluginInstance(*instance, allRoles, allRoles);
        if (!extensionInstance.isValid())
        {
            copyToBuffer(outError, outErrorSize, "failed to bind plugin instance to ARA document controller");
            return nullptr;
        }

        auto* session = new AcaAraSession();
        session->instance = std::move(instance);
        session->documentController = std::move(documentController);
        session->extensionInstance = extensionInstance;
        session->modelUpdateController = modelUpdateControllerRaw;
        session->playbackRenderer = extensionInstance.getPlaybackRendererInterface();
        session->sampleRate = sampleRate;
        session->maxBlockSize = maxBlockSize;
        session->instance->setPlayHead(&session->playHead);
        return session;
    }

    // Task 38: registers an ARA audio source backed by a caller-owned planar float buffer (one
    // pointer per channel; the caller -- eventually C#'s Melodyne pitch-stage wrapper, task 39 --
    // must keep the buffers alive for as long as the returned handle is live). Returns nullptr on
    // failure. persistentId must be unique within the session's document (task 40 will derive it
    // from (layerId, sourceAudioHash); this bridge just forwards whatever string it's given).
    __declspec(dllexport) void* aca_ara_register_audio_source(void* sessionHandle,
                                                                const float* const* channelBuffers, int numChannels,
                                                                int64 numSamples, double sourceSampleRate,
                                                                const char* persistentId,
                                                                char* outError, int outErrorSize)
    {
        if (sessionHandle == nullptr)
        {
            copyToBuffer(outError, outErrorSize, "null session handle");
            return nullptr;
        }
        if (channelBuffers == nullptr || numChannels <= 0 || numSamples <= 0)
        {
            copyToBuffer(outError, outErrorSize, "invalid audio buffer arguments");
            return nullptr;
        }

        auto* session = static_cast<AcaAraSession*>(sessionHandle);

        auto buffer = std::make_unique<AraAudioSourceBuffer>();
        buffer->channelPointers.assign(channelBuffers, channelBuffers + numChannels);
        buffer->numSamples = numSamples;
        buffer->numChannels = numChannels;
        buffer->sampleRate = sourceSampleRate;

        auto props = ARAHostModel::AudioSource::getEmptyProperties();
        props.name = "Acapella Layer";
        props.persistentID = persistentId;
        props.sampleCount = buffer->numSamples;
        props.sampleRate = sourceSampleRate;
        props.channelCount = numChannels;
        props.merits64BitSamples = false;

        auto hostRef = reinterpret_cast<ARA::ARAAudioSourceHostRef>(buffer.get());

        auto handle = std::make_unique<AcaAraAudioSourceHandle>();
        handle->araSource = std::make_unique<ARAHostModel::AudioSource>(
            hostRef, session->documentController->getDocumentController(), props);
        handle->araSource->enableAudioSourceSamplesAccess(true);
        handle->buffer = std::move(buffer);

        auto* raw = handle.get();
        session->audioSources.push_back(std::move(handle));
        return raw;
    }

    // Task 39: attaches a registered audio source to a playback region so Melodyne will analyze it
    // and the host can render its (pitch-corrected) output back. Lazily creates the session's one
    // shared MusicalContext/RegionSequence on first call (a single-track document is enough for one
    // layer's pitch stage). The region spans the whole source untransformed (kARAPlaybackTransformationNoChanges
    // -- 2A only needs "run it through Melodyne's model," not time-stretch/pitch-shift region editing).
    // Adding a region must happen while the instance is unprepared (ARA's documented requirement --
    // see PlaybackRegionRegistry's doc comment), so this also does the session's one-time
    // prepareToPlay *after* the region is added, not before.
    __declspec(dllexport) int aca_ara_add_playback_region(void* sessionHandle, void* audioSourceHandle, char* outError, int outErrorSize)
    {
        if (sessionHandle == nullptr || audioSourceHandle == nullptr)
        {
            copyToBuffer(outError, outErrorSize, "null session or audio source handle");
            return 0;
        }

        auto* session = static_cast<AcaAraSession*>(sessionHandle);
        auto* source = static_cast<AcaAraAudioSourceHandle*>(audioSourceHandle);
        auto& dc = session->documentController->getDocumentController();

        if (session->musicalContext == nullptr)
        {
            auto contextProps = ARAHostModel::MusicalContext::getEmptyProperties();
            contextProps.name = "Acapella Document";
            contextProps.orderIndex = 0;
            contextProps.color = nullptr;
            session->musicalContext = std::make_unique<ARAHostModel::MusicalContext>(
                reinterpret_cast<ARA::ARAMusicalContextHostRef>(session), dc, contextProps);

            auto sequenceProps = ARAHostModel::RegionSequence::getEmptyProperties();
            sequenceProps.name = "Acapella Track";
            sequenceProps.orderIndex = 0;
            sequenceProps.musicalContextRef = session->musicalContext->getPluginRef();
            sequenceProps.color = nullptr;
            session->regionSequence = std::make_unique<ARAHostModel::RegionSequence>(
                reinterpret_cast<ARA::ARARegionSequenceHostRef>(session), dc, sequenceProps);
        }

        auto modProps = ARAHostModel::AudioModification::getEmptyProperties();
        modProps.persistentID = "acapella-modification";
        source->modification = std::make_unique<ARAHostModel::AudioModification>(
            reinterpret_cast<ARA::ARAAudioModificationHostRef>(source), dc, *source->araSource, modProps);

        double durationSeconds = (double) source->buffer->numSamples / source->buffer->sampleRate;

        auto regionProps = ARAHostModel::PlaybackRegion::getEmptyProperties();
        regionProps.transformationFlags = ARA::kARAPlaybackTransformationNoChanges;
        regionProps.startInModificationTime = 0.0;
        regionProps.durationInModificationTime = durationSeconds;
        regionProps.startInPlaybackTime = 0.0;
        regionProps.durationInPlaybackTime = durationSeconds;
        regionProps.musicalContextRef = session->musicalContext->getPluginRef();
        regionProps.regionSequenceRef = session->regionSequence->getPluginRef();
        regionProps.name = nullptr;
        regionProps.color = nullptr;

        source->playbackRegion = std::make_unique<ARAHostModel::PlaybackRegion>(
            reinterpret_cast<ARA::ARAPlaybackRegionHostRef>(source), dc, *source->modification, regionProps);

        session->playbackRenderer.add(*source->playbackRegion);

        if (!session->prepared)
        {
            session->instance->prepareToPlay(session->sampleRate, session->maxBlockSize);
            session->prepared = true;
        }

        return 1;
    }

    // Task 39: -1 if this source's analysis hasn't reported any progress yet, else 0..1 (1 meaning
    // kARAAnalysisProgressCompleted). Melodyne triggers analysis itself once it has read access to
    // a source with an attached playback region -- there is no separate "start analysis" call in
    // this bridge (enableAudioSourceSamplesAccess(true), already set at registration, plus a region
    // attached via aca_ara_add_playback_region is what Melodyne needs to begin).
    __declspec(dllexport) float aca_ara_get_analysis_progress(void* sessionHandle, void* audioSourceHandle)
    {
        if (sessionHandle == nullptr || audioSourceHandle == nullptr) return -1.0f;
        auto* session = static_cast<AcaAraSession*>(sessionHandle);
        auto* source = static_cast<AcaAraAudioSourceHandle*>(audioSourceHandle);
        if (session->modelUpdateController == nullptr) return -1.0f;

        auto hostRef = reinterpret_cast<ARA::ARAAudioSourceHostRef>(source->buffer.get());
        return session->modelUpdateController->getProgress(hostRef);
    }

    // Task 39: renders numSamples of stereo output starting at startSampleInRegion (region-relative,
    // i.e. 0 is the start of the source) through Melodyne's playback renderer. The plugin ignores
    // whatever's in the input buffer in this role -- output is generated purely from the model
    // driven by the play head this bridge sets just before each call. Requires
    // aca_ara_add_playback_region to have already run for this source (prepareToPlay happens there).
    __declspec(dllexport) int aca_ara_render_block(void* sessionHandle, void* audioSourceHandle,
                                                    int64 startSampleInRegion,
                                                    float* outL, float* outR, int numSamples)
    {
        if (sessionHandle == nullptr || audioSourceHandle == nullptr) return 0;
        auto* session = static_cast<AcaAraSession*>(sessionHandle);
        auto* source = static_cast<AcaAraAudioSourceHandle*>(audioSourceHandle);
        if (!session->prepared || source->playbackRegion == nullptr) return 0;

        session->playHead.timeInSamples.store(startSampleInRegion);

        AudioBuffer<float> buffer(2, numSamples);
        buffer.clear();
        MidiBuffer midi;
        session->instance->processBlock(buffer, midi);

        std::memcpy(outL, buffer.getReadPointer(0), sizeof(float) * (size_t) numSamples);
        std::memcpy(outR, buffer.getReadPointer(1), sizeof(float) * (size_t) numSamples);
        return 1;
    }

    // Task 38: deregisters and frees one audio source. Safe to call with a handle from a different
    // session (no-op) since it only searches this session's own list.
    __declspec(dllexport) void aca_ara_release_audio_source(void* sessionHandle, void* audioSourceHandle)
    {
        if (sessionHandle == nullptr || audioSourceHandle == nullptr) return;
        auto* session = static_cast<AcaAraSession*>(sessionHandle);
        auto* target = static_cast<AcaAraAudioSourceHandle*>(audioSourceHandle);

        auto it = std::find_if(session->audioSources.begin(), session->audioSources.end(),
            [target](const std::unique_ptr<AcaAraAudioSourceHandle>& h) { return h.get() == target; });
        if (it != session->audioSources.end())
            session->audioSources.erase(it);
    }

    // Task 38: tears down the whole session (audio sources, document controller, plugin instance)
    // in the safe order -- see AcaAraSession's member-order comment.
    __declspec(dllexport) void aca_ara_destroy_session(void* sessionHandle)
    {
        delete static_cast<AcaAraSession*>(sessionHandle);
    }
}
