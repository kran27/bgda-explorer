using System.IO;
using System.Reflection;

namespace JetBlackEngineLib.Data.DataContainers;

/// <summary>
/// Internal entity names extracted from the Xbox release's <c>deftexte.sdb</c>.
/// PS2 retail and PS2 beta ship the same DDF entity slots that reference these
/// hashes but don't include this SDB.
/// </summary>
public static class BosEntityNameTable
{
    private static readonly Dictionary<uint, string> Names = Load();

    public static IReadOnlyDictionary<uint, string> All => Names;

    private static Dictionary<uint, string> Load()
    {
        var names = new Dictionary<uint, string>();
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("bos_entity_names.txt"));
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

    public static string? Get(uint hash) => Names.TryGetValue(hash, out var n) ? n : null;
}
