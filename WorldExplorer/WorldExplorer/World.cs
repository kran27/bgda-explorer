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

    // The parsed data from the various files.
    public WorldData? WorldData = null;

    public GobFile? WorldGob;
    public LmpFile? WorldLmp;
    public WorldTexFile? WorldTex;
    public YakFile? WorldYak;
    public SdbFile? WorldSdb;
    public DdfFile? WorldDdf;

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
                if (File.Exists(textFilePath))
                {
                    WorldTex = new WorldTexFile(EngineVersion, textFilePath);
                }
                else
                {
                    WorldTex = null;
                }

                break;
            case ".lmp":
                // TODO: Support just passing the filepath instead of having to load data here
                var data = File.ReadAllBytes(Path.Combine(DataPath, Name));
                WorldLmp = new LmpFile(EngineVersion, Name, data, 0, data.Length);
                break;
            case ".clp":
                var clpData = File.ReadAllBytes(Path.Combine(DataPath, Name));
                var clp = new ClpFile(EngineVersion, Name, clpData, 0, clpData.Length);
                WorldLmp = clp;
                AttachBosLinkage(clp, clpData);
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
    /// For BoS .CLP archives, walk the file's directory and every ancestor up
    /// to the BoS DATA root, collecting all .SDB string databases and .DDF
    /// data-definition files we can find. Each level's pack contributes its
    /// own (hash → name) entries; level-specific archives like
    /// <c>C1/BAR/BAR.CLP</c> reference hashes that only resolve in
    /// <c>C1/BAR/BAR.SDB</c> + <c>BAR.DDF</c>, while shared assets in
    /// <c>ARMOR.CLP</c> resolve through the root <c>GLOBAL.SDB</c> +
    /// <c>ALL.DDF</c>. Combine all of them so the right names show up no
    /// matter where the user opened the file from.
    /// </summary>
    private void AttachBosLinkage(ClpFile clp, byte[] clpData)
    {
        if (EngineVersion != EngineVersion.BrotherhoodOfSteel)
        {
            return;
        }

        // Walk from the CLP's directory up to the filesystem root, recording
        // every directory along the way. Stop at the topmost directory that
        // contains the canonical BoS root markers (ALL.DDF + a *.SDB) so we
        // don't wander into unrelated parents.
        var directoriesToScan = new List<string>();
        var dir = DataPath;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            directoriesToScan.Add(dir);
            if (File.Exists(Path.Combine(dir, "ALL.DDF")))
            {
                break; // found the BoS root, no need to climb further
            }
            dir = Path.GetDirectoryName(dir);
        }

        // Collect the set of CLP entry hashes from every reachable archive in
        // the scanned directories. DdfFile.Parse uses this to distinguish
        // real asset references from incidental u32s in the DDF.
        var clpHashes = new HashSet<uint>();
        foreach (var d in directoriesToScan)
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(d, "*.CLP", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (new FileInfo(path).Length > 200_000_000) continue;
                    CollectHashes(File.ReadAllBytes(path), clpHashes);
                }
                catch { }
            }
        }
        CollectHashes(clpData, clpHashes);

        // Merge SDB names from every .SDB found across the scanned directories.
        var sdbNames = new Dictionary<uint, string>();
        foreach (var d in directoriesToScan)
        {
            foreach (var sdbPath in System.IO.Directory.EnumerateFiles(d, "*.SDB", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var sdb = SdbFile.Read(sdbPath);
                    sdb.ReadDirectory();
                    foreach (var rec in sdb.Records)
                    {
                        if (rec.Text != null && !sdbNames.ContainsKey(rec.Hash))
                        {
                            sdbNames[rec.Hash] = rec.Text;
                        }
                    }
                }
                catch { }
            }
        }
        if (sdbNames.Count == 0)
        {
            return;
        }

        // Parse every .DDF found, merging the (hash → entity), (hash → role),
        // and (mesh → texture pair) maps the CLP reader / UI need.
        var combinedNames = new Dictionary<uint, string>();
        var combinedRoles = new Dictionary<uint, DdfFile.AssetRole>();
        var combinedPairs = new Dictionary<uint, uint>();
        foreach (var d in directoriesToScan)
        {
            foreach (var ddfPath in System.IO.Directory.EnumerateFiles(d, "*.DDF", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var ddf = DdfFile.Read(ddfPath);
                    ddf.Parse(sdbNames, clpHashes);
                    foreach (var (hash, name) in ddf.NameByClpHash)
                    {
                        if (!combinedNames.ContainsKey(hash)) combinedNames[hash] = name;
                    }
                    foreach (var (hash, role) in ddf.RoleByClpHash)
                    {
                        if (!combinedRoles.ContainsKey(hash)) combinedRoles[hash] = role;
                    }
                    foreach (var (mesh, tex) in ddf.TextureForMesh)
                    {
                        if (!combinedPairs.ContainsKey(mesh)) combinedPairs[mesh] = tex;
                    }
                    // Hold onto the first parsed DDF so the WorldExplorer's SDB
                    // viewer can also display it; later DDFs are merged in too.
                    WorldDdf ??= ddf;
                }
                catch { }
            }
        }

        if (combinedNames.Count > 0)
        {
            clp.NameResolver = h => combinedNames.TryGetValue(h, out var name) ? name : null;
        }
        if (combinedRoles.Count > 0)
        {
            clp.RoleResolver = h => combinedRoles.TryGetValue(h, out var role) ? RoleToExtension(role) : null;
        }
        if (combinedPairs.Count > 0)
        {
            clp.TexturePairResolver = h => combinedPairs.TryGetValue(h, out var tex) ? tex : (uint?)null;
        }
    }

    private static string? RoleToExtension(DdfFile.AssetRole role) => role switch
    {
        DdfFile.AssetRole.Mesh => ".vif",
        DdfFile.AssetRole.Texture => ".tex",
        DdfFile.AssetRole.Sound => ".vag",
        _ => null, // let the sniffer decide
    };

    private static void CollectHashes(byte[] data, HashSet<uint> sink)
    {
        if (data.Length < 24) return;
        if (BitConverter.ToUInt32(data, 0) != ClpFile.Magic) return;
        var f8 = BitConverter.ToUInt32(data, 8);
        if (f8 == 0) return;
        // Pick the largest power-of-two multiplier that keeps the dir within the file.
        var sectorSize = 1L;
        while ((f8 << 1) * sectorSize < data.Length) sectorSize <<= 1;
        var dirOff = (int)(f8 * (uint)sectorSize);
        if (dirOff <= 0 || dirOff >= data.Length) return;
        for (var i = dirOff; i + 20 <= data.Length; i += 20)
        {
            var h = BitConverter.ToUInt32(data, i);
            if (h != 0) sink.Add(h);
        }
    }
}