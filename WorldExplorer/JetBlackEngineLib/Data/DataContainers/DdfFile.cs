using System.IO;

namespace JetBlackEngineLib.Data.DataContainers;

/// <summary>
/// Reader for Fallout: Brotherhood of Steel ".DDF" data definition files.
/// ALL.DDF and the per-level *.DDF files are the master entity tables: they
/// list every named game object (characters, armor, weapons, particle effects,
/// dialogue triggers, …) and associate each entity with the CLP archive hashes
/// of the assets it uses (mesh, texture, animation, skeleton) plus inline floats
/// holding stats.
///
/// File layout (verified against the Xbox decomp at default.xbe.c lines
/// 93870–93940 and the PS2 binary):
///
///   0x00  u32  totalRecordCount
///   0x04  u32  secondaryCount
///   0x08  u32  numCategories               (always 19 in shipped data)
///   0x0C  19 × 40-byte category descriptors (760 bytes)
///   ...   totalRecordCount × 12-byte directory entries:
///                +0x00  u32  entity SDB hash
///                +0x04  u32  file_offset of this entity's record
///                +0x08  u32  zero on disk (becomes mem-pointer at runtime)
///   ...   variable-size entity records, each:
///                +0x00  u32  category code (drives schema dispatch)
///                +0x10  u32  total record size in bytes
///                +0x30+ u32  asset-reference array (CLP hashes)
///
/// Engine asset resolution (xbe.c line 93699 + sub_6B060/sub_6B0A0/...):
/// the engine switches on the category code at record+0x00, calls a per-
/// category resolver, and that resolver walks the asset-ref array starting
/// at record+0x30 in parallel with a hardcoded type-code table of the same
/// length. Each non-zero hash is loaded with its corresponding type code:
///   1 = mesh, 2 = texture, 3 = sound, 5–7 = cat-8 subsystems we haven't
///   decoded, 9 / −1 = "skip, not an asset".
/// Type 3 was empirically verified as sound: every named CLP hash that
/// appears at a type-3 slot (~7000 references across all DDFs) has a .vag
/// extension in the recovered name table.
/// Type tables for each category were lifted from the Xbox binary's data
/// section and are reproduced in <see cref="CategoryTypeTables"/>.
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
    /// Map from CLP entry hash to the role the engine uses to load it (mesh,
    /// texture, skeleton, animation, or other). Driven by the entity record's
    /// category code and the per-category type-code table the engine consults.
    /// </summary>
    public IReadOnlyDictionary<uint, AssetRole> RoleByClpHash => _roleByClpHash;

    /// <summary>
    /// Map from a mesh's CLP hash to the texture's CLP hash that immediately
    /// follows it in the same record's asset-ref array. Used so the model
    /// viewer can pick the right texture for a clicked mesh.
    /// </summary>
    public IReadOnlyDictionary<uint, uint> TextureForMesh => _textureForMesh;

    /// <summary>Reverse of <see cref="TextureForMesh"/>: texture hash → mesh hash.</summary>
    public IReadOnlyDictionary<uint, uint> MeshForTexture => _meshForTexture;

    /// <summary>One entry per parsed entity record in the DDF.</summary>
    public IReadOnlyList<EntityRecord> Entities => _entities;

    public enum AssetRole
    {
        /// <summary>Type 1 — BGDA-1 mesh (.vif).</summary>
        Mesh,
        /// <summary>Type 2 — GIF-tagged PS2 texture (.tex).</summary>
        Texture,
        /// <summary>Type 3 — sound (.vag, custom SFX, ADPCM).</summary>
        Sound,
        /// <summary>
        /// Type 4 — animation clip (.anm). In this engine animations are
        /// self-contained: each .anm carries its own SkeletonDef, BindingPose,
        /// and NumBones, so there's no separate skeleton asset. Type-4
        /// references appear only at deep offsets inside cat-0 (character)
        /// records — 68 anim slots per body part starting at +0x218. None
        /// appear in the main +0x30 asset array of any category.
        /// </summary>
        Animation,
        /// <summary>
        /// Types 5–7 — vestigial cat-8 subsystem slots. The engine has
        /// post-load handlers for them (sub_1A8E88 for type 5, sub_195E78 for
        /// type 7) but every type-5/6/7 hash in shipped DDFs resolves to
        /// nothing: 0/245 unique references match any CLP entry, and 0/245
        /// match any SDB entity hash. Cat-8's only live asset is its slot-7
        /// texture, which is the same 64×64 placeholder (hash 0xE897E1CA)
        /// bundled identically into every level CLP. Treat these as cut
        /// content — there's no extension to give them because the data
        /// they'd be doesn't exist.
        /// </summary>
        Other,
    }

    public sealed class EntityRecord
    {
        public string Name { get; }
        public uint SdbHash { get; }
        public int RecordOffset { get; }
        public int CategoryCode { get; }
        public List<EntityAsset> Assets { get; } = new();
        internal EntityRecord(string name, uint sdbHash, int recordOffset, int categoryCode)
        {
            Name = name;
            SdbHash = sdbHash;
            RecordOffset = recordOffset;
            CategoryCode = categoryCode;
        }
    }

    public sealed record EntityAsset(uint Hash, AssetRole Role, uint? PairedHash);

    private readonly Dictionary<uint, string> _nameByClpHash = new();
    private readonly Dictionary<uint, IReadOnlyList<uint>> _siblingsByClpHash = new();
    private readonly Dictionary<uint, AssetRole> _roleByClpHash = new();
    private readonly Dictionary<uint, uint> _textureForMesh = new();
    private readonly Dictionary<uint, uint> _meshForTexture = new();
    private readonly List<EntityRecord> _entities = new();

    public DdfFile(string name, byte[] data)
    {
        Name = name;
        FileData = data;
    }

    public static DdfFile Read(string path)
        => new(Path.GetFileName(path), File.ReadAllBytes(path));

    /// <summary>
    /// Per-category type-code tables, lifted from the Xbox binary at the
    /// addresses noted alongside. Each array's length is the number of
    /// asset-ref slots the category's resolver walks at record+0x30. Entries
    /// of value 9 (or −1, the cat-8 sentinel) mean "this slot isn't an asset
    /// reference, skip it" — the engine still reads the slot but doesn't
    /// dispatch a load.
    ///
    /// Categories absent from this table (5, 7, 9, 10, 11, 14–18) fall
    /// through the engine's switch with no asset references at all.
    /// </summary>
    private static readonly IReadOnlyDictionary<uint, int[]> CategoryTypeTables =
        new Dictionary<uint, int[]>
        {
            // Cat 0 (sub_6B170): walks E5B20..E5B34 — 5 slots.
            { 0,  new[] { 1, 2, 2, 1, 2 } },
            // Cat 1 (sub_6B390): walks E5B40..E5B68 — 10 slots.
            { 1,  new[] { 1, 2, 2, 1, 2, 2, 2, 2, 2, 3 } },
            // Cat 2 (sub_6B300): walks E5B9C..E5BB4 — 6 slots.
            { 2,  new[] { 1, 2, 2, 3, 3, 3 } },
            // Cat 3 (sub_6B120): walks E5B68..E5B78 — 4 slots.
            { 3,  new[] { 1, 2, 2, 3 } },
            // Cat 4 (sub_6B060): walks E5B14..E5B3C — 10 slots.
            { 4,  new[] { 1, 2, 2, 1, 2, 2, 1, 2, 3, 3 } },
            // Cat 5: particle emitters (fireemitter, sparks emitter, steam_*,
            // toxicsmoke_*, etc.). Pure parameter records — no resolver in the
            // engine's switch, no asset references. 6,070 records across all
            // shipped DDFs. Surfaced as entities with empty Assets list.
            { 5,  Array.Empty<int>() },
            // Cat 6 (sub_6B0A0): walks E5B34..E5B40 — only 3 slots (sound, sound, texture).
            // Easy mistake: E5B40 is also the start of cat 1's table, but that's
            // the *exclusive bound* here, not the start. Reading bytes directly
            // confirms 3 entries.
            { 6,  new[] { 3, 3, 2 } },
            // Cat 8 (sub_6B4A0): walks E5BB4..E5BDC — 10 slots, includes sentinels.
            { 8,  new[] { 5, 6, 2, 7, -1, 9, 9, 2, 2, 2 } },
            // Cat 9: AI behavior types (RangedBasic, MeleeBasic, DuckAndCover,
            // Player, NPC, Driving, Kamikaze, named-character AIs like
            // WastelandMayorAI). Pure parameter records — 1,210 across shipped
            // DDFs.
            { 9,  Array.Empty<int>() },
            // Cat 10: dynamic light definitions (gen_light_green_*, world_light_default,
            // light_muzzle_blast_small, fireballlight). 543 records, no assets.
            { 10, Array.Empty<int>() },
            // Cat 11: beam / lightning / laser effects (Flame, laser_blue,
            // lightning_arc_*, lightning_trap_*, body_lightning). 391 records,
            // no assets.
            { 11, Array.Empty<int>() },
            // Cat 12 (sub_6B0E0): walks E5B78..E5B9C — 9 slots.
            { 12, new[] { 1, 2, 2, 1, 1, 1, 2, 2, 2 } },
            // Cat 13 (sub_6B060): same resolver as cat 4.
            { 13, new[] { 1, 2, 2, 1, 2, 2, 1, 2, 3, 3 } },
        };

    private const int HeaderSize = 12;
    private const int CategoryDescriptorSize = 40;
    private const int DirectoryEntrySize = 12;
    private const int AssetArrayOffset = 0x30;

    /// <summary>
    /// Walk the DDF and build the (entity → assets) graph using the engine's
    /// own category dispatch table.
    /// </summary>
    /// <param name="sdbNames">SDB hash → display name lookup. Used to attach a
    /// human-readable name to each parsed entity.</param>
    /// <param name="knownClpHashes">Set of CLP entry hashes from the loaded
    /// archives. Slots holding values not in this set are dropped — they
    /// reference assets in archives we don't have open.</param>
    public void Parse(IReadOnlyDictionary<uint, string> sdbNames, IReadOnlySet<uint> knownClpHashes)
    {
        _nameByClpHash.Clear();
        _siblingsByClpHash.Clear();
        _roleByClpHash.Clear();
        _textureForMesh.Clear();
        _meshForTexture.Clear();
        _entities.Clear();

        if (FileData.Length < HeaderSize) return;

        var totalRecords = BitConverter.ToUInt32(FileData, 0);
        var numCategories = BitConverter.ToUInt32(FileData, 8);
        if (numCategories == 0 || numCategories > 64) return;

        var directoryOffset = HeaderSize + (int)numCategories * CategoryDescriptorSize;
        if (directoryOffset + (long)totalRecords * DirectoryEntrySize > FileData.Length) return;

        var entityToHashes = new Dictionary<uint, HashSet<uint>>();

        for (var i = 0u; i < totalRecords; i++)
        {
            var entryOffset = directoryOffset + (int)i * DirectoryEntrySize;
            var entityHash = BitConverter.ToUInt32(FileData, entryOffset);
            var recordOffset = (int)BitConverter.ToUInt32(FileData, entryOffset + 4);

            if (recordOffset < directoryOffset || recordOffset + AssetArrayOffset > FileData.Length)
                continue;

            var category = BitConverter.ToUInt32(FileData, recordOffset);
            if (!CategoryTypeTables.TryGetValue(category, out var typeTable))
                continue;

            var assetArrayStart = recordOffset + AssetArrayOffset;
            if (assetArrayStart + typeTable.Length * 4 > FileData.Length) continue;

            // Skip records whose entity hash isn't in any loaded SDB. The
            // engine resolves these via inter-DDF cross-references at runtime,
            // but for the explorer's tree it just produces hash placeholders
            // with no way to identify what they are. The caller can broaden
            // SDB coverage if it wants more entities visible.
            if (!sdbNames.TryGetValue(entityHash, out var entityName)) continue;
            var entity = new EntityRecord(entityName, entityHash, recordOffset, (int)category);

            // Walk the asset-ref array in the engine's order, attributing each
            // slot using the category's type table. Pair each mesh with the
            // first texture that follows it before the next mesh.
            //
            // Cat 12 (debris) special-case: the schema lists 4 meshes and 5
            // textures, but the meshes are interchangeable LOD/variant pieces
            // and they all share a single texture. Pre-find that lone texture
            // and pair every mesh in the record to it instead of doing the
            // walk-pairing.
            uint? cat12SharedTex = null;
            if (category == 12)
            {
                for (var s = 0; s < typeTable.Length; s++)
                {
                    if (typeTable[s] != 2) continue;
                    var v = BitConverter.ToUInt32(FileData, assetArrayStart + s * 4);
                    if (v != 0 && knownClpHashes.Contains(v)) { cat12SharedTex = v; break; }
                }
            }

            uint? pendingMesh = null;
            for (var slot = 0; slot < typeTable.Length; slot++)
            {
                var typeCode = typeTable[slot];
                if (typeCode == 9 || typeCode == -1) continue; // sentinels — not an asset

                var slotValue = BitConverter.ToUInt32(FileData, assetArrayStart + slot * 4);
                if (slotValue == 0 || !knownClpHashes.Contains(slotValue)) continue;

                var role = TypeCodeToRole(typeCode);

                uint? paired = null;
                if (role == AssetRole.Mesh)
                {
                    if (cat12SharedTex is uint sharedTex)
                    {
                        // Cat-12 override: every mesh maps to the one shared texture.
                        paired = sharedTex;
                        if (!_textureForMesh.ContainsKey(slotValue)) _textureForMesh[slotValue] = sharedTex;
                        if (!_meshForTexture.ContainsKey(sharedTex)) _meshForTexture[sharedTex] = slotValue;
                    }
                    else
                    {
                        pendingMesh = slotValue;
                    }
                }
                else if (role == AssetRole.Texture && pendingMesh is uint mesh)
                {
                    paired = mesh;
                    if (!_textureForMesh.ContainsKey(mesh)) _textureForMesh[mesh] = slotValue;
                    if (!_meshForTexture.ContainsKey(slotValue)) _meshForTexture[slotValue] = mesh;
                    pendingMesh = null; // pair only with the first following texture
                }

                Attribute(slotValue, role, entityHash, entity, entityToHashes);
                entity.Assets.Add(new EntityAsset(slotValue, role, paired));
            }

            // Cat 0 (characters) has additional asset references at deep
            // offsets that the main +0x30 walk doesn't touch. Engine resolver
            // sub_6B170 walks them in this order:
            //   17 type-3 (sound) slots in four groups: 5@+0x17C, 5@+0x190,
            //                                           5@+0x1A4, 2@+0x1B8
            //   then per-body-part records of 71 u32s starting at +0x218,
            //   each containing 68 type-4 (animation) entries followed by
            //   3 trailing u32s. Body-part count is at +0x160. Because each
            //   body part is 284 bytes (71 × 4), max-size cat-0 records
            //   (~2808 bytes) hold ~8 body parts × 68 anims = 544 anim refs.
            //
            // We don't replicate the engine's flag-bit guard at +0x08 — it
            // gates whether body parts get *loaded*, not whether they
            // *exist*. We just sanity-check the body-part count and trust
            // knownClpHashes.Contains() to filter out garbage if a record
            // turns out to be a stub.
            if (category == 0)
            {
                WalkCat0DeepReferences(recordOffset, entity, knownClpHashes, entityHash, entityToHashes);
            }

            // Add the entity even when it has no assets — cats 5/9/10/11 are
            // parameter-only records (particle emitters, AI behaviors, lights,
            // beam effects) that don't reference any CLP entries but are still
            // worth showing in the tree.
            _entities.Add(entity);
        }

        FinishSiblings(entityToHashes);
    }

    private static AssetRole TypeCodeToRole(int typeCode) => typeCode switch
    {
        1 => AssetRole.Mesh,
        2 => AssetRole.Texture,
        3 => AssetRole.Sound,
        4 => AssetRole.Animation,
        _ => AssetRole.Other,
    };

    private void WalkCat0DeepReferences(int recordOffset, EntityRecord entity,
        IReadOnlySet<uint> knownClpHashes, uint entityHash,
        Dictionary<uint, HashSet<uint>> entityToHashes)
    {
        // Four sound blocks the engine walks unconditionally with type code 3.
        var soundBlocks = new (int byteOff, int count)[]
        {
            (0x17C, 5), (0x190, 5), (0x1A4, 5), (0x1B8, 2),
        };
        foreach (var (byteOff, count) in soundBlocks)
        {
            for (var i = 0; i < count; i++)
            {
                var p = recordOffset + byteOff + i * 4;
                if (p + 4 > FileData.Length) break;
                var v = BitConverter.ToUInt32(FileData, p);
                if (v == 0 || !knownClpHashes.Contains(v)) continue;
                Attribute(v, AssetRole.Sound, entityHash, entity, entityToHashes);
                entity.Assets.Add(new EntityAsset(v, AssetRole.Sound, null));
            }
        }

        // Body-part records: 71 u32s each (284 bytes), starting at +0x218,
        // first 68 u32s of each are type-4 animation refs.
        const int BodyPartByteOff = 0x218;
        const int BodyPartStrideBytes = 71 * 4; // 284
        const int AnimsPerBodyPart = 68;
        if (recordOffset + 0x164 > FileData.Length) return;
        var bodyPartCount = (int)BitConverter.ToUInt32(FileData, recordOffset + 0x160);
        if (bodyPartCount <= 0 || bodyPartCount > 32) return; // sanity bound

        for (var bp = 0; bp < bodyPartCount; bp++)
        {
            var bpStart = recordOffset + BodyPartByteOff + bp * BodyPartStrideBytes;
            if (bpStart + AnimsPerBodyPart * 4 > FileData.Length) break;
            for (var anim = 0; anim < AnimsPerBodyPart; anim++)
            {
                var v = BitConverter.ToUInt32(FileData, bpStart + anim * 4);
                if (v == 0 || !knownClpHashes.Contains(v)) continue;
                Attribute(v, AssetRole.Animation, entityHash, entity, entityToHashes);
                entity.Assets.Add(new EntityAsset(v, AssetRole.Animation, null));
            }
        }
    }

    private void Attribute(uint clpHash, AssetRole role, uint entityHash, EntityRecord entity,
        Dictionary<uint, HashSet<uint>> entityToHashes)
    {
        if (!entityToHashes.TryGetValue(entityHash, out var bag))
        {
            bag = new HashSet<uint>();
            entityToHashes[entityHash] = bag;
        }
        bag.Add(clpHash);
        if (!_nameByClpHash.ContainsKey(clpHash)) _nameByClpHash[clpHash] = entity.Name;
        if (!_roleByClpHash.ContainsKey(clpHash)) _roleByClpHash[clpHash] = role;
    }

    private void FinishSiblings(Dictionary<uint, HashSet<uint>> entityToHashes)
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
