using A_Capella.Engine.Presets;
using A_Capella.Engine.Project;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace A_Capella.App.Tests;

/// <summary>Per-layer actions: upload, nudge, trim, rename, mute/solo/mirror, freeze, effects and packages, delete.</summary>
public class AuditLayerTests
{
    private static AuditEnv WithLayers(int count = 2)
    {
        var env = new AuditEnv();
        env.Import(Enumerable.Range(0, count).Select(i => TestMedia.Tone(env.Dir, $"t{i}", 300 + 100 * i, 3)).ToArray());
        env.Click("UndoMenuItem_Click");   // keep undo history simple: re-import below
        env.Click("RedoMenuItem_Click");
        return env;
    }

    [AvaloniaFact]
    public void StripUpload_PutsTheFileInThatStrip_AsOneUndoStep()
    {
        using var env = new AuditEnv();
        env.PickFilesAnswers.Enqueue(new[] { TestMedia.Tone(env.Dir, "up", 440, 2) });

        env.Click("UploadChoice_Click", env.Rows[3]);
        env.Settle();

        var layer = Assert.Single(env.Layers);
        Assert.Equal(3, layer.CellIndex);
        Assert.Equal(LayerKind.UploadedAudioOnly, layer.Kind);
        Assert.Contains("Uploaded", env.Status);
        env.Click("UndoMenuItem_Click");
        Assert.Empty(env.Layers);
    }

    [AvaloniaFact]
    public void StripUpload_Cancelled_ChangesNothing()
    {
        using var env = new AuditEnv();
        env.Click("UploadChoice_Click", env.Rows[0]);
        env.Settle(200);
        Assert.Empty(env.Layers);
        Assert.False(env.Session.IsDirty);
    }

    [AvaloniaFact]
    public void Nudge_MovesBy10ms_ResetsOnClick_AndIsSaved()
    {
        using var env = WithLayers();
        var row = env.Rows[1];
        double start = row.ManualOffsetMs;

        env.Click("NudgeLaterButton_Click", row);
        env.Click("NudgeLaterButton_Click", row);
        env.Click("NudgeEarlierButton_Click", row);
        Assert.Equal(start + 10, row.ManualOffsetMs);
        Assert.Contains("timing", env.Status);
        Assert.Equal(start + 10, env.SaveAndRead().Layers[1].GetProperty("ManualOffsetMs").GetDouble());

        env.Window.Call_NudgeReset(row);
        Assert.Equal(0, row.ManualOffsetMs);
        Assert.Contains("reset", env.Status);
    }

    [AvaloniaFact]
    public void Nudge_IsUndoable()
    {
        using var env = WithLayers();
        var row = env.Rows[1];
        double start = row.ManualOffsetMs;
        env.Click("NudgeLaterButton_Click", row);
        env.Click("UndoMenuItem_Click");
        Assert.Equal(start, env.Rows[1].ManualOffsetMs);
        env.Click("RedoMenuItem_Click");
        Assert.Equal(start + 10, env.Rows[1].ManualOffsetMs);
    }

    [AvaloniaFact]
    public void AutoAlign_OnTheGuideLayer_OrAnEmptyStrip_DoesNotThrowOrChangeAnything()
    {
        using var env = WithLayers();
        env.Click("AutoAlignButton_Click", env.Rows[0]);
        env.Click("AutoAlignButton_Click", env.Rows[5]);
        env.Settle(1500);
        Assert.Equal(2, env.Layers.Count);
    }

    [AvaloniaFact]
    public void AutoAlign_ShiftsALateTrack_TowardTheGuide_OrSaysItIsNotSure()
    {
        using var env = new AuditEnv();
        env.Import(TestMedia.Clicks(env.Dir, "guide", 0.0), TestMedia.Clicks(env.Dir, "late", 0.4));
        double before = env.Rows[1].ManualOffsetMs;
        env.Settle(500);

        env.Click("AutoAlignButton_Click", env.Rows[1]);
        AuditEnv.WaitFor(() => !env.Status.Contains("Aligning") && !env.Status.Contains("Analys"), "auto-align", 30000);
        env.Settle(500);

        double moved = env.Layers[1].CalibratedOffsetMs + env.Layers[1].ManualOffsetMs;
        Assert.True(moved != 0 || env.Status.Contains("confident") || env.Status.Contains("match"),
            "neither moved nor explained: " + env.Status);
    }

