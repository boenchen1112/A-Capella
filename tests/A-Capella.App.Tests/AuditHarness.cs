using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using A_Capella.App.ViewModels;
using A_Capella.Engine.Project;
using A_Capella.Engine.Presets;
using A_Capella.Engine.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace A_Capella.App.Tests;

/// <summary>Everything the feature-audit tests share: an isolated MainWindow (temp media, settings
/// and package folders, scripted dialogs), generated test media, and a way to wait for the async
/// handlers the headless host never pumps by itself.</summary>
internal sealed class AuditEnv : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "acapella-audit-" + Guid.NewGuid().ToString("N")[..8]);
    public MainWindow Window { get; }

    /// <summary>Messages the app showed (message boxes), newest last.</summary>
    public List<string> Messages { get; } = new();
    public MessageResult MessageAnswer { get; set; } = MessageResult.Yes;
    public Queue<IReadOnlyList<string>> PickFilesAnswers { get; } = new();
    public Queue<string?> SavePathAnswers { get; } = new();
    public Func<Window, Task<bool?>>? ModalScript { get; set; }

    public AuditEnv()
    {
        Directory.CreateDirectory(Dir);
        Window = new MainWindow
        {
            MediaDir = Path.Combine(Dir, "media"),
            SettingsInUse = new SettingsService(Path.Combine(Dir, "settings.json")),
            Packages = new FxPackageService(Path.Combine(Dir, "builtin-packages"), Path.Combine(Dir, "packages")),
        };
        Dialogs.MessageHandler = (_, text, _, _) => { Messages.Add(text); return Task.FromResult(MessageAnswer); };
        Dialogs.PickFilesHandler = (_, _, _) => Task.FromResult(PickFilesAnswers.Count > 0 ? PickFilesAnswers.Dequeue() : (IReadOnlyList<string>)Array.Empty<string>());
        Dialogs.PickSavePathHandler = (_, _) => Task.FromResult(SavePathAnswers.Count > 0 ? SavePathAnswers.Dequeue() : null);
        Dialogs.ModalHandler = (dialog, _) => ModalScript is { } script ? script(dialog) : Task.FromResult<bool?>(false);
        Window.Show();
    }

    /// <summary>Points recording at a stand-in for ffmpeg that lists one fake camera and microphone and
    /// captures ffmpeg's synthetic test video and a sine tone instead (no real devices here).</summary>
    public string InstallFakeCaptureDevices()
    {
        string shim = Path.Combine(Dir, "fake-ffmpeg.sh");
        File.WriteAllText(shim, """
            #!/bin/bash
            for a in "$@"; do
              if [ "$a" = "-list_devices" ]; then
                {
                  echo "[AVFoundation indev @ 0x1] AVFoundation video devices:"
                  echo "[AVFoundation indev @ 0x1] [0] Test Camera"
                  echo "[AVFoundation indev @ 0x1] [1] Capture screen 0"
                  echo "[AVFoundation indev @ 0x1] AVFoundation audio devices:"
                  echo "[AVFoundation indev @ 0x1] [0] Test Mic"
                } >&2
                exit 0
              fi
            done
            args=("$@")
            # Drop the avfoundation input (-f avfoundation -framerate 30 -i dev) and feed synthetic sources.
            out=()
            i=0
            n=${#args[@]}
            while [ $i -lt $n ]; do
              a="${args[$i]}"
              if [ "$a" = "-f" ] && [ "${args[$((i+1))]}" = "avfoundation" ]; then i=$((i+2)); continue; fi
              if [ "$a" = "-framerate" ]; then i=$((i+2)); continue; fi
              if [ "$a" = "-i" ]; then i=$((i+2)); continue; fi
              out+=("$a")
              i=$((i+1))
            done
            exec ffmpeg -re -f lavfi -i testsrc=size=320x180:rate=30 -re -f lavfi -i sine=frequency=440:sample_rate=44100 "${out[@]}"
            """);
        System.Diagnostics.Process.Start("chmod", new[] { "+x", shim })!.WaitForExit();
        Window.DeviceCatalogInUse = new A_Capella.Engine.Devices.DeviceCatalog(shim);
        Window.CaptureSessionFactory = () => new A_Capella.Engine.Capture.FfmpegCaptureSession(shim);
        return shim;
    }

    public string Status => Window.StatusText.Text ?? "";
    public ProjectSession Session => Window.Session;
    public IReadOnlyList<LayerRowViewModel> Rows => Window.TrackList.Items.Cast<LayerRowViewModel>().ToList();
    public IReadOnlyList<LayerModel> Layers => Session.Layers.Layers;

    public string Path_(string name) => Path.Combine(Dir, name);

    // ----- driving handlers -----

    private static MethodInfo Method(string name) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMethodException(name);

    /// <summary>Invokes a window event handler exactly as the control would, with a sender whose
    /// DataContext is the given row (the strip buttons find their layer that way).</summary>
    public object? Click(string handler, LayerRowViewModel? row = null)
    {
        var sender = new Button { DataContext = row };
        return Method(handler).Invoke(Window, new object?[] { sender, new RoutedEventArgs() });
    }

    /// <summary>Calls a private method with arbitrary arguments.</summary>
    public object? Call(string method, params object?[] args) => Method(method).Invoke(Window, args);

    /// <summary>Calls a private async method and waits for it to finish.</summary>
    public void CallAsync(string method, params object?[] args)
    {
        var task = (Task)Method(method).Invoke(Window, args)!;
        WaitFor(() => task.IsCompleted, $"{method} to finish");
        task.GetAwaiter().GetResult();
    }

    /// <summary>Pumps the UI dispatcher until the condition holds (async void handlers continue there).</summary>
    public static void WaitFor(Func<bool> condition, string what, int timeoutMs = 60000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Timed out waiting for " + what);
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    private bool ExportStarted(string output) => !Window.ExportMenuItem.IsEnabled || Status.StartsWith("Exporting") || File.Exists(output);

    private bool ExportFinished() => Window.ExportMenuItem.IsEnabled && !Window.ExportProgressBar.IsVisible;

    public void WaitForExport(string output, int timeoutMs = 30000)
    {
        try { WaitFor(() => ExportStarted(output), "export to start", 10000); WaitFor(() => ExportFinished(), "export", timeoutMs); Settle(300); }
        catch (TimeoutException) { throw new TimeoutException("Export did not finish; status was: " + Status + "; file exists=" + File.Exists(output) + " size=" + (File.Exists(output) ? new FileInfo(output).Length : -1) + "; jobs pending=" + Dispatcher.UIThread.CheckAccess()); }
    }

    /// <summary>Lets queued UI work and the fire-and-forget preview refreshes finish.</summary>
    public void Settle(int ms = 600)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    }

    // ----- common actions -----

    /// <summary>Imports files through File/Layers > Import files as layers, the real handler.</summary>
    public void Import(params string[] files)
    {
        PickFilesAnswers.Enqueue(files);
        Click("ImportFilesMenuItem_Click");
        Settle();
    }

    public void SelectRow(int index) => Call("SelectLane", Rows[index]);

    public ProjectSnapshotJson SaveAndRead(string name = "p.a-capella.json")
    {
        string path = Path_(name);
        Session.Save(path);
        return new ProjectSnapshotJson(JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone(), path);
    }

    public void Dispose()
    {
        try
        {
            // A saved project closes without the unsaved-changes prompt.
            Settle(900);
            if (Session.IsDirty) Session.Save(Path_("closing.a-capella.json"));
            Window.ExportMenuItem.IsEnabled = true;
            Window.Close();
        }
        catch { /* a failing test should report its own failure */ }
        Dialogs.MessageHandler = null;
        Dialogs.PickFilesHandler = null;
        Dialogs.PickSavePathHandler = null;
        Dialogs.ModalHandler = null;
        try { Directory.Delete(Dir, true); } catch { }
    }
}

