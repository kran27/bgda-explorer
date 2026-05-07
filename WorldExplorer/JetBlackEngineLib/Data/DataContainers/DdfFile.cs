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

    /// <summary>
    /// Map from CLP entry hash to the role inferred from the DDF asset offset
    /// at which it was referenced. Verified empirically: across the shipped
    /// data, +0x2c never holds a TEX, +0x30 never holds a VIF, +0x38 holds a
    /// VAG ~75% of the time. So the offset is a more authoritative type
    /// signal than content sniffing for entries DDF actually mentions.
    /// </summary>
    public IReadOnlyDictionary<uint, AssetRole> RoleByClpHash => _roleByClpHash;

    /// <summary>
    /// Map from a mesh's CLP hash to the texture's CLP hash that shares its
    /// DDF record. When several DDF records all reference the same entity
    /// (e.g. multiple "Bottle Caps" item variants), each record's mesh and
    /// texture get bound *to each other* here, so a viewer can pick the right
    /// texture for a specific mesh instead of guessing.
    /// </summary>
    public IReadOnlyDictionary<uint, uint> TextureForMesh => _textureForMesh;

    /// <summary>Reverse of <see cref="TextureForMesh"/>: texture hash → mesh hash.</summary>
    public IReadOnlyDictionary<uint, uint> MeshForTexture => _meshForTexture;

    public enum AssetRole { Mesh, Texture, Sound, Other }

    private readonly Dictionary<uint, string> _nameByClpHash = new();
    private readonly Dictionary<uint, IReadOnlyList<uint>> _siblingsByClpHash = new();
    private readonly Dictionary<uint, AssetRole> _roleByClpHash = new();
    private readonly Dictionary<uint, uint> _textureForMesh = new();
    private readonly Dictionary<uint, uint> _meshForTexture = new();

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
        _roleByClpHash.Clear();
        _textureForMesh.Clear();
        _meshForTexture.Clear();

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
        // Asset records hold a *series of pair slots*, each occupying 8 bytes:
        //   pair A: +0x2c (mesh-or-sound) +0x30 (texture-or-zero)
        //   pair B: +0x38 (mesh-or-sound) +0x3c (texture-or-zero)
        //   pair C: +0x44 (mesh-or-sound) +0x48 (texture-or-zero)   [rare]
        // separated by zero u32s at +0x34, +0x40.
        //
        // Within a pair, the rule is:
        //   - both halves hold CLP hashes  →  (mesh, texture) for that body
        //                                     part / variant
        //   - first half holds a CLP hash, second is zero  →  the first half
        //                                     is a secondary asset (sound for
        //                                     items, sometimes an icon)
        //
        // For most item records only pair A is filled and pair B's first
        // half holds a sound. For characters with multiple body parts (Nadia
        // body + hair, etc.) both pair A and pair B carry mesh+texture pairs.
        //
        // The other lone slots (+0x34, +0x40, +0x44 alone) sometimes pick up
        // refs from wrapper/stub records whose inner record happens to
        // coincide. Don't attribute through those — see the "Bottle Caps"
        // wrapper at @0x4a1c0 → cw_mutant_telephonepole_2600 case.
        var pairOffsets = new[] { (0x2c, 0x30), (0x38, 0x3c), (0x44, 0x48) };

        for (var off = 16; off + 0x48 <= FileData.Length; off += 4)
        {
            var primary = BitConverter.ToUInt32(FileData, off);
            if (primary == 0 || !sdbNames.TryGetValue(primary, out var entityName))
            {
                continue;
            }

            HashSet<uint>? bag = null;
            foreach (var (meshOff, texOff) in pairOffsets)
            {
                if (off + texOff + 4 > FileData.Length) break;
                var meshCandidate = BitConverter.ToUInt32(FileData, off + meshOff);
                var texCandidate = BitConverter.ToUInt32(FileData, off + texOff);

                var meshIsClp = meshCandidate != 0 && !sdbNames.ContainsKey(meshCandidate)
                                && knownClpHashes.Contains(meshCandidate);
                var texIsClp = texCandidate != 0 && !sdbNames.ContainsKey(texCandidate)
                               && knownClpHashes.Contains(texCandidate);

                if (meshIsClp && texIsClp)
                {
                    // Full (mesh, texture) pair — bind them to each other.
                    Attribute(meshCandidate, AssetRole.Mesh, entityName, ref bag, entityToHashes);
                    Attribute(texCandidate, AssetRole.Texture, entityName, ref bag, entityToHashes);
                    if (!_textureForMesh.ContainsKey(meshCandidate))
                        _textureForMesh[meshCandidate] = texCandidate;
                    if (!_meshForTexture.ContainsKey(texCandidate))
                        _meshForTexture[texCandidate] = meshCandidate;
                }
                else if (meshIsClp && texCandidate == 0)
                {
                    // Lone first-half slot with a zero second half — treat as
                    // a secondary asset (sound for items, occasionally icon).
                    Attribute(meshCandidate, AssetRole.Sound, entityName, ref bag, entityToHashes);
                }
                // Anything else (texture-only, or non-zero non-CLP halves)
                // we skip — those are stat fields or wrapper-record bleed.
            }
        }

        // (sibling materialisation follows)
        FinishSiblings(entityToHashes);
    }

    private void Attribute(uint candidate, AssetRole role, string entityName,
        ref HashSet<uint>? bag, Dictionary<string, HashSet<uint>> entityToHashes)
    {
        if (bag == null && !entityToHashes.TryGetValue(entityName, out bag))
        {
            bag = new HashSet<uint>();
            entityToHashes[entityName] = bag;
        }
        bag!.Add(candidate);
        if (!_nameByClpHash.ContainsKey(candidate)) _nameByClpHash[candidate] = entityName;
        if (!_roleByClpHash.ContainsKey(candidate)) _roleByClpHash[candidate] = role;
    }

    private void FinishSiblings(Dictionary<string, HashSet<uint>> entityToHashes)
    {
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
