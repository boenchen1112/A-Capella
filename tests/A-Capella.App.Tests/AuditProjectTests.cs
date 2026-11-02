using System.Reflection;
using A_Capella.Engine.Composite;
using A_Capella.Engine.Metronome;
using A_Capella.Engine.Presets;
using A_Capella.Engine.Project;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Xunit;

namespace A_Capella.App.Tests;

/// <summary>Project-wide controls: master volume and limiter, metronome, layout, video settings, song range, edit menu, close.</summary>
public class AuditProjectTests
{
    private static AuditEnv WithLayers(int count = 2)
    {
        var env = new AuditEnv();
        env.Import(Enumerable.Range(0, count).Select(i => TestMedia.Tone(env.Dir, $"t{i}", 300 + 100 * i, 4)).ToArray());
        return env;
    }

    /// <summary>Undoes until the import is gone (an automatic alignment after it is its own undo step).</summary>
    private static void UndoImport(AuditEnv env)
    {
        for (int i = 0; i < 4 && env.Layers.Count > 0; i++) env.Click("UndoMenuItem_Click");
    }

    private static void Invoke(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.Invoke(target, args);

    private static bool Key_(AuditEnv env, Key key, KeyModifiers mods = KeyModifiers.None)
    {
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods, Source = env.Window };
        Invoke(env.Window, "MainWindow_PreviewKeyDown", env.Window, args);
        env.Settle(300);
        return args.Handled;
    }

    // ----- master and metronome -----

    [AvaloniaFact]
    public void MasterVolume_IsSaved_AndLimiterPresetIsSaved()
    {
        using var env = WithLayers();
        env.Window.MasterVolumeSlider.Value = -9;
        Invoke(env.Window, "MasterVolumeSlider_PointerReleased", env.Window.MasterVolumeSlider, null!);
        Assert.Equal(-9, env.Session.MasterVolumeDb, 3);
        Assert.Equal(-9, env.SaveAndRead().Root.GetProperty("MasterVolumeDb").GetDouble(), 3);
        env.Click("UndoMenuItem_Click");
        Assert.Equal(0, env.Session.MasterVolumeDb, 3);
    }

    [AvaloniaFact]
    public void TimeSignature_Change_IsStatusReported_SavedAndUndoable()
    {
        using var env = WithLayers();
        var other = env.Window.TimeSignatureCombo.Items.Cast<TimeSignature>().First(t => t != env.Session.MetronomeTimeSignature);
        env.Window.TimeSignatureCombo.SelectedItem = other;
        Assert.Equal(other, env.Session.MetronomeTimeSignature);
        Assert.Equal($"Metronome: {other}.", env.Status);
        var root = env.SaveAndRead().Root;
        Assert.Equal(other.BeatsPerBar, root.GetProperty("MetronomeBeatsPerBar").GetInt32());
        env.Click("UndoMenuItem_Click");
        Assert.NotEqual(other, env.Session.MetronomeTimeSignature);
        Assert.Equal(env.Session.MetronomeTimeSignature, env.Window.TimeSignatureCombo.SelectedItem);
    }

    [AvaloniaFact]
    public void MetronomeBpm_ValidIsKept_InvalidRevertsTheBox()
    {
        using var env = new AuditEnv();
        env.Window.MetronomeBpmTextBox.Text = "132";
        Invoke(env.Window, "MetronomeBpmTextBox_LostFocus", env.Window.MetronomeBpmTextBox, new RoutedEventArgs());
        Assert.Equal(132, env.Session.MetronomeBpm);
        env.Window.MetronomeBpmTextBox.Text = "fast";
        Invoke(env.Window, "MetronomeBpmTextBox_LostFocus", env.Window.MetronomeBpmTextBox, new RoutedEventArgs());
        Assert.Equal(132, env.Session.MetronomeBpm);
        Assert.StartsWith("132", env.Window.MetronomeBpmTextBox.Text);
        env.Window.MetronomeBpmTextBox.Text = "-5";
        Invoke(env.Window, "MetronomeBpmTextBox_LostFocus", env.Window.MetronomeBpmTextBox, new RoutedEventArgs());
        Assert.Equal(132, env.Session.MetronomeBpm);
    }

