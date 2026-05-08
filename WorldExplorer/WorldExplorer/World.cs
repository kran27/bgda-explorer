/*  Copyright (C) 2012 Ian Brown

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using JetBlackEngineLib;
using JetBlackEngineLib.Data.DataContainers;
using JetBlackEngineLib.Data.Textures;
using JetBlackEngineLib.Data.World;
using System;
using System.Collections.Generic;
using System.IO;

namespace WorldExplorer;

public class World
{
    public readonly string DataPath;
    public readonly EngineVersion EngineVersion;
    public readonly string Name;
    public CacheFile? HdrDatFile;

    public WorldData? WorldData = null;

    public GobFile? WorldGob;
    public LmpFile? WorldLmp;
    public WorldTexFile? WorldTex;
    public YakFile? WorldYak;
    public SdbFile? WorldSdb;
    public DdfFile? WorldDdf;

    /// <summary>
    /// CLP entry hash → (archive, entry label) for every archive loaded
    /// alongside an opened DDF. The DDF tree resolves entity asset hashes
    /// through here. Empty for non-DDF flows.
    /// </summary>
    public readonly Dictionary<uint, ClpAssetRef> AssetIndex = new();
    public readonly List<ClpFile> LoadedClps = new();

    public World(EngineVersion engineVersion, string dataPath, string name)
    {
        EngineVersion = engineVersion;
        DataPath = dataPath ?? throw new ArgumentNullException(nameof(dataPath));
        Name = name;
    }

    public void Load()
    {
        var ext = (Path.GetExtension(Name) ?? "").ToLower();

        switch (ext)
        {
            case ".gob":
                var texFileName = Path.GetFileNameWithoutExtension(Name) + ".tex";
                var textFilePath = Path.Combine(DataPath, texFileName);
                WorldGob = new GobFile(EngineVersion, Path.Combine(DataPath, Name));
                WorldTex = File.Exists(textFilePath) ? new WorldTexFile(EngineVersion, textFilePath) : null;
                break;
            case ".lmp":
                var data = File.ReadAllBytes(Path.Combine(DataPath, Name));
                WorldLmp = new LmpFile(EngineVersion, Name, data, 0, data.Length);
                break;
            case ".clp":
                var clpData = File.ReadAllBytes(Path.Combine(DataPath, Name));
                var clp = new ClpFile(EngineVersion, Name, clpData, 0, clpData.Length);
                WorldLmp = clp;
                AttachClpResolvers(clp);
                break;
            case ".ddf":
                WorldDdf = DdfFile.Read(Path.Combine(DataPath, Name));
                LoadDdfWithSiblings();
                break;
            case ".sdb":
                var sdbData = File.ReadAllBytes(Path.Combine(DataPath, Name));
                WorldSdb = new SdbFile(Name, sdbData);
                break;
            case ".yak":
                var yakData = File.ReadAllBytes(Path.Combine(DataPath, Name));
                WorldYak = new YakFile(EngineVersion, Name, yakData);
                break;
            case ".hdr":
                var baseName = Name[..^4];
                var hdrData = File.ReadAllBytes(Path.Combine(DataPath, Name));
                var datData = File.ReadAllBytes(Path.Combine(DataPath, baseName + ".DAT"));
                HdrDatFile = new CacheFile(EngineVersion, baseName, hdrData, datData);
                break;
            default:
                throw new NotSupportedException("Unsupported file type");
        }
    }

    /// <summary>
    /// For an opened .CLP: walk the file's directory + every ancestor up to
    /// the BoS DATA root, collect SDB names + every CLP archive's hashes +
    /// every DDF's role/pair maps, and wire RoleResolver / TexturePairResolver
    /// onto the focus CLP so its directory entries get correct extensions and
    /// the model viewer can pair meshes with the right textures. We don't
    /// keep the other archives loaded — only the focused one.
    /// </summary>
    private void AttachClpResolvers(ClpFile focusClp)
    {
        if (EngineVersion != EngineVersion.BrotherhoodOfSteel) return;

        var directoriesToScan = WalkUpToBosRoot(DataPath);

        var clpHashes = new HashSet<uint>();
        ClpFile.CollectHashesFromBytes(focusClp.FileData, clpHashes);
        foreach (var d in directoriesToScan)
        {
            foreach (var path in Directory.EnumerateFiles(d, "*.CLP", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetFullPath(path),
                        Path.GetFullPath(Path.Combine(DataPath, focusClp.Name)),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                try
                {
                    if (new FileInfo(path).Length > 200_000_000) continue;
                    ClpFile.CollectHashesFromBytes(File.ReadAllBytes(path), clpHashes);
                }
                catch { }
            }
        }

        var sdbNames = LoadAllSdbNames(directoriesToScan);
        if (sdbNames.Count == 0) return;

        var combinedRoles = new Dictionary<uint, DdfFile.AssetRole>();
        var combinedPairs = new Dictionary<uint, uint>();
        foreach (var d in directoriesToScan)
        {
            foreach (var ddfPath in Directory.EnumerateFiles(d, "*.DDF", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var ddf = DdfFile.Read(ddfPath);
                    ddf.Parse(sdbNames, clpHashes);
                    foreach (var (hash, role) in ddf.RoleByClpHash) combinedRoles.TryAdd(hash, role);
                    foreach (var (mesh, tex) in ddf.TextureForMesh) combinedPairs.TryAdd(mesh, tex);
                    WorldDdf ??= ddf;
                }
                catch { }
            }
        }

        focusClp.RoleResolver = h => combinedRoles.TryGetValue(h, out var r) ? RoleToExtension(r) : null;
        focusClp.TexturePairResolver = h => combinedPairs.TryGetValue(h, out var t) ? t : null;
    }

    /// <summary>
    /// For an opened .DDF: scan ONLY the file's own directory. We deliberately
    /// don't walk up to the root, so opening a level DDF (e.g.
    /// C3/GARDEN/GARDEN.DDF) doesn't pull in ALL.DDF + GLOBAL.CLP + every
    /// other shared archive. Hashes the level DDF references but that aren't
    /// in any local archive show up as "[not loaded]" leaves in the tree —
    /// the user can open the relevant global archive separately.
    /// </summary>
    private void LoadDdfWithSiblings()
    {
        if (EngineVersion != EngineVersion.BrotherhoodOfSteel || WorldDdf == null) return;

        // Load every CLP under the BoS data root (recursive). This makes the
        // ALL.DDF cat-8 entries resolve their level-world hashes — those live
        // in per-level archives like C1/BAR/BAR.CLP, C3/RINS_3/RINS_3.CLP,
        // etc. — and surfaces other cross-archive references too (e.g. PC/
        // animation archives that ALL.DDF entities point at). Top-level-only
        // loading was leaving cat-8 entities with empty asset lists and
        // skipping any reference outside the immediate directory.
        var rootDir = FindBosRoot(DataPath) ?? DataPath;
        var clpHashes = new HashSet<uint>();
        var loadedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(rootDir, "*.CLP", SearchOption.AllDirectories))
        {
            if (!loadedPaths.Add(Path.GetFullPath(path))) continue;
            try
            {
                if (new FileInfo(path).Length > 200_000_000) continue;
                var bytes = File.ReadAllBytes(path);
                var loaded = new ClpFile(EngineVersion, Path.GetFileName(path), bytes, 0, bytes.Length);
                loaded.ReadDirectory();
                foreach (var (label, _) in loaded.Directory)
                {
                    if (loaded.HashByLabel.TryGetValue(label, out var h))
                    {
                        clpHashes.Add(h);
                        AssetIndex.TryAdd(h, new ClpAssetRef(loaded, label));
                    }
                }
                LoadedClps.Add(loaded);
            }
            catch { }
        }

        // Same expanded scope for SDB names — entity names live in level-
        // specific SDBs that the top-level scan wouldn't pick up.
        var sdbDirs = new List<string> { rootDir };
        try
        {
            sdbDirs.AddRange(Directory.EnumerateDirectories(rootDir, "*", SearchOption.AllDirectories));
        }
        catch { }
        var sdbNames = LoadAllSdbNames(sdbDirs);
        if (sdbNames.Count == 0) return;

        WorldDdf.Parse(sdbNames, clpHashes);

        // Wire role + pair resolvers into the loaded CLPs so the model viewer
        // can pair a clicked mesh with its right texture and the entry list
        // gets correct extensions. ReadDirectory is re-run so the ext-from-
        // role override applies; AssetIndex is rebuilt against the new labels.
        var roleRes = (Func<uint, string?>)(h => WorldDdf.RoleByClpHash.TryGetValue(h, out var r) ? RoleToExtension(r) : null);
        var pairRes = (Func<uint, uint?>)(h => WorldDdf.TextureForMesh.TryGetValue(h, out var t) ? t : null);
        AssetIndex.Clear();
        foreach (var loaded in LoadedClps)
        {
            loaded.RoleResolver = roleRes;
            loaded.TexturePairResolver = pairRes;
            loaded.ReadDirectory();
            foreach (var (label, _) in loaded.Directory)
            {
                if (loaded.HashByLabel.TryGetValue(label, out var h))
                {
                    AssetIndex.TryAdd(h, new ClpAssetRef(loaded, label));
                }
            }
        }
    }

    /// <summary>
    /// Walk up at most 8 directories from <paramref name="startDir"/> looking
    /// for ALL.DDF (the marker that lives directly in BoS's /DATA/). Returns
    /// every visited directory; the last entry is the root if it was found.
    /// </summary>
    private static List<string> WalkUpToBosRoot(string startDir)
    {
        var dirs = new List<string>();
        var dir = startDir;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            dirs.Add(dir);
            if (File.Exists(Path.Combine(dir, "ALL.DDF"))) break;
            dir = Path.GetDirectoryName(dir);
        }
        return dirs;
    }

    /// <summary>Returns the BoS data root or null if no ancestor has ALL.DDF.</summary>
    private static string? FindBosRoot(string startDir)
    {
        var dirs = WalkUpToBosRoot(startDir);
        return dirs.Count > 0 && File.Exists(Path.Combine(dirs[^1], "ALL.DDF")) ? dirs[^1] : null;
    }

    /// <summary>
    /// Two-pass SDB load: first the non-GTEXT files (internal IDs from
    /// GLOBAL.SDB and per-level SDBs), then GTEXT.* (translated display
    /// names). 63 hashes intentionally appear in both namespaces — same
    /// level/character has both an internal ID like "LAB_1b" and a player-
    /// facing name like "Vault Lab 1, mutated". When both exist for the
    /// same hash we combine them as "INTERNAL — Display" so the explorer
    /// surfaces both. Internal-only or display-only entries pass through
    /// unmodified.
    /// </summary>
    private static Dictionary<uint, string> LoadAllSdbNames(IEnumerable<string> dirs)
    {
        var sdbList = new List<string>();
        foreach (var d in dirs)
        {
            foreach (var sdbPath in Directory.EnumerateFiles(d, "*.SDB", SearchOption.TopDirectoryOnly))
            {
                sdbList.Add(sdbPath);
            }
        }
        // Order: non-GTEXT first (internal IDs), then GTEXT (display names).
        sdbList.Sort((a, b) =>
        {
            var ag = Path.GetFileName(a).StartsWith("GTEXT", StringComparison.OrdinalIgnoreCase);
            var bg = Path.GetFileName(b).StartsWith("GTEXT", StringComparison.OrdinalIgnoreCase);
            return ag.CompareTo(bg);
        });

        var internalNames = new Dictionary<uint, string>();
        var displayNames = new Dictionary<uint, string>();
        foreach (var sdbPath in sdbList)
        {
            var isGtext = Path.GetFileName(sdbPath).StartsWith("GTEXT", StringComparison.OrdinalIgnoreCase);
            try
            {
                var sdb = SdbFile.Read(sdbPath);
                sdb.ReadDirectory();
                foreach (var rec in sdb.Records)
                {
                    if (rec.Text == null) continue;
                    var bucket = isGtext ? displayNames : internalNames;
                    bucket.TryAdd(rec.Hash, rec.Text);
                }
            }
            catch { }
        }

        // Merge: prefer internal as the primary name, append display in
        // parens when distinct. Display-only entries (no internal match)
        // come through with just the display name.
        var combined = new Dictionary<uint, string>();
        foreach (var (h, intern) in internalNames)
        {
            if (displayNames.TryGetValue(h, out var disp) &&
                !string.Equals(intern, disp, StringComparison.OrdinalIgnoreCase))
            {
                combined[h] = $"{intern} — {disp}";
            }
            else
            {
                combined[h] = intern;
            }
        }
        foreach (var (h, disp) in displayNames)
        {
            combined.TryAdd(h, disp);
        }
        return combined;
    }

    private static string? RoleToExtension(DdfFile.AssetRole role) => role switch
    {
        DdfFile.AssetRole.Mesh => ".vif",
        DdfFile.AssetRole.Texture => ".tex",
        DdfFile.AssetRole.Sound => ".vag",
        DdfFile.AssetRole.Animation => ".anm",
        _ => null,
    };

}

public sealed record ClpAssetRef(ClpFile Clp, string EntryLabel);