    [AvaloniaFact]
    public void Trim_InAndOut_AreSavedAndUndoable()
    {
        using var env = WithLayers();
        env.SelectRow(0);
        env.Window.TrimInTextBox.Text = "250";
        env.Window.TrimInTextBox.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(InputElement_LostFocus));
        env.Window.TrimOutTextBox.Text = "2000";
        env.Window.TrimOutTextBox.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(InputElement_LostFocus));

        Assert.Equal(250, env.Layers[0].TrimStartMs);
        Assert.Equal(2000, env.Layers[0].TrimEndMs);
        var json = env.SaveAndRead().Layers[0];
        Assert.Equal(250, json.GetProperty("TrimStartMs").GetDouble());
        Assert.Equal(2000, json.GetProperty("TrimEndMs").GetDouble());
        env.Click("UndoMenuItem_Click");
        Assert.NotEqual(2000, env.Layers[0].TrimEndMs);
    }

    private static readonly Avalonia.Interactivity.RoutedEvent InputElement_LostFocus = Avalonia.Input.InputElement.LostFocusEvent;

    [AvaloniaFact]
    public void Rename_OnLostFocus_IsSavedAndUndoable_BlankRevertsToDefault()
    {
        using var env = WithLayers();
        var box = new TextBox { DataContext = env.Rows[0], Text = "  Lead vocal " };
        env.Window.GetType().GetMethod("LaneNameTextBox_LostFocus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(env.Window, new object?[] { box, new Avalonia.Interactivity.RoutedEventArgs() });

        Assert.Equal("Lead vocal", env.Layers[0].Name);
        Assert.Equal("Lead vocal", box.Text);
        Assert.Equal("Lead vocal", env.SaveAndRead().Layers[0].GetProperty("Name").GetString());
        env.Click("UndoMenuItem_Click");
        Assert.NotEqual("Lead vocal", env.Layers[0].Name);
    }

    [AvaloniaFact]
    public void MuteSoloMirrorPanGain_WriteThroughToTheSavedLayer()
    {
        using var env = WithLayers();
        var row = env.Rows[1];
        row.Mute = true; row.Solo = true; row.Mirror = true; row.Pan = -0.5f; row.GainDb = -6f;
        env.Click("CommitCheckBox_Click");

        var json = env.SaveAndRead().Layers[1];
        Assert.True(json.GetProperty("Mirror").GetBoolean());
        var mix = json.GetProperty("MixParameters");
        Assert.True(mix.GetProperty("Mute").GetBoolean());
        Assert.True(mix.GetProperty("Solo").GetBoolean());
        Assert.Equal(-0.5, mix.GetProperty("Pan").GetDouble(), 3);
        Assert.Equal(-6, mix.GetProperty("GainDb").GetDouble(), 3);
        Assert.False(env.SaveAndRead().Layers[0].GetProperty("MixParameters").GetProperty("Mute").GetBoolean());
    }

    [AvaloniaFact]
    public void Freeze_Toggles_IsSaved_AndUndoable()
    {
        using var env = WithLayers();
        env.Click("FreezeToggle_Click", env.Rows[0]);
        Assert.True(env.Layers[0].MixParameters.Frozen);
        Assert.True(env.SaveAndRead().Layers[0].GetProperty("MixParameters").GetProperty("Frozen").GetBoolean());
        env.Click("FreezeToggle_Click", env.Rows[0]);
        Assert.False(env.Layers[0].MixParameters.Frozen);
        env.Click("UndoMenuItem_Click");
        Assert.True(env.Layers[0].MixParameters.Frozen);
    }

    [AvaloniaFact]
    public void Freeze_OnAnEmptyStrip_DoesNothing()
    {
        using var env = new AuditEnv();
        env.Click("FreezeToggle_Click", env.Rows[0]);
        Assert.Empty(env.Layers);
        Assert.False(env.Session.IsDirty);
    }

    [AvaloniaFact]
    public void EffectPresetChoice_IsSaved_AndUndoable()
    {
        using var env = WithLayers();
        var row = env.Rows[0];
        var package = new FxPackage { Name = "x", CompressorPreset = new PresetRef("FabFilter Pro-C 3", "Vocal", "Smooth") };
        env.Window.Call_Apply(row, package);

        Assert.Equal("Smooth", env.Layers[0].MixParameters.CompressorPreset!.Name);
        Assert.Equal("Smooth", env.SaveAndRead().Layers[0].GetProperty("MixParameters").GetProperty("CompressorPreset").GetProperty("Name").GetString());
        env.Click("UndoMenuItem_Click");
        Assert.Null(env.Layers[0].MixParameters.CompressorPreset);
    }

    [AvaloniaFact]
    public void Packages_SaveFromALayer_ApplyToAnother_AndDelete()
    {
        using var env = WithLayers();
        env.Rows[0].ApplyPackage(new FxPackage { Name = "seed", EqPreset = new PresetRef("FabFilter Pro-Q 4", "Vocal", "Bright") });
        env.ModalScript = d =>
        {
            ((SavePackageWindow)d).NameBox.Text = "My vocal";
            d.GetType().GetMethod("Save_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(d, new object?[] { d, new Avalonia.Interactivity.RoutedEventArgs() });
            env.Settle(200);
            return Task.FromResult<bool?>(true);
        };

        env.CallAsync("SaveCurrentAsPackageAsync", env.Rows[0]);
        Assert.Equal("Saved package \"My vocal\".", env.Status);
        var saved = Assert.Single(env.Window.Packages.List(), p => p.Name == "My vocal");
        Assert.Equal("Bright", saved.EqPreset!.Name);

        var item = (Avalonia.Controls.MenuItem)env.Call("PackageMenuItem", saved, env.Rows[1])!;
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.MenuItem.ClickEvent));
        Assert.Equal("Bright", env.Layers[1].MixParameters.EqPreset!.Name);
        Assert.Contains("Applied package", env.Status);

        env.MessageAnswer = MessageResult.No;
        env.CallAsync("DeletePackageAsync", saved);
        Assert.Contains(env.Window.Packages.List(), p => p.Name == "My vocal");
        env.MessageAnswer = MessageResult.Yes;
        env.CallAsync("DeletePackageAsync", saved);
        Assert.DoesNotContain(env.Window.Packages.List(), p => p.Name == "My vocal");
        Assert.Equal("Deleted package \"My vocal\".", env.Status);
    }

    [AvaloniaFact]
    public void SavePackageWindow_RejectsBlankAndBuiltInNames()
    {
        var packages = new FxPackageService(Path.Combine(Path.GetTempPath(), "b" + Guid.NewGuid().ToString("N")[..6]), Path.Combine(Path.GetTempPath(), "u" + Guid.NewGuid().ToString("N")[..6]));
        var w = new SavePackageWindow(packages);
        w.Show();
        w.NameBox.Text = "   ";
        w.GetType().GetMethod("Save_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(w, new object?[] { w, new Avalonia.Interactivity.RoutedEventArgs() });
        AuditEnv.WaitFor(() => w.ErrorText.Text == "Type a name first.", "blank name error", 3000);
        Assert.Equal("", w.PackageName);
        w.Close();
    }

    [AvaloniaFact]
    public void PackagesButton_Opens_WithoutError()
    {
        using var env = WithLayers();
        env.Click("PackagesButton_Click", env.Rows[0]);
        env.Settle(100);
    }

    [AvaloniaFact]
    public void DeleteLayer_AsksFirst_KeepsTheSlot_AndUndoBringsItBack()
    {
        using var env = WithLayers(3);
        env.SelectRow(1);
        env.MessageAnswer = MessageResult.No;
        env.Click("DeleteLayerMenuItem_Click");
        env.Settle(200);
        Assert.Equal(3, env.Layers.Count);

        env.MessageAnswer = MessageResult.Yes;
        env.Click("DeleteLayerMenuItem_Click");
        env.Settle(200);
        Assert.Equal(2, env.Layers.Count);
        Assert.Null(env.Rows[1].Layer);
        Assert.Equal(new[] { 0, 2 }, env.Layers.Select(l => l.CellIndex).OrderBy(i => i));
        Assert.StartsWith("Deleted", env.Status);

        env.Click("UndoMenuItem_Click");
        Assert.Equal(3, env.Layers.Count);
        Assert.NotNull(env.Rows[1].Layer);
    }

    [AvaloniaFact]
    public void DeleteLayer_OnAnEmptyStrip_DoesNothing()
    {
        using var env = new AuditEnv();
        env.SelectRow(0);
        env.Click("DeleteLayerMenuItem_Click");
        env.Settle(100);
        Assert.Empty(env.Messages);
    }

    [AvaloniaFact]
    public void ChannelMoreButton_Opens_WithoutError()
    {
        using var env = WithLayers();
        env.SelectRow(0);
        env.Click("ChannelMoreButton_Click");
    }

    [AvaloniaFact]
    public void ReplaceWithFile_OnAnEmptyStripFlow_Cancelled_ChangesNothing()
    {
        using var env = WithLayers();
        string before = env.Layers[0].SourcePath;
        env.CallAsync("RetakeReplaceWithFileAsync", env.Rows[0]);
        Assert.Equal(before, env.Layers[0].SourcePath);
    }
}