    [AvaloniaFact]
    public void MonitorToggle_FlipsWithoutError_BothWays()
    {
        using var env = WithLayers();
        env.Window.MonitorToggle.IsChecked = true;
        env.Window.MonitorToggle.IsChecked = false;
        env.Settle(100);
    }

    // ----- layout and video -----

    [AvaloniaFact]
    public void ChooseLayout_PicksATile_SavesIt_AndUndoRestores()
    {
        using var env = WithLayers();
        string before = env.Session.Video.LayoutId;
        string? picked = null;
        env.ModalScript = d =>
        {
            var tile = AllButtons(d).First(b => b.IsEnabled && b.Content is Canvas && !ReferenceEquals(b, null) && ((Canvas)b.Content).Children.Count == 4);
            tile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            picked = ((LayoutPickerWindow)d).SelectedLayoutId;
            return Task.FromResult<bool?>(true);
        };

        env.Click("ChooseLayoutMenuItem_Click");
        env.Settle(500);

        Assert.NotNull(picked);
        Assert.Equal(picked, env.Session.Video.LayoutId);
        Assert.Equal("4 cells", $"{env.Session.Video.Layout.Cells.Count} cells");
        Assert.Equal(picked, env.SaveAndRead().Root.GetProperty("LayoutId").GetString());
        if (picked != before)
        {
            env.Click("UndoMenuItem_Click");
            Assert.Equal(before, env.Session.Video.LayoutId);
        }
    }

    private static IEnumerable<Button> AllButtons(Window w)
    {
        var panel = (StackPanel)w.FindControl<StackPanel>("GroupsPanel")!;
        return panel.Children.OfType<WrapPanel>().SelectMany(p => p.Children.OfType<Button>());
    }

    [AvaloniaFact]
    public void LayoutPicker_DisablesLayoutsTooSmallForTheLayers()
    {
        var picker = new LayoutPickerWindow("", 4);
        picker.Show();
        var buttons = AllButtons(picker).ToList();
        Assert.Contains(buttons, b => !b.IsEnabled);
        Assert.All(buttons.Where(b => !b.IsEnabled), b => Assert.True(((Canvas)b.Content!).Children.Count < 4));
        Assert.All(buttons.Where(b => b.IsEnabled), b => Assert.True(((Canvas)b.Content!).Children.Count >= 4));
        picker.Close();
    }

    [AvaloniaFact]
    public void LayoutPicker_Cancel_ChangesNothing()
    {
        using var env = WithLayers();
        string before = env.Session.Video.LayoutId;
        env.ModalScript = _ => Task.FromResult<bool?>(false);
        env.Click("ChooseLayoutMenuItem_Click");
        env.Settle(200);
        Assert.Equal(before, env.Session.Video.LayoutId);
    }

    [AvaloniaFact]
    public void VideoSettings_Ok_AppliesBackgroundGutterAndFit_AndIsSaved()
    {
        using var env = WithLayers();
        env.ModalScript = d =>
        {
            var w = (VideoSettingsWindow)d;
            w.BackgroundColorBox.Text = "#112233";
            w.GutterSlider.Value = 5;
            w.FillRadio.IsChecked = true;
            Invoke(w, "OkButton_Click", w, new RoutedEventArgs());
            return Task.FromResult<bool?>(w.Result.Background.Color == "#112233");
        };

        env.Click("VideoSettingsMenuItem_Click");
        env.Settle(500);

        Assert.Equal("#112233", env.Session.Video.Background.Color);
        Assert.Equal(CellFit.Fill, env.Session.Video.Fit);
        Assert.Equal(0.05, env.Session.Video.GutterFraction, 3);
        Assert.Equal("Video settings updated.", env.Status);
        Assert.Equal("#112233", env.SaveAndRead().Root.GetProperty("Video").GetProperty("BackgroundColor").GetString());
        env.Click("UndoMenuItem_Click");
        Assert.NotEqual("#112233", env.Session.Video.Background.Color);
    }

