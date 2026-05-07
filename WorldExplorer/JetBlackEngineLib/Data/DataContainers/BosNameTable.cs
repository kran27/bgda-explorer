using System.IO;
using System.Reflection;

namespace JetBlackEngineLib.Data.DataContainers;

/// <summary>
/// Maps Fallout: Brotherhood of Steel CLP entry hashes to their original
/// asset filenames (and, transitively, their file types via the extension).
///
/// Built from string literals preserved in the shipped game binaries (PS2
/// SLUS_205.39 and the Xbox default.xbe). Each candidate string is run
/// through the engine's CLP-name hash:
/// <code>
/// hash = 0; mul = 0
/// for each char c (with '\\' folded to '/'):
///     hash = (hash >> 27) ^ mul ^ c
///     mul  = (uint)(hash * 0x80000025u)   // 32-bit modular
/// </code>
/// matched against the union of every CLP archive's directory hashes.
/// Currently covers ~311 of ~7900 entries; extensions seen include
/// <c>.tex .vif .vag .anm .cut .lmp .bin .hsh .m2v .va1 .skl .fnt</c>.
///
/// The table is the most authoritative type signal we have for any entry
/// it covers — the extension is the original filename's, not a guess from
/// content sniffing.
/// </summary>
public static class BosNameTable
{
    private static readonly Dictionary<uint, string> Names = Load();

    private static Dictionary<uint, string> Load()
    {
        var names = new Dictionary<uint, string>();
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("bos_name_table.txt"));
        if (resourceName == null) return names;
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null) return names;
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var sp = line.IndexOf(' ');
            if (sp < 8) continue;
            if (uint.TryParse(line.AsSpan(0, sp),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var hash))
            {
                names[hash] = line.Substring(sp + 1);
            }
        }
        return names;
    }

    /// <summary>Returns the original filename for a CLP entry hash, or null if unknown.</summary>
    public static string? Get(uint hash) => Names.TryGetValue(hash, out var n) ? n : null;

    /// <summary>Compute the engine's CLP-name hash of a candidate filename.</summary>
    public static uint Hash(string path)
    {
        uint hash = 0, mul = 0;
        foreach (var ch in path)
        {
            uint c = ch == '\\' ? (uint)'/' : (uint)(byte)(sbyte)ch;
            hash = (hash >> 27) ^ mul ^ c;
            mul = hash * 0x80000025u;
        }
        return hash;
    }
}
