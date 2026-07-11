using Acapella.Engine.Devices;
using Acapella.Engine.Settings;
using Acapella.Engine.Sync;

var catalog = new DeviceCatalog();

Console.WriteLine("=== WASAPI Render (output) devices ===");
var renderDevices = catalog.GetWasapiRenderDevices();
foreach (var d in renderDevices) Console.WriteLine($"  {d.Id} :: {d.Name}");

Console.WriteLine("=== WASAPI Capture (input) devices ===");
var captureDevices = catalog.GetWasapiCaptureDevices();
foreach (var d in captureDevices) Console.WriteLine($"  {d.Id} :: {d.Name}");

if (args.Length > 0 && args[0] == "listonly")
    return;

var outputDevice = renderDevices.FirstOrDefault(d => d.Name.Contains("Realtek", StringComparison.OrdinalIgnoreCase))
    ?? renderDevices.FirstOrDefault();
var loopbackDevice = captureDevices.FirstOrDefault(d => d.Name.Contains("Stereo Mix", StringComparison.OrdinalIgnoreCase));

if (outputDevice is null || loopbackDevice is null)
{
    Console.WriteLine("Could not find output device or Stereo Mix loopback device. Aborting.");
    return;
}

Console.WriteLine($"\nUsing output: {outputDevice.Name}");
Console.WriteLine($"Using loopback input: {loopbackDevice.Name}\n");

var settingsPath = Path.Combine(Path.GetTempPath(), "acapella-hwcheck-settings.json");
if (File.Exists(settingsPath)) File.Delete(settingsPath);
var settingsService = new SettingsService(settingsPath);
var calibrator = new LatencyCalibrator(settingsService);

Console.WriteLine("--- Check 1: Latency calibration (run twice for consistency) ---");
double offset1 = calibrator.CalibrateAndSave(outputDevice.Id, loopbackDevice.Id);
Console.WriteLine($"Run 1 offset: {offset1:F1} ms");
double offset2 = calibrator.CalibrateAndSave(outputDevice.Id, loopbackDevice.Id);
Console.WriteLine($"Run 2 offset: {offset2:F1} ms");
double consistency = Math.Abs(offset1 - offset2);
// WASAPI shared-mode buffer quantization is itself ~20ms, so run-to-run jitter of up to
// one buffer is expected, not a calibration bug. Threshold set above that floor.
Console.WriteLine($"Consistency delta: {consistency:F1} ms -> {(consistency < 40 ? "PASS" : "FAIL")}");

Console.WriteLine("\n--- Check 2: Settings persistence across reload ---");
var reloadedSettings = new SettingsService(settingsPath);
double? reloaded = reloadedSettings.GetLatencyOffsetMs(loopbackDevice.Id, outputDevice.Id);
bool persistPass = reloaded.HasValue && Math.Abs(reloaded.Value - offset2) < 0.01;
Console.WriteLine($"Reloaded offset: {reloaded:F1} ms (saved: {offset2:F1} ms) -> {(persistPass ? "PASS" : "FAIL")}");

Console.WriteLine("\nDone.");
