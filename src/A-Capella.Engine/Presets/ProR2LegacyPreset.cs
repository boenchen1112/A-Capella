using System.Globalization;

namespace A_Capella.Engine.Presets;

/// <summary>
/// About two thirds of Pro-R 2's factory presets are stored in the older binary layout ("FPRr",
/// 85 floats). This maps that layout onto the named values Pro-R 2's current layout uses, so both
/// kinds apply the same way. Only settings the old layout has are set; the rest keep the plugin's
/// defaults.
/// </summary>
internal static class ProR2LegacyPreset
{
    public const string Signature = "FPRr";

    private const float UnusedEqFrequency = 9.9658f;

    public static FabFilterPresetFile ToNamed(FabFilterPresetFile legacy)
    {
        var f = legacy.Binary;
        if (f.Count < 85) throw new FormatException("Unexpected Pro-R 2 legacy preset size.");
        var lines = new List<string>
        {
            "[Preset]", "Signature=FPRr", "Version=" + legacy.Version.ToString(CultureInfo.InvariantCulture), "[Parameters]",
        };
        void Add(string key, float value) => lines.Add(key + "=" + value.ToString("R", CultureInfo.InvariantCulture));

        Add("Space", f[0]);
        Add("Decay Rate", f[1]);
        Add("Distance", f[2]);
        Add("Brightness", f[3]);
        Add("Character", f[4]);
        Add("Stereo Width", f[5]);
        Add("Mix", f[6]);
        Add("Lock Mix", f[7]);
        Add("Predelay", f[74]);
        Add("Predelay Offset", f[75]);
        Add("Predelay Sync", f[76]);

        for (int band = 0; band < 6; band++)
        {
            int p = 9 + band * 5;
            float freq = f[p], rate = f[p + 1], q = f[p + 2], shape = f[p + 3];
            bool used = Math.Abs(freq - UnusedEqFrequency) > 1e-3f || rate != 0f || Math.Abs(q - 0.5f) > 1e-3f;
            string n = $"Decay EQ Band {band + 1} ";
            Add(n + "Used", used ? 1 : 0);
            Add(n + "Enabled", used ? 1 : 0);
            Add(n + "Frequency", freq);
            Add(n + "Rate", rate);
            Add(n + "Q", q);
            Add(n + "Shape", shape);
        }

        for (int band = 0; band < 6; band++)
        {
            int p = 39 + band * 6;
            float freq = f[p], gain = f[p + 1], q = f[p + 2], shape = f[p + 3], slope = f[p + 4];
            // The sixth band has no flag of its own; a band is in use when it has gain or a cut/shelf shape.
            bool used = band < 5 ? f[p + 5] == 1f : gain != 0f;
            string n = $"Post EQ Band {band + 1} ";
            Add(n + "Used", used ? 1 : 0);
            Add(n + "Enabled", used ? 1 : 0);
            Add(n + "Frequency", freq);
            Add(n + "Gain", gain);
            Add(n + "Q", q);
            Add(n + "Shape", shape);
            Add(n + "Slope", slope);
        }

        return FabFilterPresetFile.Parse(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines)));
    }
}
