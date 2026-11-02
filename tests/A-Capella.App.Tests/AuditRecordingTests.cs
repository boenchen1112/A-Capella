using System.Reflection;
using A_Capella.Engine.Project;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Xunit;

namespace A_Capella.App.Tests;

/// <summary>Recording setup, the Record button, per-strip Record, Retake and Calibrate, driven against
/// a stand-in capture device (ffmpeg synthetic test video and a sine tone).</summary>
public class AuditRecordingTests
{
    private static void ClickIn(RecordSetupWindow dialog, string handler)
    {
        var method = typeof(RecordSetupWindow).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance)
                     ?? throw new MissingMethodException(handler);
        method.Invoke(dialog, new object?[] { dialog, new RoutedEventArgs() });
    }

    /// <summary>Answers the Recording setup dialog by recording for a moment and pressing Stop.</summary>
    private static Func<Avalonia.Controls.Window, Task<bool?>> RecordATake(AuditEnv env, int seconds = 2, Action<RecordSetupWindow>? before = null)
    {
        return dialog =>
        {
            var setup = (RecordSetupWindow)dialog;
            before?.Invoke(setup);
            ClickIn(setup, "RecordButton_Click");
            env.Settle(seconds * 1000);
            ClickIn(setup, "RecordButton_Click");     // Stop
            env.Settle(500);
            return Task.FromResult<bool?>(setup.CreatedLayer is not null || setup.RetakeResult is not null);
        };
    }

    [AvaloniaFact]
    public void RecordingSetup_ListsCamerasAndMicrophones_ButNotScreenCapture()
    {
        using var env = new AuditEnv();
        env.InstallFakeCaptureDevices();
        RecordSetupWindow? seen = null;
        List<string>? cameras = null, mics = null;
        env.ModalScript = dialog =>
        {
            seen = (RecordSetupWindow)dialog;
            cameras = seen.CameraCombo.Items.Cast<string>().ToList();
            mics = seen.MicCombo.Items.Cast<string>().ToList();
            return Task.FromResult<bool?>(false);
        };

        env.Click("RecordingSetupMenuItem_Click");
        env.Settle(300);

        Assert.NotNull(seen);
        Assert.Equal(new[] { "Test Camera" }, cameras);
        Assert.Equal(new[] { "Test Mic" }, mics);
        Assert.Equal(0, seen!.CameraCombo.SelectedIndex);
        Assert.Empty(env.Layers);
    }

    [AvaloniaFact]
    public void RecordButton_RecordsAFirstLayer_IntoSlotOne_AsOneUndoStep()
    {
        using var env = new AuditEnv();
        env.InstallFakeCaptureDevices();
        env.ModalScript = RecordATake(env);

        env.Click("RecordButton_Click");
        env.Settle(600);

        var layer = Assert.Single(env.Layers);
        Assert.Equal(LayerKind.RecordedAV, layer.Kind);
        Assert.Equal(0, layer.CellIndex);
        Assert.True(File.Exists(layer.SourcePath));
        Assert.StartsWith(env.Window.MediaDir, layer.SourcePath);
        string streams = TestMedia.Probe(layer.SourcePath, "stream=codec_type");
        Assert.Contains("codec_type=video", streams);
        Assert.Contains("codec_type=audio", streams);
        double seconds = double.Parse(TestMedia.Probe(layer.SourcePath, "format=duration").Split('=')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(seconds, 1.0, 4.0);
        Assert.Equal("Layer 1", env.Rows[0].Name);
        Assert.Equal("Recorded Layer 1.", env.Status);
        Assert.True(env.Session.IsDirty);

        env.Click("UndoMenuItem_Click");
        Assert.Empty(env.Layers);
    }

    [AvaloniaFact]
    public void StripRecord_RecordsIntoThatStrip()
    {
        using var env = new AuditEnv();
        env.InstallFakeCaptureDevices();
        env.ModalScript = RecordATake(env);

        env.Click("RecordChoice_Click", env.Rows[2]);
        env.Settle(600);

        var layer = Assert.Single(env.Layers);
        Assert.Equal(2, layer.CellIndex);
        Assert.Same(layer, env.Rows[2].Layer);
        Assert.Null(env.Rows[0].Layer);
        Assert.Contains("Layer 3", env.Rows[2].Name);
    }

    [AvaloniaFact]
    public void RecordButton_AfterAnImport_FillsTheLowestFreeSlot()
    {
        using var env = new AuditEnv();
        env.InstallFakeCaptureDevices();
        env.Import(TestMedia.Tone(env.Dir, "guide", 440, 3));
        env.ModalScript = RecordATake(env);

        env.Click("RecordButton_Click");
        env.Settle(600);

        Assert.Equal(2, env.Layers.Count);
        Assert.Equal(new[] { 0, 1 }, env.Layers.Select(l => l.CellIndex).OrderBy(i => i));
        Assert.Equal(LayerKind.RecordedAV, env.Rows[1].Layer!.Kind);
    }

    [AvaloniaFact]
    public void Record_CancelledDialog_CreatesNothing()
    {
        using var env = new AuditEnv();
        env.InstallFakeCaptureDevices();
        env.ModalScript = _ => Task.FromResult<bool?>(false);

        env.Click("RecordButton_Click");
        env.Settle(300);

        Assert.Empty(env.Layers);
        Assert.False(env.Session.IsDirty);
    }

    [AvaloniaFact]
    public void Record_WithAFullProject_ReportsTheCap_WithoutOpeningTheDialog()
    {
        using var env = new AuditEnv();
        env.InstallFakeCaptureDevices();
        env.Import(Enumerable.Range(0, LayerCollection.MaxLayers).Select(i => TestMedia.Tone(env.Dir, $"t{i}", 200 + 20 * i, 1)).ToArray());
        bool opened = false;
        env.ModalScript = _ => { opened = true; return Task.FromResult<bool?>(false); };

        env.Click("RecordButton_Click");
        string status = env.Status;   // read now: background alignments keep rewriting the status line
        env.Settle(200);

        Assert.False(opened);
        Assert.Equal($"Layer cap reached ({LayerCollection.MaxLayers}).", status);
    }

    [AvaloniaFact]
    public void RecordingSetup_RemembersTheLastDevicesAndMetronome_AndCarriesBpmAndMeterBack()
    {
        using var env = new AuditEnv();
        env.InstallFakeCaptureDevices();
        env.ModalScript = dialog =>
        {
            var setup = (RecordSetupWindow)dialog;
            setup.MetronomeToggle.IsChecked = true;
            setup.BpmTextBox.Text = "101";
            setup.TimeSignatureCombo.SelectedIndex = 1;
            setup.Close(false);
            env.Settle(100);
            return Task.FromResult<bool?>(false);
        };

        env.Click("RecordingSetupMenuItem_Click");
        env.Settle(300);

        Assert.Equal(101, env.Session.MetronomeBpm);
        Assert.NotEqual(A_Capella.Engine.Metronome.TimeSignature.FourFour, env.Session.MetronomeTimeSignature);
        Assert.Equal(env.Session.MetronomeTimeSignature, env.Window.TimeSignatureCombo.SelectedItem);
        var (camera, mic, metronome) = env.Window.SettingsInUse.GetLastRecordingSetup();
        Assert.Equal("Test Camera", camera);
        Assert.Equal("Test Mic", mic);
        Assert.True(metronome);
    }

    [AvaloniaFact]
    public void Record_WithNoCaptureDevices_AsksToSelectThem()
    {
        using var env = new AuditEnv();
        string shim = env.Path_("no-devices.sh");
        File.WriteAllText(shim, "#!/bin/bash\nexit 0\n");
        System.Diagnostics.Process.Start("chmod", new[] { "+x", shim })!.WaitForExit();
        env.Window.DeviceCatalogInUse = new A_Capella.Engine.Devices.DeviceCatalog(shim);
        string? text = null;
        env.ModalScript = dialog =>
        {
            var setup = (RecordSetupWindow)dialog;
            ClickIn(setup, "RecordButton_Click");
            text = setup.StatusText.Text;
            return Task.FromResult<bool?>(false);
        };

        env.Click("RecordButton_Click");
        env.Settle(300);

        Assert.Equal("Select a camera and microphone first.", text);
    }

    [AvaloniaFact]
    public void Retake_ReRecord_ReplacesTheSourceInPlace_AndUndoRestoresIt()
    {
        using var env = new AuditEnv();
        env.InstallFakeCaptureDevices();
        string original = TestMedia.Video(env.Dir, "original", 2);
        env.Import(original);
        env.Rows[0].Name = "Lead";
        int layerId = env.Layers[0].LayerId;
        env.Session.CommitEdit();
        env.ModalScript = RecordATake(env);

        env.CallAsync("RetakeRecordAsync", env.Rows[0]);
        env.Settle(300);

        var layer = Assert.Single(env.Layers);
        Assert.Equal(layerId, layer.LayerId);
        Assert.Equal("Lead", env.Rows[0].Name);
        Assert.NotEqual(original, layer.SourcePath);
        Assert.Equal(LayerKind.RecordedAV, layer.Kind);
        Assert.True(File.Exists(layer.SourcePath));
        Assert.Equal("Re-recorded Lead.", env.Status);

        env.Click("UndoMenuItem_Click");
        Assert.Equal(original, env.Layers[0].SourcePath);
    }

    [AvaloniaFact]
    public void Retake_ReplaceWithFile_KeepsTheLayer_AndResetsOffsets()
    {
        using var env = new AuditEnv();
        env.Import(TestMedia.Video(env.Dir, "orig", 2));
        env.Rows[0].Name = "Lead";
        env.Rows[0].Nudge(40);
        int layerId = env.Layers[0].LayerId;
        string replacement = TestMedia.Tone(env.Dir, "replacement", 550, 2);
        env.PickFilesAnswers.Enqueue(new[] { replacement });

        env.CallAsync("RetakeReplaceWithFileAsync", env.Rows[0]);

        var layer = Assert.Single(env.Layers);
        Assert.Equal(layerId, layer.LayerId);
        Assert.Equal(replacement, layer.SourcePath);
        Assert.Equal(LayerKind.UploadedAudioOnly, layer.Kind);
        Assert.Equal(0, layer.ManualOffsetMs);
        Assert.Equal("Lead", env.Rows[0].Name);
        Assert.StartsWith("Replaced Lead with replacement.wav", env.Status);
    }

    [AvaloniaFact]
    public void RetakeButton_OnAnEmptyStrip_DoesNothing()
    {
        using var env = new AuditEnv();
        env.Click("RetakeButton_Click", env.Rows[0]);
        env.Settle(100);
        Assert.Empty(env.Layers);
    }

    [AvaloniaFact]
    public void CalibrateLatency_OpensTheSameDialog_AndNeverCreatesALayer()
    {
        using var env = new AuditEnv();
        env.InstallFakeCaptureDevices();
        RecordSetupWindow? seen = null;
        env.ModalScript = dialog => { seen = (RecordSetupWindow)dialog; return Task.FromResult<bool?>(false); };

        env.Click("CalibrateLatencyMenuItem_Click");
        env.Settle(300);

        Assert.NotNull(seen);
        Assert.True(seen!.CalibrateButton.IsEnabled);
        Assert.Empty(env.Layers);
        Assert.False(env.Session.IsDirty);
    }

    [AvaloniaFact(Skip = "Needs a microphone: opening the audio input stalls the test host when there is no input device or permission, so the measurement itself is left for a check with real hardware")]
    public void CalibrateButton_MeasuresRoundTripLatency()
    {
        using var env = new AuditEnv();
        string? text = null;
        env.ModalScript = dialog =>
        {
            var setup = (RecordSetupWindow)dialog;
            ClickIn(setup, "CalibrateButton_Click");
            text = setup.CalibrationResultText.Text;
            return Task.FromResult<bool?>(false);
        };
        env.Click("CalibrateLatencyMenuItem_Click");
        env.Settle(300);
        Assert.Matches("Calibrated offset", text!);
    }
}
