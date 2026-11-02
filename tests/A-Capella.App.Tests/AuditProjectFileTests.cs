using Avalonia.Headless.XUnit;
using A_Capella.Engine.Project;
using Xunit;

namespace A_Capella.App.Tests;

/// <summary>File menu: New, Open, Save, Save As, Export, Import, About, Recordings folder size.</summary>
public class AuditProjectFileTests
{
    [AvaloniaFact]
    public void Import_FillsFreeSlotsInNameOrder_AndIsOneUndoStep()
    {
        using var env = new AuditEnv();
        string b = TestMedia.Tone(env.Dir, "b-take");
        string a = TestMedia.Tone(env.Dir, "a-take");
        string v = TestMedia.Video(env.Dir, "c-take");

        env.Import(b, v, a);

        Assert.Equal(3, env.Layers.Count);
        Assert.Equal(new[] { a, b, v }, env.Layers.OrderBy(l => l.CellIndex).Select(l => l.SourcePath));
        Assert.Equal(new[] { LayerKind.UploadedAudioOnly, LayerKind.UploadedAudioOnly, LayerKind.UploadedVideo },
            env.Layers.OrderBy(l => l.CellIndex).Select(l => l.Kind));
        Assert.Contains("3 of 9", env.Window.LaneHeaderCountText.Text);

        env.Click("UndoMenuItem_Click");
        Assert.Empty(env.Layers);
    }

    [AvaloniaFact]
    public void Import_PastTheLayerCap_ImportsUpToTheCapAndReportsTheRest()
    {
        using var env = new AuditEnv();
        var files = Enumerable.Range(0, LayerCollection.MaxLayers + 2).Select(i => TestMedia.Tone(env.Dir, $"t{i}", 200 + i * 50, 1)).ToArray();

        env.PickFilesAnswers.Enqueue(files);
        env.Click("ImportFilesMenuItem_Click");
        // The import status is replaced by auto-align progress once the loudness match runs, so read it first.
        AuditEnv.WaitFor(() => env.Layers.Count > 0, "import");
        Assert.Contains("2 files skipped", env.Status);
        env.Settle();

        Assert.Equal(LayerCollection.MaxLayers, env.Layers.Count);
        // A full project refuses a further import without opening the picker.
        env.PickFilesAnswers.Enqueue(new[] { files[0] });
        env.Click("ImportFilesMenuItem_Click");
        Assert.Contains("No empty layer slots", env.Status);
        Assert.Single(env.PickFilesAnswers);
    }

    [AvaloniaFact]
    public void Import_Cancelled_ChangesNothing()
    {
        using var env = new AuditEnv();
        env.Click("ImportFilesMenuItem_Click");
        env.Settle(100);
        Assert.Empty(env.Layers);
        Assert.False(env.Session.IsDirty);
    }

    [AvaloniaFact]
    public void Save_Untitled_AsksForAPath_AddsTheSuffix_AndWritesTheProject()
    {
        using var env = new AuditEnv();
        env.Import(TestMedia.Tone(env.Dir, "one"), TestMedia.Video(env.Dir, "two"));
        env.Window.MetronomeBpmTextBox.Text = "97.5";
        env.Call("MetronomeBpmTextBox_LostFocus", null, new Avalonia.Interactivity.RoutedEventArgs());
        Assert.True(env.Session.IsDirty);

        string target = env.Path_("My Song");
        env.SavePathAnswers.Enqueue(target);
        env.Click("SaveMenuItem_Click");
        env.Settle(200);

        string expected = target + ".a-capella.json";
        Assert.True(File.Exists(expected), env.Status);
        Assert.Equal(expected, env.Session.CurrentFilePath);
        Assert.False(env.Session.IsDirty);
        Assert.Equal("A-Capella — My Song", env.Window.Title);
        var root = System.Text.Json.JsonDocument.Parse(File.ReadAllText(expected)).RootElement;
        Assert.Equal(2, root.GetProperty("Layers").GetArrayLength());
        Assert.Equal(97.5, root.GetProperty("MetronomeBpm").GetDouble());
    }

    [AvaloniaFact]
    public void Save_AfterTheFirstSave_WritesInPlace_WithoutAskingAgain()
    {
        using var env = new AuditEnv();
        env.Import(TestMedia.Tone(env.Dir, "one"));
        env.SavePathAnswers.Enqueue(env.Path_("x"));
        env.Click("SaveMenuItem_Click");
        env.Settle(200);
        string path = env.Session.CurrentFilePath!;

        env.Import(TestMedia.Tone(env.Dir, "two"));
        Assert.True(env.Session.IsDirty);
        env.SavePathAnswers.Enqueue(env.Path_("should-not-be-used"));
        env.Click("SaveMenuItem_Click");
        env.Settle(200);

        Assert.Single(env.SavePathAnswers);
        Assert.False(env.Session.IsDirty);
        Assert.Equal(2, System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("Layers").GetArrayLength());
    }

