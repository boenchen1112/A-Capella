namespace A_Capella.Engine.Presets;

/// <summary>JUCE's MemoryBlock base-64 text form ("&lt;byte count&gt;.&lt;data&gt;"), which plugins use inside their saved state.</summary>
internal static class JuceBase64
{
    private const string Alphabet = ".ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+";

    public static byte[] Decode(string text)
    {
        int dot = text.IndexOf('.');
        if (dot <= 0 || !int.TryParse(text.AsSpan(0, dot), out int size) || size < 0)
            throw new FormatException("Not a JUCE base-64 block.");
        var output = new byte[size];
        int written = 0;
        uint bits = 0;
        int bitCount = 0;
        for (int i = dot + 1; i < text.Length && written < size; i++)
        {
            int v = Alphabet.IndexOf(text[i]);
            if (v < 0) throw new FormatException("Invalid base-64 character.");
            bits |= (uint)v << bitCount;
            bitCount += 6;
            while (bitCount >= 8 && written < size)
            {
                output[written++] = (byte)(bits & 0xFF);
                bits >>= 8;
                bitCount -= 8;
            }
        }
        if (written != size) throw new FormatException("Base-64 block is shorter than its declared size.");
        return output;
    }

    public static string Encode(byte[] data)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(data.Length).Append('.');
        uint bits = 0;
        int bitCount = 0;
        foreach (byte b in data)
        {
            bits |= (uint)b << bitCount;
            bitCount += 8;
            while (bitCount >= 6)
            {
                sb.Append(Alphabet[(int)(bits & 63)]);
                bits >>= 6;
                bitCount -= 6;
            }
        }
        if (bitCount > 0) sb.Append(Alphabet[(int)(bits & 63)]);
        return sb.ToString();
    }
}