    [AvaloniaFact]
    public void VideoSettings_InvalidColor_BlocksOkWithAMessage()
    {
        using var env = new AuditEnv();
        var w = new VideoSettingsWindow(env.Session.Video);
        w.Show();
        w.BackgroundColorBox.Text = "red";
        Invoke(w, "OkButton_Click", w, new RoutedEventArgs());
        AuditEnv.WaitFor(() => env.Messages.Count > 0, "color message", 3000);
        Assert.Contains("#RRGGBB", env.Messages[0]);
        Assert.Equal(env.Session.Video, w.Result);
        w.Close();
    }

    [AvaloniaFact]
    public void VideoSettings_Cancel_ChangesNothing()
    {
        using var env = new AuditEnv();
        env.ModalScript = d => { ((VideoSettingsWindow)d).BackgroundColorBox.Text = "#010203"; return Task.FromResult<bool?>(false); };
        env.Click("VideoSettingsMenuItem_Click");
        env.Settle(200);
        Assert.NotEqual("#010203", env.Session.Video.Background.Color);
        Assert.False(env.Session.IsDirty);
    }

    [AvaloniaFact]
    public void ShowLayerLabels_Toggles_WithoutError()
    {
        using var env = WithLayers();
        env.Window.ShowLayerLabelsMenuItem.IsChecked = true;
        env.Click("ShowLayerLabelsMenuItem_Click");
        env.Settle(300);
        Assert.True(env.Window.PreviewEngine.ShowLayerLabels);
        env.Window.ShowLayerLabelsMenuItem.IsChecked = false;
        env.Click("ShowLayerLabelsMenuItem_Click");
        env.Settle(300);
        Assert.False(env.Window.PreviewEngine.ShowLayerLabels);
    }

    // ----- song range, transport, keyboard -----

    [AvaloniaFact]
    public void SongInOut_SetFromThePlayhead_KeepInBeforeOut_AndClear()
    {
        using var env = WithLayers();
        env.Click("SeekForwardButton_Click");
        env.Settle(1500);
        double position = env.Window.PreviewEngine.PositionMs;

        env.Click("SetSongInMenuItem_Click");
        Assert.Equal(position, env.Session.SongInMs);
        Assert.StartsWith("Song in set", env.Status);

        env.Click("RestartButton_Click");
        env.Settle(800);
        env.Click("SetSongOutMenuItem_Click");   // playhead at 0, before In: Out is kept after In
        Assert.True(env.Session.SongOutMs > env.Session.SongInMs);

        var root = env.SaveAndRead().Root;
        Assert.Equal(env.Session.SongInMs!.Value, root.GetProperty("SongInMs").GetDouble(), 1);
        Assert.Equal(env.Session.SongOutMs!.Value, root.GetProperty("SongOutMs").GetDouble(), 1);

        env.Click("ClearSongRangeMenuItem_Click");
        Assert.Null(env.Session.SongInMs);
        Assert.Null(env.Session.SongOutMs);
        Assert.Equal("Song in/out cleared.", env.Status);
        env.Click("UndoMenuItem_Click");
        Assert.NotNull(env.Session.SongInMs);
    }

    [AvaloniaFact]
    public void ClearSongRange_WithNoRange_DoesNothing()
    {
        using var env = new AuditEnv();
        env.Click("ClearSongRangeMenuItem_Click");
        Assert.False(env.Session.IsDirty);
    }

    [AvaloniaFact]
    public void IAndOKeys_SetTheSongRange()
    {
        using var env = WithLayers();
        Assert.True(Key_(env, Key.I));
        Assert.NotNull(env.Session.SongInMs);
        env.Click("SeekForwardButton_Click");
        env.Settle(1500);
        Assert.True(Key_(env, Key.O));
        Assert.NotNull(env.Session.SongOutMs);
    }

    [AvaloniaFact]
    public void Seek_ForwardAndBack_MovesThePlayheadFiveSeconds_AndRestartGoesToZero()
    {
        using var env = WithLayers();
        env.Click("SeekForwardButton_Click");
        env.Settle(1500);
        double forward = env.Window.PreviewEngine.PositionMs;
        Assert.InRange(forward, 1, 5000.5);       // clamped to the 4 s song
        env.Click("SeekBackButton_Click");
        env.Settle(1000);
        Assert.True(env.Window.PreviewEngine.PositionMs < forward);
        env.Click("SeekForwardButton_Click");
        env.Settle(1000);
        env.Click("RestartButton_Click");
        env.Settle(1000);
        Assert.Equal(0, env.Window.PreviewEngine.PositionMs, 1);
    }