    [AvaloniaFact]
    public void SaveAs_AlwaysAsks_AndSwitchesTheCurrentFile()
    {
        using var env = new AuditEnv();
        env.Import(TestMedia.Tone(env.Dir, "one"));
        env.SavePathAnswers.Enqueue(env.Path_("first.a-capella.json"));
        env.Click("SaveMenuItem_Click");
        env.Settle(200);

        env.SavePathAnswers.Enqueue(env.Path_("second.a-capella.json"));
        env.Click("SaveProjectButton_Click");
        env.Settle(200);

        Assert.Equal(env.Path_("second.a-capella.json"), env.Session.CurrentFilePath);
        Assert.True(File.Exists(env.Path_("second.a-capella.json")));
        Assert.True(File.Exists(env.Path_("first.a-capella.json")));
    }

    [AvaloniaFact]
    public void Save_CancelledDialog_LeavesTheProjectUnsaved()
    {
        using var env = new AuditEnv();
        env.Import(TestMedia.Tone(env.Dir, "one"));
        env.SavePathAnswers.Enqueue(null);
        env.Click("SaveMenuItem_Click");
        env.Settle(200);
        Assert.True(env.Session.IsDirty);
        Assert.Null(env.Session.CurrentFilePath);
    }

    [AvaloniaFact]
    public void Open_RestoresLayersInTheirCells_BpmMasterVolumeAndPresets()
    {
        string saved;
        using (var first = new AuditEnv())
        {
            first.Import(TestMedia.Tone(first.Dir, "keep-a"), TestMedia.Tone(first.Dir, "keep-b", 330));
            first.Session.Layers.Layers[1].CellIndex = 2;   // a gap in the cells, as a project with a deleted layer has
            first.Session.MetronomeBpm = 88;
            first.Session.MasterVolumeDb = -4;
            first.Session.Layers.Layers[0].Name = "Lead";
            first.Session.Layers.Layers[0].MixParameters.Pan = -0.5f;
            saved = Path.Combine(Path.GetTempPath(), "acapella-open-" + Guid.NewGuid().ToString("N")[..6] + ".a-capella.json");
            first.Session.Save(saved);
        }

        try
        {
            using var env = new AuditEnv();
            env.PickFilesAnswers.Enqueue(new[] { saved });
            env.Click("OpenProjectButton_Click");
            env.Settle(300);

            Assert.Equal(2, env.Layers.Count);
            Assert.Equal("Lead", env.Rows[0].Name);
            Assert.NotNull(env.Rows[2].Layer);
            Assert.Null(env.Rows[1].Layer);
            Assert.Equal(88, env.Session.MetronomeBpm);
            Assert.Equal(-4f, env.Session.MasterVolumeDb);
            Assert.Equal(-4, env.Window.MasterVolumeSlider.Value);
            Assert.Equal(-0.5f, env.Rows[0].Layer!.MixParameters.Pan);
            Assert.Equal(saved, env.Session.CurrentFilePath);
            Assert.False(env.Session.IsDirty);
            Assert.StartsWith("Project opened", env.Status);
        }
        finally
        {
            File.Delete(saved);
        }
    }

    [AvaloniaFact]
    public void Open_NotAProjectFile_ReportsTheFailure_AndKeepsTheCurrentProject()
    {
        using var env = new AuditEnv();
        env.Import(TestMedia.Tone(env.Dir, "one"));
        env.Session.Save(env.Path_("cur.a-capella.json"));
        string junk = env.Path_("junk.json");
        File.WriteAllText(junk, "{\"hello\": 1}");
        env.PickFilesAnswers.Enqueue(new[] { junk });
        env.Click("OpenProjectButton_Click");
        env.Settle(300);

        Assert.StartsWith("Open failed", env.Status);
        Assert.Single(env.Layers);
    }

    [AvaloniaFact]
    public void New_DiscardsLayers_ResetsTitle_AndAsksFirstWhenUnsaved()
    {
        using var env = new AuditEnv();
        env.Import(TestMedia.Tone(env.Dir, "one"));
        env.Window.MasterVolumeSlider.Value = -6;

        // Cancel keeps everything.
        env.MessageAnswer = MessageResult.Cancel;
        env.Click("NewProjectMenuItem_Click");
        env.Settle(200);
        Assert.Single(env.Layers);
        Assert.Contains("Save changes to Untitled?", env.Messages.Last());

        // No discards the unsaved project.
        env.MessageAnswer = MessageResult.No;
        env.Click("NewProjectMenuItem_Click");
        env.Settle(300);
        Assert.Empty(env.Layers);
        Assert.All(env.Rows, r => Assert.Null(r.Layer));
        Assert.Equal(0, env.Session.MasterVolumeDb);
        Assert.Equal(0, env.Window.MasterVolumeSlider.Value);
        Assert.Equal("New project.", env.Status);
        Assert.Null(env.Session.CurrentFilePath);
        Assert.Contains("Untitled", env.Window.Title);
    }

