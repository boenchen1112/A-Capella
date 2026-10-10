using System.Text;

namespace A_Capella.Engine.Presets;

/// <summary>
/// Builds a FabFilter plugin state blob that carries a factory preset. The plugins save their
/// settings as a JUCE "VC2!" wrapper around VST3 state, and FabFilter's own component state holds a
/// plain float array in the same internal units as the .ffp files, so a preset is applied by taking
/// the plugin's untouched state and swapping that float array.
/// </summary>
public static class FabFilterPresetState
{
    private const string ComponentOpen = "<IComponent>";
    private const string ComponentClose = "</IComponent>";

    /// <summary>The float array inside a state blob (the plugin's settings in internal units).</summary>
    public static float[] ReadFloats(byte[] state)
    {
        var component = ReadComponent(state);
        var (offset, count) = Locate(component);
        var floats = new float[count];
        Buffer.BlockCopy(component, offset, floats, 0, count * 4);
        return floats;
    }

    /// <summary>
    /// A state like <paramref name="defaultState"/> (the plugin's untouched state) but holding the
    /// preset's values. <paramref name="defaultPreset"/> is the plugin's own "Default Setting"
    /// preset, whose key order says where each named value of a text preset goes.
    /// </summary>
    public static byte[] Build(byte[] defaultState, FabFilterPresetFile preset, FabFilterPresetFile? defaultPreset)
    {
        if (preset.IsBinary && preset.Signature == ProR2LegacyPreset.Signature)
            preset = ProR2LegacyPreset.ToNamed(preset);

        var component = ReadComponent(defaultState);
        var (offset, count) = Locate(component);
        var floats = new float[count];
        Buffer.BlockCopy(component, offset, floats, 0, count * 4);

        if (preset.IsBinary)
        {
            // Older presets list the first N values of the same array.
            for (int i = 0; i < Math.Min(preset.Binary.Count, count); i++) floats[i] = preset.Binary[i];
        }
        else
        {
            if (defaultPreset is null) throw new InvalidOperationException("A text preset needs the plugin's Default Setting preset for its key order.");
            var index = new Dictionary<string, int>();
            for (int i = 0; i < defaultPreset.Named.Count; i++)
                index.TryAdd(defaultPreset.Named[i].Key, i);
            foreach (var kv in preset.Named)
                if (index.TryGetValue(kv.Key, out int i) && i < count) floats[i] = kv.Value;
        }

        var patched = (byte[])component.Clone();
        Buffer.BlockCopy(floats, 0, patched, offset, count * 4);
        return WriteComponent(defaultState, patched);
    }

    // (offset of the first float, number of floats) inside the component chunk.
    private static (int Offset, int Count) Locate(byte[] c)
    {
        string magic = Encoding.ASCII.GetString(c, 0, 4);
        if (magic == "FFBS")
            return (12, BitConverter.ToInt32(c, 8));
        if (magic == "FabF")
        {
            int nameLength = BitConverter.ToInt32(c, 8);
            int p = 12 + nameLength + 4;
            return (p + 4, BitConverter.ToInt32(c, p));
        }
        throw new FormatException($"Unknown FabFilter state '{magic}'.");
    }

    private static byte[] ReadComponent(byte[] state)
    {
        string text = Encoding.Latin1.GetString(state);
        int a = text.IndexOf(ComponentOpen, StringComparison.Ordinal);
        int b = text.IndexOf(ComponentClose, StringComparison.Ordinal);
        if (state.Length < 8 || state[0] != 'V' || a < 0 || b < a)
            throw new FormatException("Unrecognised plugin state.");
        int start = a + ComponentOpen.Length;
        return JuceBase64.Decode(text.Substring(start, b - start));
    }

    /// <summary>The state with its component chunk replaced by <paramref name="component"/> verbatim.</summary>
    public static byte[] WithComponent(byte[] state, byte[] component) => WriteComponent(state, component);

    private static byte[] WriteComponent(byte[] state, byte[] component)
    {
        string text = Encoding.Latin1.GetString(state);
        int a = text.IndexOf(ComponentOpen, StringComparison.Ordinal) + ComponentOpen.Length;
        int b = text.IndexOf(ComponentClose, StringComparison.Ordinal);
        string rebuilt = text[..a] + JuceBase64.Encode(component) + text[b..];
        var bytes = Encoding.Latin1.GetBytes(rebuilt);
        // The 4-byte length after "VC2!" counts the XML text without its trailing NUL.
        int xmlLength = bytes.Length - 8 - (bytes[^1] == 0 ? 1 : 0);
        BitConverter.GetBytes(xmlLength).CopyTo(bytes, 4);
        return bytes;
    }
}