    [AvaloniaFact]
    public void Play_WithNoLayers_AsksForOne()
    {
        using var env = new AuditEnv();
        env.Click("PlayButton_Click");
        env.Settle(200);
        Assert.Equal("Add at least one layer first.", env.Status);
        Assert.False(env.Window.PreviewEngine.IsPlaying);
    }

    [AvaloniaFact]
    public void Play_ThenStop_StartsAndStopsThePreview_AndStopRewinds()
    {
        using var env = WithLayers();
        env.Click("PlayButton_Click");
        AuditEnv.WaitFor(() => env.Window.PreviewEngine.IsPlaying || env.Status.StartsWith("Preview failed") || env.Status.Contains("fail"), "play", 15000);
        Assert.True(env.Window.PreviewEngine.IsPlaying, env.Status);
        env.Settle(700);
        Assert.True(env.Window.PreviewEngine.PositionMs > 0);

        env.Click("PlayButton_Click");
        AuditEnv.WaitFor(() => !env.Window.PreviewEngine.IsPlaying, "stop", 15000);
        env.Settle(500);
        Assert.Equal("Preview stopped.", env.Status);
        Assert.Equal(0, env.Window.PreviewEngine.PositionMs, 1);
    }

    [AvaloniaFact]
    public void Play_DuringAnExport_IsRefused()
    {
        using var env = WithLayers();
        env.Window.ExportMenuItem.IsEnabled = false;
        env.Click("PlayButton_Click");
        env.Settle(200);
        Assert.Equal("Finish the export first.", env.Status);
    }

    [AvaloniaFact]
    public void SpaceKey_TogglesPlayback_AndHomeRestarts()
    {
        using var env = WithLayers();
        Assert.True(Key_(env, Key.Space));
        AuditEnv.WaitFor(() => env.Window.PreviewEngine.IsPlaying, "play via Space", 15000);
        Assert.True(Key_(env, Key.Space));
        AuditEnv.WaitFor(() => !env.Window.PreviewEngine.IsPlaying, "stop via Space", 15000);
        Key_(env, Key.Right);
        Key_(env, Key.Home);
        Assert.Equal(0, env.Window.PreviewEngine.PositionMs, 1);
    }

    [AvaloniaFact]
    public void CommandS_Saves_AndCommandZ_Undoes()
    {
        using var env = WithLayers();
        string path = env.Path_("keys.a-capella.json");
        env.SavePathAnswers.Enqueue(path);
        Assert.True(Key_(env, Key.S, KeyModifiers.Meta));
        AuditEnv.WaitFor(() => File.Exists(path), "save via shortcut", 10000);
        env.Settle(300);
        Assert.False(env.Session.IsDirty);

        for (int i = 0; i < 4 && env.Layers.Count > 0; i++) Assert.True(Key_(env, Key.Z, KeyModifiers.Meta));
        Assert.Empty(env.Layers);
    }

    // ----- edit menu and window -----

    [AvaloniaFact]
    public void UndoRedo_WalkAnImportBackAndForth()
    {
        using var env = WithLayers();
        UndoImport(env);
        Assert.Empty(env.Layers);
        for (int i = 0; i < 4 && env.Layers.Count < 2; i++) env.Click("RedoMenuItem_Click");
        Assert.Equal(2, env.Layers.Count);
        Assert.NotNull(env.Rows[0].Layer);
        UndoImport(env);
        env.Click("UndoMenuItem_Click");   // nothing left to undo: must be harmless
        env.Click("UndoMenuItem_Click");
        Assert.Empty(env.Layers);
    }

    [AvaloniaFact]
    public void Close_WithUnsavedChanges_CancelKeepsTheWindowOpen()
    {
        using var env = WithLayers();
        env.MessageAnswer = MessageResult.Cancel;
        env.Window.Close();
        env.Settle(500);
        Assert.True(env.Window.IsVisible);
        Assert.Equal(2, env.Layers.Count);
        Assert.NotEmpty(env.Messages);
    }
}
