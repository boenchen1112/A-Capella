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

    // Task 38: one registered ARA audio source -- the model object plus the raw buffer it reads
    // from. Held alive by the owning AcaAraSession until aca_ara_release_audio_source or the
    // session itself is destroyed.
    struct AcaAraAudioSourceHandle
    {
        std::unique_ptr<ARAHostModel::AudioSource> araSource;
        std::unique_ptr<AraAudioSourceBuffer> buffer;
    };

    // Task 38: one ARA hosting session -- a Melodyne instance bound to its own DocumentController.
    // Member order matters for destruction (reverse declaration order): audioSources must be torn
    // down (deregistered from the DocumentController) before documentController itself is
    // destroyed, and documentController (whose destructor calls back into the plugin) must run
    // before instance is destroyed.
    struct AcaAraSession
    {
        std::unique_ptr<AudioPluginInstance> instance;
        std::unique_ptr<ARAHostDocumentController> documentController;
        ARAHostModel::PlugInExtensionInstance extensionInstance;
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

        ARAFactoryWrapper factory;
        createARAFactoryAsync(*instance, [&factory](ARAFactoryWrapper f) { factory = std::move(f); });
        if (factory.get() == nullptr)
        {
            copyToBuffer(outError, outErrorSize, "failed to obtain ARA factory from plugin");
            return nullptr;
        }

        auto documentController = ARAHostDocumentController::create(
            factory, "Acapella Document",
            std::make_unique<AcaAudioAccessController>(),
            std::make_unique<AcaArchivingController>());
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
