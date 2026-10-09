using Acapella.Engine.Composite;
using Acapella.Engine.Host;
using Acapella.Engine.Metronome;
using Acapella.Engine.Mix;
using Acapella.Engine.Persistence;
using Acapella.Engine.Project;

namespace Acapella.Engine.Tests.Project;

public class VideoSettingsTests : IDisposable
{
    private readonly MixEngine _mixEngine = new(NoHostedPluginsAvailable.Instance);
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var f in _temp) if (File.Exists(f)) File.Delete(f);
    }

    private string TempPath()
    {
        string p = Path.Combine(Path.GetTempPath(), $"video-{Guid.NewGuid()}{ProjectSession.ProjectFileSuffix}");
        _temp.Add(p);
        return p;
    }

    [Theory]
    [InlineData(AspectRatioOption.Wide16x9)]
    [InlineData(AspectRatioOption.Square1x1)]
    [InlineData(AspectRatioOption.Standard4x3)]
    [InlineData(AspectRatioOption.Tall9x16)]
    public void PreviewAndExportSizes_ShareTheRatio_AndAreEven(AspectRatioOption ratio)
    {
        var settings = new VideoSettings { AspectRatio = ratio };
        var (ew, eh) = settings.ExportSize;
        var (pw, ph) = settings.PreviewSize;

        Assert.Equal(0, ew % 2);
        Assert.Equal(0, eh % 2);
        Assert.Equal((double)ew / eh, (double)pw / ph, 6);
    }

    [Fact]
    public void Ratios_AreTheAdvertisedShapes()
    {
        Assert.Equal((1280, 720), new VideoSettings { AspectRatio = AspectRatioOption.Wide16x9 }.ExportSize);
        Assert.Equal((1080, 1080), new VideoSettings { AspectRatio = AspectRatioOption.Square1x1 }.ExportSize);
        Assert.Equal((1440, 1080), new VideoSettings { AspectRatio = AspectRatioOption.Standard4x3 }.ExportSize);
        Assert.Equal((720, 1280), new VideoSettings { AspectRatio = AspectRatioOption.Tall9x16 }.ExportSize);
    }

    [Fact]
    public void NewProject_DefaultsToFillWithASmallGap()
    {
        var video = new ProjectSession(_mixEngine).Video;
        Assert.Equal(AspectRatioOption.Wide16x9, video.AspectRatio);
        Assert.Equal(CellFit.Fill, video.Fit);
        Assert.Equal(0.01, video.GutterFraction);
    }

    [Fact]
    public void Settings_RoundTripThroughSaveAndOpen()
    {
        var session = new ProjectSession(_mixEngine);
        session.Video = session.Video with { AspectRatio = AspectRatioOption.Tall9x16, Fit = CellFit.Fit, GutterFraction = 0.03 };
        string path = TempPath();
        session.Save(path);

        var reopened = new ProjectSession(_mixEngine);
        reopened.Open(path);
        Assert.Equal(AspectRatioOption.Tall9x16, reopened.Video.AspectRatio);
        Assert.Equal(CellFit.Fit, reopened.Video.Fit);
        Assert.Equal(0.03, reopened.Video.GutterFraction);
    }

    [Fact]
    public void FileWithoutVideoSection_OpensAsLegacy_FitAndNoGap()
    {
        string path = TempPath();
        File.WriteAllText(path, "{\"LayoutId\":\"2x2\",\"MetronomeBpm\":120,\"Layers\":[]}");
        var session = new ProjectSession(_mixEngine);
        session.Open(path);

        Assert.Equal(AspectRatioOption.Wide16x9, session.Video.AspectRatio);
        Assert.Equal(CellFit.Fit, session.Video.Fit);
        Assert.Equal(0, session.Video.GutterFraction);
    }

    [Fact]
    public void BadValuesInTheFile_FallBackToDefaults_AndGutterIsClamped()
    {
        string path = TempPath();
        File.WriteAllText(path, "{\"LayoutId\":\"2x2\",\"Video\":{\"AspectRatio\":\"Banana\",\"FillMode\":\"??\",\"GutterFraction\":9},\"Layers\":[]}");
        var session = new ProjectSession(_mixEngine);
        session.Open(path);

        Assert.Equal(AspectRatioOption.Wide16x9, session.Video.AspectRatio);
        Assert.Equal(CellFit.Fill, session.Video.Fit);
        Assert.Equal(VideoSettings.MaxGutterFraction, session.Video.GutterFraction);
    }

    [Fact]
    public void VideoSettingsEdit_IsUndoable()
    {
        var session = new ProjectSession(_mixEngine);
        session.Video = session.Video with { AspectRatio = AspectRatioOption.Square1x1 };
        session.CommitEdit();
        Assert.True(session.Undo());
        Assert.Equal(AspectRatioOption.Wide16x9, session.Video.AspectRatio);
    }

    [Fact]
    public void BackgroundAndWatermark_RoundTripThroughSaveAndOpen()
    {
        var session = new ProjectSession(_mixEngine);
        session.Video = session.Video with
        {
            Background = new BackgroundSettings { Color = "#123456", ImagePath = "C:/pics/bg.png", ImageMode = BackgroundImageMode.Tile },
            Watermark = new WatermarkSettings
            {
                Enabled = true, Text = "My Choir", FontFamily = "Georgia", SizeFraction = 0.1, FillColor = "#FFEE00",
                StrokeColor = "#000011", StrokeFraction = 0.12, Shadow = false, Opacity = 0.5, Corner = WatermarkCorner.TopLeft,
            },
        };
        string path = TempPath();
        session.Save(path);

        var reopened = new ProjectSession(_mixEngine);
        reopened.Open(path);
        Assert.Equal(session.Video, reopened.Video);
    }

    [Fact]
    public void WatermarkValuesInTheFile_AreClamped()
    {
        string path = TempPath();
        File.WriteAllText(path, "{\"LayoutId\":\"2x2\",\"Video\":{\"Watermark\":{\"Enabled\":true,\"Text\":\"x\",\"SizeFraction\":50,\"Opacity\":-3,\"StrokeFraction\":9,\"Corner\":\"Nowhere\"}},\"Layers\":[]}");
        var session = new ProjectSession(_mixEngine);
        session.Open(path);

        var w = session.Video.Watermark;
        Assert.Equal(WatermarkSettings.MaxSizeFraction, w.SizeFraction);
        Assert.Equal(0, w.Opacity);
        Assert.Equal(0.3, w.StrokeFraction);
        Assert.Equal(WatermarkCorner.BottomRight, w.Corner);
    }

    [Fact]
    public void LegacyFile_HasNoWatermarkAndABlackBackground()
    {
        string path = TempPath();
        File.WriteAllText(path, "{\"LayoutId\":\"2x2\",\"Layers\":[]}");
        var session = new ProjectSession(_mixEngine);
        session.Open(path);
        Assert.False(session.Video.Watermark.IsVisible);
        Assert.Equal("#000000", session.Video.Background.Color);
        Assert.Null(session.Video.Background.ImagePath);
    }

    // ----- Metronome meter (stored with the project like the BPM) -----

    [Fact]
    public void TimeSignature_DefaultsTo4_4()
    {
        Assert.Equal(TimeSignature.FourFour, new ProjectSession(_mixEngine).MetronomeTimeSignature);
    }

    [Fact]
    public void TimeSignature_RoundTripsThroughSaveAndOpen_AndIsUndoable()
    {
        var session = new ProjectSession(_mixEngine);
        session.MetronomeTimeSignature = TimeSignature.SixEight;
        session.CommitEdit();
        string path = TempPath();
        session.Save(path);

        var reopened = new ProjectSession(_mixEngine);
        reopened.Open(path);
        Assert.Equal(TimeSignature.SixEight, reopened.MetronomeTimeSignature);

        Assert.True(session.Undo());
        Assert.Equal(TimeSignature.FourFour, session.MetronomeTimeSignature);
    }

    [Fact]
    public void FileWithoutAMeter_OpensAs4_4_AndNonsenseIsSanitized()
    {
        string old = TempPath();
        File.WriteAllText(old, "{\"LayoutId\":\"2x2\",\"Layers\":[]}");
        var session = new ProjectSession(_mixEngine);
        session.Open(old);
        Assert.Equal(TimeSignature.FourFour, session.MetronomeTimeSignature);

        string weird = TempPath();
        File.WriteAllText(weird, "{\"LayoutId\":\"2x2\",\"MetronomeBeatsPerBar\":0,\"MetronomeBeatUnit\":7,\"Layers\":[]}");
        session.Open(weird);
        Assert.Equal(new TimeSignature(1, 4), session.MetronomeTimeSignature);
    }

    [Fact]
    public void ChangingTheMeter_MarksTheProjectDirty_ButReassigningTheSameOneDoesNot()
    {
        var session = new ProjectSession(_mixEngine);
        session.MetronomeTimeSignature = TimeSignature.FourFour;
        Assert.False(session.IsDirty);
        session.MetronomeTimeSignature = TimeSignature.ThreeFour;
        Assert.True(session.IsDirty);
    }
}