internal sealed record ProjectSnapshotJson(JsonElement Root, string Path)
{
    public JsonElement Layers => Root.GetProperty("Layers");
}

/// <summary>Synthetic media from ffmpeg's lavfi sources; the audit never touches real recordings.</summary>
internal static class TestMedia
{
    public static void RunFfmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("ffmpeg failed: " + err);
    }

    public static string Tone(string dir, string name, double freq = 440, double seconds = 3, double volume = 0.5)
    {
        string path = Path.Combine(dir, name + ".wav");
        RunFfmpeg("-f", "lavfi", "-i", $"sine=frequency={freq.ToString(CultureInfo.InvariantCulture)}:sample_rate=44100:duration={seconds.ToString(CultureInfo.InvariantCulture)}",
            "-af", $"volume={volume.ToString(CultureInfo.InvariantCulture)}", path);
        return path;
    }

    /// <summary>A short click train: clicks at the given times (seconds), so two files differing only in
    /// start delay can be told apart by cross-correlation.</summary>
    public static string Clicks(string dir, string name, double delaySeconds, double seconds = 6)
    {
        string path = Path.Combine(dir, name + ".wav");
        // Irregular bursts of noise, then delayed.
        string expr = "aevalsrc='if(lt(mod(t,0.7),0.06),sin(2*PI*t*(300+800*mod(floor(t/0.7),3))),0)':s=44100:d=" + seconds.ToString(CultureInfo.InvariantCulture);
        if (delaySeconds > 0)
            RunFfmpeg("-f", "lavfi", "-i", expr, "-af", $"adelay={(int)(delaySeconds * 1000)}|{(int)(delaySeconds * 1000)}", path);
        else
            RunFfmpeg("-f", "lavfi", "-i", expr, path);
        return path;
    }

    public static string Video(string dir, string name, double seconds = 3, double freq = 440)
    {
        string path = Path.Combine(dir, name + ".mp4");
        RunFfmpeg("-f", "lavfi", "-i", $"testsrc=size=320x180:rate=30:duration={seconds.ToString(CultureInfo.InvariantCulture)}",
            "-f", "lavfi", "-i", $"sine=frequency={freq.ToString(CultureInfo.InvariantCulture)}:sample_rate=44100:duration={seconds.ToString(CultureInfo.InvariantCulture)}",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", path);
        return path;
    }

    public static string Probe(string path, string entries, string select = "")
    {
        var psi = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-v", "error", "-show_entries", entries, "-of", "default=nw=1:nk=0" }) psi.ArgumentList.Add(a);
        if (select.Length > 0) { psi.ArgumentList.Add("-select_streams"); psi.ArgumentList.Add(select); }
        psi.ArgumentList.Add(path);
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return o;
    }

    /// <summary>Loudest sample of the file's audio in dBFS, via ffmpeg's volumedetect.</summary>
    public static double PeakDb(string path)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-i", path, "-vn", "-af", "volumedetect", "-f", "null", "-" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var m = System.Text.RegularExpressions.Regex.Match(err, @"max_volume:\s*(-?[\d.]+) dB");
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : double.NaN;
    }
}

internal static class AuditWindowExtensions
{
    public static void Call_NudgeReset(this MainWindow window, A_Capella.App.ViewModels.LayerRowViewModel row)
    {
        var m = typeof(MainWindow).GetMethod("NudgeLabel_PointerPressed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        m.Invoke(window, new object?[] { new Avalonia.Controls.Button { DataContext = row }, null });
    }

    public static void Call_Apply(this MainWindow window, A_Capella.App.ViewModels.LayerRowViewModel row, A_Capella.Engine.Presets.FxPackage package)
        => row.ApplyPackage(package);
}