    [AvaloniaFact]
    public void New_YesSavesFirst()
    {
        using var env = new AuditEnv();
        env.Import(TestMedia.Tone(env.Dir, "one"));
        env.MessageAnswer = MessageResult.Yes;
        env.SavePathAnswers.Enqueue(env.Path_("keepme"));
        env.Click("NewProjectMenuItem_Click");
        env.Settle(400);
        Assert.True(File.Exists(env.Path_("keepme.a-capella.json")));
        Assert.Empty(env.Layers);
    }

    [AvaloniaFact]
    public void Export_WritesAValidMp4WithVideoAndAudio_AndLeavesTheProjectEditable()
    {
        using var env = new AuditEnv();
        env.Import(TestMedia.Video(env.Dir, "v1", 3), TestMedia.Tone(env.Dir, "t2", 330, 3, 1.0), TestMedia.Video(env.Dir, "v3", 3, 550));
        string output = env.Path_("out.mp4");
        env.SavePathAnswers.Enqueue(output);

        env.Click("ExportButton_Click");
        env.WaitForExport(output);

        Assert.True(env.Window.ExportMenuItem.IsEnabled);
        Assert.False(env.Window.ExportProgressBar.IsVisible);
        string video = TestMedia.Probe(output, "stream=codec_name,width,height,codec_type");
        Assert.Contains("codec_name=h264", video);
        Assert.Contains("codec_type=audio", video);
        Assert.Contains("width=1280", video);
        double duration = double.Parse(TestMedia.Probe(output, "format=duration").Split('=')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(duration, 2.7, 3.6);
        Assert.True(TestMedia.PeakDb(output) < 0, "limiter keeps the export under full scale");
    }

    [AvaloniaFact]
    public void Export_EndsWithTheCompletionMessageInTheStatusBar()
    {
        using var env = new AuditEnv();
        env.Import(TestMedia.Video(env.Dir, "v1", 3));
        env.SavePathAnswers.Enqueue(env.Path_("done.mp4"));
        env.Click("ExportButton_Click");
        env.WaitForExport(env.Path_("done.mp4"));
        Assert.Equal("Export complete: done.mp4", env.Status);
    }

    [AvaloniaFact]
    public void Export_WithNoLayers_RefusesAndAsksForNothing()
    {
        using var env = new AuditEnv();
        env.SavePathAnswers.Enqueue(env.Path_("never.mp4"));
        env.Click("ExportButton_Click");
        Assert.Equal("Add at least one layer before exporting.", env.Status);
        Assert.Single(env.SavePathAnswers);
    }

    [AvaloniaFact]
    public void Export_HonorsSongInAndOut()
    {
        using var env = new AuditEnv();
        env.Import(TestMedia.Video(env.Dir, "v1", 4));
        env.Session.SongInMs = 1000;
        env.Session.SongOutMs = 3000;
        string output = env.Path_("range.mp4");
        env.SavePathAnswers.Enqueue(output);
        env.Click("ExportButton_Click");
        env.WaitForExport(output);
        double duration = double.Parse(TestMedia.Probe(output, "format=duration").Split('=')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(duration, 1.8, 2.3);
    }

    [AvaloniaFact]
    public void About_ShowsTheAppName()
    {
        using var env = new AuditEnv();
        env.Click("AboutMenuItem_Click");
        env.Settle(100);
        Assert.Contains("A-Capella", env.Messages.Last());
    }

    [AvaloniaFact]
    public void RecordingsFolderSize_ReportsFileCountAndSize()
    {
        using var env = new AuditEnv();
        env.Click("RecordingsFolderSizeMenuItem_Click");
        env.Settle(100);
        Assert.Equal("No recordings folder yet.", env.Messages.Last());

        Directory.CreateDirectory(env.Window.MediaDir);
        File.WriteAllBytes(Path.Combine(env.Window.MediaDir, "take1.mp4"), new byte[2 * 1024 * 1024]);
        File.WriteAllBytes(Path.Combine(env.Window.MediaDir, "take2.mp4"), new byte[1024 * 1024]);
        env.Click("RecordingsFolderSizeMenuItem_Click");
        env.Settle(100);
        Assert.Contains("2 file(s), 3.0 MB", env.Messages.Last());
    }
}
