using A_Capella.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace A_Capella.App.Tests;

/// <summary>Renders the interface through its real handlers into numbered PNG sequences, one
/// folder per scene, for demos and documentation. Only runs when ACAPELLA_CAPTURE_DIR is set;
/// ACAPELLA_CAPTURE_VIDEOS may name a folder holding piano.mp4, soprano.mp4, alto.mp4, tenor.mp4 and
/// bass.mp4 (default: a "src" folder inside the capture folder).</summary>
public class CaptureTests
{
    private const double Scale = 2;
    private static readonly string[] Parts = { "piano", "soprano", "alto", "tenor", "bass" };

    private sealed class Recorder
    {
        private readonly string _root;
        private readonly Window _window;
        public readonly Dictionary<string, int> Counts = new();
        public Recorder(string root, Window window) { _root = root; _window = window; }

        public void Frame(string scene, int repeat = 1)
        {
            string dir = Path.Combine(_root, scene);
            Directory.CreateDirectory(dir);
            Counts.TryGetValue(scene, out int n);
            Dispatcher.UIThread.RunJobs();
            _window.UpdateLayout();
            string first = Path.Combine(dir, $"{++n:D4}.png");
            _window.CaptureRenderedFrame()!.Save(first);
            for (int i = 1; i < repeat; i++) File.Copy(first, Path.Combine(dir, $"{++n:D4}.png"), true);
            Counts[scene] = n;
        }
    }

    private static IEnumerable<PresetPicker> PresetPickers(Control root) => root.GetVisualDescendants().OfType<PresetPicker>();

    private static void ShowMenu(Recorder rec, AuditEnv env, PresetPicker picker, string scene)
    {
        var menu = picker.CreateMenu();
        menu.Open(picker);
        env.Settle(400);
        rec.Frame(scene);
        menu.Close();
        env.Settle(150);
    }

    [AvaloniaFact]
    public void CaptureInterfaceSequences()
    {
        string? root = Environment.GetEnvironmentVariable("ACAPELLA_CAPTURE_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(root), "Set ACAPELLA_CAPTURE_DIR to write the interface sequences.");
        string videos = Environment.GetEnvironmentVariable("ACAPELLA_CAPTURE_VIDEOS") ?? Path.Combine(root!, "src");
        Directory.CreateDirectory(root!);
        const int Fps = 30;

        using var env = new AuditEnv();
        var window = env.Window;
        window.Width = 1440;
        window.Height = 900;
        window.SetRenderScaling(Scale);
        env.Settle(500);
        var rec = new Recorder(root!, window);

        // Empty window.
        rec.Frame("01_empty");

        int alignedRow = -1; double alignedMs = 0;
        // Layers appear one at a time; each state holds for half a second.
        for (int i = 0; i < Parts.Length; i++)
        {
            env.Import(Path.Combine(videos, Parts[i] + ".mp4"));
            AuditEnv.WaitFor(() => !env.Status.Contains("Aligning") && !env.Status.Contains("Analys"), "auto-align", 60000);
            env.Settle(1500);
            // Keep every face in view: remember what auto-align found and show it in its own scene.
            double found = env.Rows[i].ManualOffsetMs;
            if (found != 0) { alignedRow = i; alignedMs = found; env.Window.Call_NudgeReset(env.Rows[i]); env.Settle(1000); }
            rec.Frame("02_grid_growing", Fps / 2);
        }
        env.Settle(1500);
        rec.Frame("03_five_layers");
        File.WriteAllText(Path.Combine(root!, "layers.txt"), string.Join("\n", env.Layers.Select(l =>
            $"{l.LayerId} {Path.GetFileName(l.SourcePath)} cell={l.CellIndex} calibrated={l.CalibratedOffsetMs} manual={l.ManualOffsetMs} trim={l.TrimStartMs}")) + "\n");

        // Layout picker, through the real menu handler.
        env.ModalScript = dialog =>
        {
            Dispatcher.UIThread.RunJobs();
            if (dialog is Window w) { w.Show(); w.SetRenderScaling(Scale); w.UpdateLayout(); Dispatcher.UIThread.RunJobs(); w.CaptureRenderedFrame()!.Save(NextPath(root!, "04_layout_picker")); }
            return Task.FromResult<bool?>(false);
        };
        env.Click("ChooseLayoutMenuItem_Click");
        env.Settle(500);
        env.ModalScript = null;

        // Effects strips with the preset menus open, for the first singer layer.
        env.SelectRow(1);
        env.Settle(800);
        rec.Frame("05_effects_strip");
        int slot = 0;
        foreach (var picker in PresetPickers(window.FxPanel).ToList())
            ShowMenu(rec, env, picker, $"06_fx_menu_{++slot}");
        ShowMenu(rec, env, window.MasterLimiterCombo, "07_master_limiter_menu");

        // The timing nudge showing the offset auto-align found for the late track.
        if (alignedRow >= 0)
        {
            env.SelectRow(alignedRow);
            env.Rows[alignedRow].Nudge(alignedMs);
            env.Settle(1200);
            rec.Frame("08_auto_align");
            File.WriteAllText(Path.Combine(root!, "08_auto_align", "status.txt"), $"layer {alignedRow + 1} offset {alignedMs} ms\n");
            env.Window.Call_NudgeReset(env.Rows[alignedRow]);
            env.Settle(1000);
        }

        // Playhead moving: three seconds at 30 fps.
        env.SelectRow(0);
        double min = window.TimelineSlider.Minimum, max = window.TimelineSlider.Maximum;
        for (int f = 0; f < 3 * Fps; f++)
        {
            window.TimelineSlider.Value = min + (max - min) * 0.4 * f / (3 * Fps);
            env.Settle(120);
            rec.Frame("09_playhead");
        }

        // The real export, captured whenever its progress moves.
        string output = Path.Combine(root!, "export_grid.mp4");
        env.SavePathAnswers.Enqueue(output);
        env.Click("ExportButton_Click");
        AuditEnv.WaitFor(() => !window.ExportMenuItem.IsEnabled, "export to start", 15000);
        double last = -1;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!(window.ExportMenuItem.IsEnabled && !window.ExportProgressBar.IsVisible))
        {
            Dispatcher.UIThread.RunJobs();
            if (window.ExportProgressBar.IsVisible && window.ExportProgressBar.Value - last >= 0.03)
            {
                last = window.ExportProgressBar.Value;
                rec.Frame("10_export_progress");
            }
            Thread.Sleep(5);
            if (sw.Elapsed.TotalSeconds > 600) throw new TimeoutException("export");
        }
        env.Settle(500);
        rec.Frame("11_export_complete");

        File.WriteAllText(Path.Combine(root!, "frame-counts.txt"),
            string.Join("\n", rec.Counts.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")) + "\n");
    }

    private static string NextPath(string root, string scene)
    {
        string dir = Path.Combine(root, scene);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{Directory.GetFiles(dir, "*.png").Length + 1:D4}.png");
    }
}
