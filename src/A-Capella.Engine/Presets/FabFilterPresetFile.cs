using System.Globalization;
using System.Text;

namespace A_Capella.Engine.Presets;

/// <summary>
/// One FabFilter factory preset (.ffp). Newer presets are INI-style text with named values;
/// older ones (Pro-DS, Pro-L 2 and many Pro-R 2 presets) are a short binary header followed by a
/// float array. Values are in the plugin's own internal units, so they are only ever copied, never
/// interpreted.
/// </summary>
public sealed class FabFilterPresetFile
{
    /// <summary>Ordered (key, value) pairs; empty for a binary preset.</summary>
    public IReadOnlyList<KeyValuePair<string, float>> Named { get; }

    /// <summary>The float array of a binary preset; empty for a text preset.</summary>
    public IReadOnlyList<float> Binary { get; }

    public string Signature { get; }
    public int Version { get; }
    public bool IsBinary => Binary.Count > 0;

    private FabFilterPresetFile(string signature, int version, List<KeyValuePair<string, float>> named, float[] binary)
    {
        Signature = signature;
        Version = version;
        Named = named;
        Binary = binary;
    }

    public static FabFilterPresetFile Parse(byte[] bytes)
    {
        if (bytes.Length >= 12 && !LooksLikeText(bytes))
        {
            string sig = Encoding.ASCII.GetString(bytes, 0, 4);
            int version = BitConverter.ToInt32(bytes, 4);
            int count = BitConverter.ToInt32(bytes, 8);
            if (count < 0 || count > 100000 || bytes.Length < 12 + 4L * count)
                throw new FormatException("Truncated binary preset.");
            var floats = new float[count];
            Buffer.BlockCopy(bytes, 12, floats, 0, count * 4);
            return new FabFilterPresetFile(sig, version, new List<KeyValuePair<string, float>>(), floats);
        }

        var named = new List<KeyValuePair<string, float>>();
        string signature = "";
        int ver = 0;
        string section = "";
        foreach (var raw in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line[0] == '[') { section = line.Trim('[', ']', ' '); continue; }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq];
            string value = line[(eq + 1)..];
            if (section == "Parameters")
            {
                if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                    named.Add(new(key, f));
            }
            else if (section == "Preset")
            {
                if (key == "Signature") signature = value;
                else if (key == "Version") int.TryParse(value, out ver);
            }
        }
        if (named.Count == 0) throw new FormatException("Preset has no parameters.");
        return new FabFilterPresetFile(signature, ver, named, Array.Empty<float>());
    }

    private static bool LooksLikeText(byte[] b) => b[0] is 0x5B or 0x20 or 0x09 or 0x0D or 0x0A or 0x3B || (b.Length > 2 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF);
}
