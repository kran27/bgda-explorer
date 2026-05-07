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
        CollectHashesFromBytes(focusClp.FileData, clpHashes);
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
                    CollectHashesFromBytes(File.ReadAllBytes(path), clpHashes);
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

        var clpHashes = new HashSet<uint>();
        foreach (var path in Directory.EnumerateFiles(DataPath, "*.CLP", SearchOption.TopDirectoryOnly))
        {
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

        var sdbNames = LoadAllSdbNames(new[] { DataPath });
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

    private static Dictionary<uint, string> LoadAllSdbNames(IEnumerable<string> dirs)
    {
        var sdbNames = new Dictionary<uint, string>();
        foreach (var d in dirs)
        {
            foreach (var sdbPath in Directory.EnumerateFiles(d, "*.SDB", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var sdb = SdbFile.Read(sdbPath);
                    sdb.ReadDirectory();
                    foreach (var rec in sdb.Records)
                    {
                        if (rec.Text != null) sdbNames.TryAdd(rec.Hash, rec.Text);
                    }
                }
                catch { }
            }
        }
        return sdbNames;
    }

    private static string? RoleToExtension(DdfFile.AssetRole role) => role switch
    {
        DdfFile.AssetRole.Mesh => ".vif",
        DdfFile.AssetRole.Texture => ".tex",
        DdfFile.AssetRole.Sound => ".vag",
        _ => null,
    };

    private static void CollectHashesFromBytes(byte[] data, HashSet<uint> sink)
    {
        if (data.Length < 24) return;
        if (BitConverter.ToUInt32(data, 0) != ClpFile.Magic) return;
        var f8 = BitConverter.ToUInt32(data, 8);
        if (f8 == 0) return;
        var sectorSize = 1L;
        while ((f8 << 1) * sectorSize < data.Length) sectorSize <<= 1;
        var dirOff = (int)(f8 * sectorSize);
        if (dirOff <= 0 || dirOff >= data.Length) return;
        for (var i = dirOff; i + 20 <= data.Length; i += 20)
        {
            var h = BitConverter.ToUInt32(data, i);
            if (h != 0) sink.Add(h);
        }
    }
}

public sealed record ClpAssetRef(ClpFile Clp, string EntryLabel);
