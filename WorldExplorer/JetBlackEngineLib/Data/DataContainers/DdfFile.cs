using System.IO;

namespace JetBlackEngineLib.Data.DataContainers;

/// <summary>
/// Reader for Fallout: Brotherhood of Steel ".DDF" data definition files.
/// ALL.DDF and the per-level *.DDF files are the master entity tables: they
/// list every named game object (characters, armor, weapons, particle effects,
/// dialogue triggers, …) and associate each entity with the CLP archive hashes
/// of the assets it uses (mesh, texture, icon, sound) plus inline floats
/// holding stats.
///
/// Layout (16-byte header, then variable-size records):
///   0x00  u32  totalRecordCount
///   0x04  u32  secondaryCount
///   0x08  u32  numCategories         (19 in shipped data)
///   0x0C  u32  hash / build signature
///   0x10+      records of variable size
///
/// The schema is not fully decoded — record sizes vary by category and the
/// per-category layouts aren't fully documented. <see cref="Parse"/> works
/// structurally: it walks the file four bytes at a time, treating each u32
/// that matches an SDB hash as a candidate entity record, and only attributes
/// CLP asset hashes when they appear at one of the *canonical asset offsets*
/// from that SDB hash. See <see cref="Parse"/> for the offsets and rationale.
/// </summary>
public class DdfFile
{
    public string Name { get; }
    public byte[] FileData { get; }

    /// <summary>Map from CLP entry hash to its primary entity name (from SDB).</summary>
    public IReadOnlyDictionary<uint, string> NameByClpHash => _nameByClpHash;

    /// <summary>
    /// Map from CLP entry hash to the other CLP hashes that share its DDF
    /// record. Used to pair, e.g., a mesh entry with its texture entry when
    /// the on-disk archive has no naming convention to do it with.
    /// </summary>
    public IReadOnlyDictionary<uint, IReadOnlyList<uint>> SiblingsByClpHash => _siblingsByClpHash;

    private readonly Dictionary<uint, string> _nameByClpHash = new();
    private readonly Dictionary<uint, IReadOnlyList<uint>> _siblingsByClpHash = new();

    public DdfFile(string name, byte[] data)
    {
        Name = name;
        FileData = data;
    }

    public static DdfFile Read(string path)
        => new(Path.GetFileName(path), File.ReadAllBytes(path));

    /// <summary>
    /// Walk the DDF and build the (entity → assets) graph.
    /// </summary>
    /// <param name="sdbNames">SDB hash → display name lookup, used to identify entity records.</param>
    /// <param name="knownClpHashes">Set of CLP entry hashes from the loaded archives, used to pick out asset references.</param>
    public void Parse(IReadOnlyDictionary<uint, string> sdbNames, IReadOnlySet<uint> knownClpHashes)
    {
        _nameByClpHash.Clear();
        _siblingsByClpHash.Clear();

        if (FileData.Length < 16) return;

        // SDB hashes appear in the DDF in two roles:
        //   1. as the +0x00 *header* of an asset record (the entity that owns
        //      the bytes following), and
        //   2. as inline cross-references inside *other* records — loot tables,
        //      ammo conversion tables, dialogue trees, etc.
        //
        // We can't tell role from the hash alone, but role-1 records put their
        // CLP asset hashes at fixed offsets from the SDB header (+0x2c..+0x44
        // in shipped BoS data); role-2 reference lists hold *other SDB hashes*
        // (or NaN sentinels) at those same offsets. So the rule is: only treat
        // an SDB hash as the owner of a CLP hash when the CLP hash sits at one
        // of the canonical asset slots and is actually a known CLP hash, not
        // an SDB hash. This avoids attribution leakage between unrelated
        // entities that share a record neighbourhood.
        var entityToHashes = new Dictionary<string, HashSet<uint>>();
        ReadOnlySpan<int> assetOffsets = stackalloc int[]
            { 0x2c, 0x30, 0x34, 0x38, 0x3c, 0x40, 0x44 };

        for (var off = 16; off + 0x48 <= FileData.Length; off += 4)
        {
            var primary = BitConverter.ToUInt32(FileData, off);
            if (primary == 0 || !sdbNames.TryGetValue(primary, out var entityName))
            {
                continue;
            }

            HashSet<uint>? bag = null;
            foreach (var rel in assetOffsets)
            {
                var assetOff = off + rel;
                if (assetOff + 4 > FileData.Length) break;
                var candidate = BitConverter.ToUInt32(FileData, assetOff);
                if (candidate == 0 || sdbNames.ContainsKey(candidate)) continue;
                if (!knownClpHashes.Contains(candidate)) continue;

                if (bag == null && !entityToHashes.TryGetValue(entityName, out bag))
                {
                    bag = new HashSet<uint>();
                    entityToHashes[entityName] = bag;
                }
                bag!.Add(candidate);
                if (!_nameByClpHash.ContainsKey(candidate))
                {
                    _nameByClpHash[candidate] = entityName;
                }
            }
        }

        // Materialise sibling lists.
        foreach (var (_, hashes) in entityToHashes)
        {
            var list = hashes.ToList();
            foreach (var h in list)
            {
                if (_siblingsByClpHash.ContainsKey(h)) continue;
                _siblingsByClpHash[h] = list.Where(other => other != h).ToList();
            }
        }
    }
}
