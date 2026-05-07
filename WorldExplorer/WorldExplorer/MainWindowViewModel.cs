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
using JetBlackEngineLib.Data.Animation;
using JetBlackEngineLib.Data.CutScenes;
using JetBlackEngineLib.Data.DataContainers;
using JetBlackEngineLib.Data.Models;
using JetBlackEngineLib.Data.Scripting;
using JetBlackEngineLib.Data.Textures;
using JetBlackEngineLib.Data.World;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using WorldExplorer.Logging;
using WorldExplorer.TreeView;

namespace WorldExplorer;

public class MainWindowViewModel : INotifyPropertyChanged
{
    private string _dataPath;
    private string? _gobFile;

    private LevelViewModel _levelViewModel;

    private string? _logText;

    private ModelViewModel _modelViewModel;

    private object? _selectedNode;

    private WriteableBitmap? _selectedNodeImage;

    private SkeletonViewModel _skeletonViewModel;

    private WorldTreeViewModel? _worldTreeViewModel;

    // This is what the tree view binds to.
    public ReadOnlyCollection<WorldTreeViewModel> Children =>
        new ReadOnlyCollection<WorldTreeViewModel>(_worldTreeViewModel != null
            ? new[] {_worldTreeViewModel}
            : Array.Empty<WorldTreeViewModel>());

    public WriteableBitmap? SelectedNodeImage
    {
        get => _selectedNodeImage;
        set
        {
            _selectedNodeImage = value;
            OnPropertyChanged(nameof(SelectedNodeImage));
        }
    }

    public SkeletonViewModel TheSkeletonViewModel
    {
        get => _skeletonViewModel;
        set
        {
            _skeletonViewModel = value;
            OnPropertyChanged(nameof(TheSkeletonViewModel));
        }
    }

    public ModelViewModel TheModelViewModel
    {
        get => _modelViewModel;
        set
        {
            _modelViewModel = value;
            OnPropertyChanged(nameof(TheModelViewModel));
        }
    }

    public LevelViewModel TheLevelViewModel
    {
        get => _levelViewModel;
        set
        {
            _levelViewModel = value;
            OnPropertyChanged(nameof(TheLevelViewModel));
        }
    }

    public object? SelectedNode
    {
        get => _selectedNode;
        set
        {
            // Clear log text
            LogText = null;

            _selectedNode = value;
            if (_selectedNode is LmpEntryTreeViewModel)
            {
                OnLmpEntrySelected((LmpEntryTreeViewModel)_selectedNode);
            }
            else if (_selectedNode is WorldFileTreeViewModel)
            {
                OnWorldEntrySelected((WorldFileTreeViewModel)_selectedNode);
            }
            else if (_selectedNode is WorldElementTreeViewModel)
            {
                OnWorldElementSelected((WorldElementTreeViewModel)_selectedNode);
            }
            else if (_selectedNode is YakChildTreeViewItem)
            {
                OnYakChildElementSelected((YakChildTreeViewItem)_selectedNode);
            }
            else if (_selectedNode is HdrDatChildTreeViewItem)
            {
                OnHdrDatChildElementSelected((HdrDatChildTreeViewItem)_selectedNode);
            }
            else if (_selectedNode is SdbTreeViewModel sdbNode)
            {
                OnSdbSelected(sdbNode);
            }
            else if (_selectedNode is DdfEntityTreeViewModel entityNode)
            {
                OnDdfEntitySelected(entityNode);
            }

            OnPropertyChanged(nameof(SelectedNode));
        }
    }

    public World? World { get; private set; }

    public MainWindow MainWindow { get; }

    public string? LogText
    {
        get => _logText;
        set
        {
            _logText = value;
            OnPropertyChanged(nameof(LogText));
        }
    }

    public MainWindowViewModel(MainWindow window, string dataPath)
    {
        MainWindow = window;
        _dataPath = dataPath;

        // Create View Models
        _modelViewModel = new ModelViewModel(this);
        _skeletonViewModel = new SkeletonViewModel(this);
        _levelViewModel = new LevelViewModel(this);
    }

    public void LoadFile(string file)
    {
        // Clear log text
        LogText = null;

        var folderPath = Path.GetDirectoryName(file) ?? Environment.CurrentDirectory;
        var engineVersion = App.Settings.Get<EngineVersion>("Core.EngineVersion");
        _gobFile = file;

        World = new World(engineVersion, folderPath, Path.GetFileName(_gobFile));
        _worldTreeViewModel = new WorldTreeViewModel(World);
        OnPropertyChanged("Children");
    }

    public void SettingsChanged()
    {
        _dataPath = App.Settings.Get("Files.DataPath", "") ?? "";

        if (_gobFile != null)
            // Reload file with new settings
        {
            LoadFile(_gobFile);
        }
    }

    /// <summary>
    /// Selecting a DDF entity:
    ///   - Renders all of its Mesh assets in the model viewport (each mesh
    ///     keeps its DDF-paired texture). Multi-mesh entities — most commonly
    ///     cat-0 characters with separate body and hair — show every part
    ///     instead of the first one only.
    ///   - Writes a structured record dump to the Log tab so parameter-only
    ///     categories (cat 5 emitters, cat 9 AI, cat 10 lights, cat 11 beam
    ///     effects) and the per-entity floats on asset-bearing categories are
    ///     legible. The log is populated regardless of whether the viewport
    ///     has anything to render.
    ///   - Clears the viewport when nothing renderable is available, so the
    ///     previous entity's model doesn't linger.
    /// </summary>
    private void OnDdfEntitySelected(DdfEntityTreeViewModel entityNode)
    {
        if (World == null) return;
        var entity = entityNode.Entity;

        // Cat-8 (level/container) entities point at a per-level world-layout
        // file via their Other-role asset (type-5 slot). When we can find
        // those bytes in a loaded CLP, decode them with the BGDA1 world
        // decoder (BoS reuses that layout) and route to the Level tab.
        if (entity.CategoryCode == 8)
        {
            if (TryRenderCat8World(entity))
            {
                LogText = DumpEntityRecord(entity);
                return;
            }
        }

        // Collect every Mesh asset that resolves to a loaded CLP entry.
        // Cat 12 (debris) records list 4 interchangeable mesh variants; render
        // only the first to keep the view legible — the parser already pairs
        // every cat-12 mesh to the same texture, so picking any one looks the
        // same as picking the canonical first variant.
        var meshParts = new List<(JetBlackEngineLib.Data.Models.Model vif, BitmapSource? texture)>();
        var renderedLabels = new List<string>();
        var firstMeshOnly = entity.CategoryCode == 12;
        foreach (var asset in entity.Assets)
        {
            if (asset.Role != DdfFile.AssetRole.Mesh) continue;
            if (!World.AssetIndex.TryGetValue(asset.Hash, out var loc)) continue;
            var clp = loc.Clp;
            if (!clp.Directory.TryGetValue(loc.EntryLabel, out var meshEntry)) continue;

            BitmapSource? texture = null;
            var pairedTex = FindSiblingTex(clp, loc.EntryLabel);
            if (pairedTex != null)
                texture = TexDecoder.Decode(clp.FileData.AsSpan().Slice(pairedTex.StartOffset, pairedTex.Length));

            var uvW = texture?.PixelWidth ?? 256;
            var uvH = texture?.PixelHeight ?? 256;
            var vif = new JetBlackEngineLib.Data.Models.Model(VifDecoder.Decode(
                new StringLogger(),
                clp.FileData.AsSpan().Slice(meshEntry.StartOffset, meshEntry.Length),
                uvW, uvH));

            meshParts.Add((vif, texture));
            renderedLabels.Add(loc.EntryLabel);
            if (firstMeshOnly) break;
        }

        if (meshParts.Count > 0)
        {
            // Show the first part's texture in the texture tab as a hint.
            SelectedNodeImage = meshParts[0].texture as System.Windows.Media.Imaging.WriteableBitmap;
            _modelViewModel.SetCompositeModel(meshParts);
            MainWindow.SetViewportText(1, entity.Name,
                meshParts.Count > 1 ? $"{meshParts.Count} meshes: {string.Join(", ", renderedLabels)}" : "");
            MainWindow.tabControl.SelectedIndex = 1; // Model View
            MainWindow.ResetCamera();
        }
        else
        {
            SelectedNodeImage = null;
            _modelViewModel.Texture = null;
            _modelViewModel.AnimData = null;
            _modelViewModel.VifModel = null;
            MainWindow.SetViewportText(1, entity.Name + " (no model)", "");
        }

        LogText = DumpEntityRecord(entity);
    }

    /// <summary>
    /// Look up the BoS level texture atlas. Tries two strategies:
    ///   1. Hash "&lt;entity_name&gt;.tex" directly — covers BAR, T_GAZ, GARDEN,
    ///      etc. where the CLP and asset names match.
    ///   2. Fallback: locate the sibling "&lt;entity&gt;_T.CLP" archive and pick
    ///      its largest entry — covers cases where the CLP name is a short
    ///      code but the asset is the full word (WARE_1 → warehouse_1.tex,
    ///      TUTOR → tutorial.tex). The .tex is always the biggest of the 2–3
    ///      entries in a _T.CLP (they hold .tex / .hsh / .vat where .hsh is
    ///      always tiny and .vat is medium).
    /// </summary>
    private WorldTexFile? LoadBosLevelTex(string entityName)
    {
        if (World == null) return null;
        var lowerName = entityName.Trim().ToLowerInvariant();

        // Strategy 1: direct hash on "<lower>.tex"
        var hash = BosNameTable.Hash(lowerName + ".tex");
        if (World.AssetIndex.TryGetValue(hash, out var loc)
            && loc.Clp.Directory.TryGetValue(loc.EntryLabel, out var entry))
        {
            var bytes = new byte[entry.Length];
            Buffer.BlockCopy(loc.Clp.FileData, entry.StartOffset, bytes, 0, entry.Length);
            return new WorldTexFile(World.EngineVersion, bytes, lowerName + ".tex");
        }

        // Strategy 2: find the sibling _T.CLP and pick its biggest entry
        var siblingName = entityName.Trim().ToUpperInvariant() + "_T.CLP";
        foreach (var clp in World.LoadedClps)
        {
            if (!string.Equals(clp.Name, siblingName, StringComparison.OrdinalIgnoreCase)) continue;
            // Pick the largest entry — that's always the .tex
            string? bestKey = null;
            int bestSize = 0;
            foreach (var (key, info) in clp.Directory)
            {
                if (info.Length > bestSize) { bestSize = info.Length; bestKey = key; }
            }
            if (bestKey != null)
            {
                var info = clp.Directory[bestKey];
                var bytes = new byte[info.Length];
                Buffer.BlockCopy(clp.FileData, info.StartOffset, bytes, 0, info.Length);
                return new WorldTexFile(World.EngineVersion, bytes, bestKey);
            }
        }
        return null;
    }

    /// <summary>
    /// Decode a cat-8 entity's type-5 (Other-role) asset as a BoS world file
    /// and surface it in the Level tab. Returns false if the entity has no
    /// usable Other reference, or its bytes don't parse — in which case the
    /// caller falls through to the regular mesh/log path.
    /// </summary>
    private bool TryRenderCat8World(DdfFile.EntityRecord entity)
    {
        if (World == null) return false;
        // The world-layout slot is the cat-8 type-5 reference. We attributed
        // it as AssetRole.Other in the DDF parser. There can be multiple
        // Other-role assets (types 5/6/7), so try each in turn.
        foreach (var asset in entity.Assets)
        {
            if (asset.Role != DdfFile.AssetRole.Other) continue;
            if (!World.AssetIndex.TryGetValue(asset.Hash, out var loc)) continue;
            if (!loc.Clp.Directory.TryGetValue(loc.EntryLabel, out var entry)) continue;
            try
            {
                var data = loc.Clp.FileData.AsSpan(entry.StartOffset, entry.Length);
                // Quick header sanity-check before paying the parse cost: a
                // real WorldFileHeader has a small NumberOfElements at +0
                // and an ElementArrayStart at +0x24 inside the file.
                if (data.Length < 0x68) continue;
                var ne = BitConverter.ToInt32(data.Slice(0, 4));
                var eas = BitConverter.ToInt32(data.Slice(0x24, 4));
                var wtoo = BitConverter.ToInt32(data.Slice(0x64, 4));
                if (ne <= 0 || ne > 8000 || eas < 100 || eas >= data.Length) continue;
                if (wtoo < 100 || wtoo >= data.Length) continue;

                WorldFileDecoder decoder = World.EngineVersion == EngineVersion.BrotherhoodOfSteel
                    ? new WorldFileV1BoSDecoder()
                    : new WorldFileV1Decoder();
                // Derive the level texture atlas. Cat-8 entity names are
                // upper-case level IDs ("BAR", "MILL_3"); the matching tex
                // entry is "<lower>.tex" and lives in the level's _T.CLP.
                var texFile = LoadBosLevelTex(entity.Name) ?? World.WorldTex;
                var worldData = decoder.Decode(data, texFile);
                World.WorldData = worldData;
                _levelViewModel.WorldNode = null;
                _levelViewModel.WorldData = worldData;

                MainWindow.tabControl.SelectedIndex = 3; // Level View
                MainWindow.SetViewportText(3, entity.Name, $"{worldData.WorldElements.Count} elements");
                MainWindow.ResetCamera();

                // Clear the model viewport so a previously-selected character
                // mesh doesn't overlap visually.
                _modelViewModel.VifModel = null;
                _modelViewModel.Texture = null;
                _modelViewModel.AnimData = null;
                SelectedNodeImage = null;
                return true;
            }
            catch (Exception ex)
            {
                LogText = $"Cat-8 world decode failed for asset 0x{asset.Hash:X8}: {ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Build a human-readable text dump of a DDF entity record: header info,
    /// asset list with names where known, and the populated float / int slots
    /// in the record body. Used as the Log-tab preview when an entity is
    /// selected so parameter-only records (emitters, AI, lights, beam effects)
    /// are legible and asset-bearing entities also expose their gameplay
    /// stats.
    /// </summary>
    private string DumpEntityRecord(DdfFile.EntityRecord entity)
    {
        var sb = new StringBuilder();
        sb.AppendFormat("Entity: {0}\n", entity.Name);
        sb.AppendFormat("  SDB hash:      0x{0:X8}\n", entity.SdbHash);
        sb.AppendFormat("  Category:      {0}\n", entity.CategoryCode);
        sb.AppendFormat("  Record offset: 0x{0:X}\n", entity.RecordOffset);

        if (entity.Assets.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Assets:");
            foreach (var a in entity.Assets)
            {
                var name = BosNameTable.Get(a.Hash);
                var nameStr = name != null ? $"  '{name}'" : "";
                var pair = a.PairedHash.HasValue ? $"  paired→0x{a.PairedHash.Value:X8}" : "";
                sb.AppendFormat("  {0,-9} 0x{1:X8}{2}{3}\n", a.Role, a.Hash, nameStr, pair);
            }
        }

        var ddf = World?.WorldDdf;
        if (ddf != null && entity.RecordOffset + 0x14 <= ddf.FileData.Length)
        {
            // Get total size from +0x10
            var size = (int)BitConverter.ToUInt32(ddf.FileData, entity.RecordOffset + 0x10);
            // Clamp to the file
            size = Math.Min(size, ddf.FileData.Length - entity.RecordOffset);
            if (size > 0x14)
            {
                sb.AppendLine();
                sb.AppendFormat("Record body ({0} bytes total, showing non-zero u32/float pairs from +0x14):\n", size);
                for (var off = 0x14; off + 4 <= size; off += 4)
                {
                    var u = BitConverter.ToUInt32(ddf.FileData, entity.RecordOffset + off);
                    if (u == 0) continue;
                    var f = BitConverter.ToSingle(ddf.FileData, entity.RecordOffset + off);
                    var i = (int)u;
                    // Float interpretation: only show if it's a "reasonable" magnitude
                    var floatStr = (Math.Abs(f) > 0.0001f && Math.Abs(f) < 1e10f) ? f.ToString("F4") : "—";
                    sb.AppendFormat("  +0x{0:X3}  u32=0x{1:X8} ({2,11})  float={3}\n", off, u, i, floatStr);
                }
            }
        }
        return sb.ToString();
    }

    private void OnLmpEntrySelected(LmpEntryTreeViewModel lmpEntry)
    {
        var lmpFile = lmpEntry.LmpFileProperty;
        var entry = lmpFile.Directory[lmpEntry.Label];

        var ext = (Path.GetExtension(lmpEntry.Label) ?? "").ToLower();

        try
        {
            DispatchLmpEntry(lmpFile, entry, lmpEntry, ext);
        }
        catch (Exception ex)
        {
            LogText = $"Failed to decode {lmpEntry.Label} as {ext}: {ex.GetType().Name}: {ex.Message}\n\n"
                      + HexDump(lmpFile.FileData, entry.StartOffset, Math.Min(entry.Length, 256));
            MainWindow.tabControl.SelectedIndex = 4; // Log View
        }
    }

    private static LmpFile.EntryInfo? FindSiblingTex(LmpFile lmp, string vifLabel)
    {
        // Strongest signal: ask the DDF for the specific texture hash that
        // shares this mesh's asset record. When an entity has several variants
        // (e.g. multiple Bottle Caps drops, each with its own mesh+texture),
        // each variant binds the right texture to the right mesh.
        if (lmp is ClpFile clp &&
            clp.TexturePairResolver != null &&
            clp.HashByLabel.TryGetValue(vifLabel, out var meshHash))
        {
            var paired = clp.TexturePairResolver(meshHash);
            if (paired.HasValue)
            {
                foreach (var (key, info) in lmp.Directory)
                {
                    if (!key.EndsWith(".tex")) continue;
                    if (clp.HashByLabel.TryGetValue(key, out var texHash) && texHash == paired.Value)
                    {
                        return info;
                    }
                }
            }
        }

        // Legacy BGDA naming convention — same basename, .tex extension.
        var legacy = Path.GetFileNameWithoutExtension(vifLabel) + ".tex";
        if (lmp.Directory.TryGetValue(legacy, out var byBasename)) return byBasename;

        // Last resort: any .tex with the same entity-name token.
        var vifEntity = ExtractEntityToken(vifLabel);
        if (vifEntity == null) return null;
        foreach (var (key, info) in lmp.Directory)
        {
            if (!key.EndsWith(".tex")) continue;
            if (ExtractEntityToken(key) == vifEntity) return info;
        }
        return null;
    }

    private static string? ExtractEntityToken(string label)
    {
        // Labels are "slotNNN_<entity>.<ext>" (the entity may contain '_').
        var dot = label.LastIndexOf('.');
        var trimmed = dot > 0 ? label.Substring(0, dot) : label;
        var firstUnderscore = trimmed.IndexOf('_');
        if (firstUnderscore < 0 || firstUnderscore + 1 >= trimmed.Length) return null;
        return trimmed.Substring(firstUnderscore + 1);
    }

    private void OnSdbSelected(SdbTreeViewModel node)
    {
        var sdb = node.SdbFile;
        var sb = new StringBuilder();
        sb.AppendFormat("{0}\nString database — {1} records.\n", sdb.Name, sdb.Records.Count);
        sb.AppendLine("Note: SDB hashes do NOT match .CLP entry hashes; this is a separate");
        sb.AppendLine("hash space for in-game item / dialogue strings, not a CLP filename map.");
        sb.AppendLine("The hash function used to derive the upper 24 bits of each record is");
        sb.AppendLine("not yet identified.");
        sb.AppendLine();
        sb.AppendLine("  slot   stored hash   string");
        sb.AppendLine("  -----  -----------   ------------------------------------------------");
        foreach (var rec in sdb.Records)
        {
            var preview = rec.Text ?? "<missing string>";
            if (preview.Length > 96) preview = preview.Substring(0, 93) + "...";
            // Strip newlines for table layout.
            preview = preview.Replace('\r', ' ').Replace('\n', ' ');
            sb.AppendFormat("  {0,5}  0x{1:X8}    {2}\n", rec.Slot, rec.Hash, preview);
        }
        LogText = sb.ToString();
        MainWindow.tabControl.SelectedIndex = 4; // Log View
    }

    private static string DescribeVag(string label, byte[] data, int offset, int length)
    {
        var sb = new StringBuilder();
        sb.AppendLine(label);
        if (length < 0x40)
        {
            sb.AppendLine("VAG file is too short to read header.");
            return sb.ToString();
        }
        // VAG header is big-endian.
        var version = (data[offset + 4] << 24) | (data[offset + 5] << 16) | (data[offset + 6] << 8) | data[offset + 7];
        var dataSize = (data[offset + 0xC] << 24) | (data[offset + 0xD] << 16) | (data[offset + 0xE] << 8) | data[offset + 0xF];
        var sampleRate = (data[offset + 0x10] << 24) | (data[offset + 0x11] << 16) | (data[offset + 0x12] << 8) | data[offset + 0x13];
        var name = new StringBuilder();
        for (var i = 0; i < 16 && data[offset + 0x20 + i] != 0; i++) name.Append((char)data[offset + 0x20 + i]);
        sb.AppendFormat("VAG (PS2 ADPCM audio)\n");
        sb.AppendFormat("  Internal name: {0}\n", name);
        sb.AppendFormat("  Version:       0x{0:X8}\n", version);
        sb.AppendFormat("  Sample rate:   {0} Hz\n", sampleRate);
        sb.AppendFormat("  Data size:     {0} bytes ({1:F1} KB)\n", dataSize, dataSize / 1024.0);
        sb.AppendFormat("  File size:     {0} bytes\n", length);
        return sb.ToString();
    }

    private static string DescribeNameTable(string label, byte[] data, int offset, int length)
    {
        var sb = new StringBuilder();
        sb.AppendLine(label);
        sb.AppendFormat("32-byte name table, {0} records\n\n", length / 32);
        for (var i = 0; i < length; i += 32)
        {
            var name = new StringBuilder();
            for (var j = 0; j < 32 && i + j < length; j++)
            {
                var b = data[offset + i + j];
                if (b == 0) break;
                name.Append(b >= 0x20 && b < 0x7f ? (char)b : '.');
            }
            if (name.Length == 0) continue;
            sb.AppendFormat("  {0:D4}: {1}\n", i / 32, name);
        }
        return sb.ToString();
    }

    private static string HexDump(byte[] data, int offset, int length)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < length; i += 16)
        {
            sb.AppendFormat("{0:X8}: ", offset + i);
            for (var j = 0; j < 16 && i + j < length; j++)
            {
                sb.AppendFormat("{0:X2} ", data[offset + i + j]);
            }
            sb.Append(' ');
            for (var j = 0; j < 16 && i + j < length; j++)
            {
                var b = data[offset + i + j];
                sb.Append(b >= 32 && b < 127 ? (char)b : '.');
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private void DispatchLmpEntry(LmpFile lmpFile, LmpFile.EntryInfo entry, LmpEntryTreeViewModel lmpEntry, string ext)
    {
        switch (ext)
        {
            case ".tex":
            {
                SelectedNodeImage =
                    TexDecoder.Decode(lmpFile.FileData.AsSpan().Slice(entry.StartOffset, entry.Length));

                MainWindow.tabControl.SelectedIndex = 0; // Texture View
            }
                break;
            case ".vif":
            {
                // Try multiple pairing strategies in order:
                //   1. BGDA naming convention: same basename, .tex extension
                //   2. BoS / DDF: a sibling entry whose name shares the slotNNN
                //      prefix (the ScanSubFiles output for a single CLP entry
                //      labels its parts slotNNN_p0, slotNNN_p1, …)
                //   3. Same-prefix entry (entity name) elsewhere in the archive
                LmpFile.EntryInfo? pairedTex = null;
                var texFilename = Path.GetFileNameWithoutExtension(lmpEntry.Label) + ".tex";
                if (lmpFile.Directory.TryGetValue(texFilename, out var ent))
                {
                    pairedTex = ent;
                }
                else
                {
                    pairedTex = FindSiblingTex(lmpFile, lmpEntry.Label);
                }
                SelectedNodeImage = pairedTex != null
                    ? TexDecoder.Decode(lmpFile.FileData.AsSpan().Slice(pairedTex.StartOffset, pairedTex.Length))
                    : null;
                StringLogger log = new();
                _modelViewModel.Texture = SelectedNodeImage;
                _modelViewModel.AnimData = null;
                // VifDecoder divides UV coords by (textureDim * 16). With dim 0 we
                // get NaN UVs and WPF silently drops the geometry. Substitute 256
                // so UVs are finite when there is no paired texture; the Model
                // viewer falls back to a checkerboard brush in that case.
                var uvW = SelectedNodeImage?.PixelWidth ?? 256;
                var uvH = SelectedNodeImage?.PixelHeight ?? 256;
                Model model = new(VifDecoder.Decode(
                    log,
                    lmpFile.FileData.AsSpan().Slice(entry.StartOffset, entry.Length),
                    uvW, uvH));
                _modelViewModel.VifModel = model;

                /*// Load animation data
                var animData = LoadFirstAnim(lmpFile);
                // Make sure the animation will work with the model
                if (animData.Count > 0 && animData[0].NumBones == model.CountBones())
                    _modelViewModel.AnimData = animData.Count == 0 ? null : animData.First();*/

                LogText += log.ToString();

                MainWindow.tabControl.SelectedIndex = 1; // Model View
                MainWindow.ResetCamera();
                MainWindow.SetViewportText(1, lmpEntry.Label, "");
            }
                break;
            case ".anm":
            {
                var engineVersion =
                    App.Settings.Get("Core.EngineVersion", EngineVersion.DarkAlliance);
                var animData = AnmDecoder.Decode(engineVersion,
                    lmpFile.FileData.AsSpan().Slice(entry.StartOffset, entry.Length));
                _skeletonViewModel.AnimData = animData;
                LogText = animData.ToString();

                if (_modelViewModel.VifModel != null)
                {
                    var boneCount = _modelViewModel.VifModel.CountBones();
                    // Ensure the animation has the same number of bones as the model
                    if (boneCount != 0 && boneCount == animData.NumBones)
                    {
                        _modelViewModel.AnimData = animData;

                        // Switch tab to animation tab only if the current tab isn't the model view tab
                        if (MainWindow.tabControl.SelectedIndex != 1) // Model View
                        {
                            MainWindow.tabControl.SelectedIndex = 2; // Skeleton View
                            MainWindow.ResetCamera();
                        }
                    }
                    else
                    {
                        // Bone count doesn't match, switch to skeleton view
                        MainWindow.tabControl.SelectedIndex = 2; // Skeleton View
                        MainWindow.ResetCamera();
                    }
                }
                else
                {
                    MainWindow.tabControl.SelectedIndex = 2; // Skeleton View
                    MainWindow.ResetCamera();
                }
            }

                MainWindow.SetViewportText(2, lmpEntry.Label, ""); // Set Skeleton View Text

                break;
            case ".ob":
            {
                var objects = ObDecoder.Decode(lmpFile.FileData, entry.StartOffset, entry.Length);

                StringBuilder sb = new();

                foreach (var obj in objects)
                {
                    sb.AppendFormat("Name: {0}\n", obj.Name);
                    sb.AppendFormat("I6: {0}\n", obj.I6.ToString("X4"));
                    sb.AppendFormat("Floats: {0},{1},{2}\n", obj.Floats[0], obj.Floats[1], obj.Floats[2]);
                    foreach (var prop in obj.Properties)
                    {
                        sb.AppendFormat("Property: {0}\n", prop);
                    }

                    sb.Append("\n");
                }

                LogText = sb.ToString();
            }
                MainWindow.tabControl.SelectedIndex = 4; // Log View

                break;
            case ".scr":
                var script = ScrDecoder.Decode(lmpFile.FileData, entry.StartOffset, entry.Length);
                LogText = script.Disassemble();
                MainWindow.tabControl.SelectedIndex = 4; // Log View

                break;
            case ".cut":
                var scene = CutDecoder.Decode(lmpFile.FileData, entry.StartOffset, entry.Length);
                LogText = scene.Disassemble();
                MainWindow.tabControl.SelectedIndex = 4; // Log View

                break;
            case ".bin":
            {
                var dialog =
                    DialogDecoder.Decode(lmpFile.FileData, entry.StartOffset, entry.Length);
                StringBuilder sb = new();

                foreach (var obj in dialog)
                {
                    sb.AppendFormat("Name: {0}\n", obj.Name);
                    sb.AppendFormat("Start offset in VA File: 0x{0:x}\n", obj.StartOffsetInVAFile);
                    sb.AppendFormat("Length: 0x{0:x}\n", obj.Length);
                    sb.Append("\n");
                }

                LogText = sb.ToString();
            }
                MainWindow.tabControl.SelectedIndex = 4; // Log View

                break;
            case ".vag":
                LogText = DescribeVag(lmpEntry.Label, lmpFile.FileData, entry.StartOffset, entry.Length);
                MainWindow.tabControl.SelectedIndex = 4; // Log View
                break;
            case ".names":
                LogText = DescribeNameTable(lmpEntry.Label, lmpFile.FileData, entry.StartOffset, entry.Length);
                MainWindow.tabControl.SelectedIndex = 4; // Log View
                break;
            case ".adpcm":
                LogText = $"{lmpEntry.Label}\nRaw PS2 ADPCM stream (header-less VAG body).\n  {entry.Length} bytes = {entry.Length / 16} frames = {entry.Length * 28 / 16} samples\n\n"
                          + HexDump(lmpFile.FileData, entry.StartOffset, Math.Min(entry.Length, 128));
                MainWindow.tabControl.SelectedIndex = 4; // Log View
                break;
            case ".world":
            {
                if (World == null) break;
                var data = lmpFile.FileData.AsSpan(entry.StartOffset, entry.Length);
                WorldFileDecoder decoder = World.EngineVersion == EngineVersion.BrotherhoodOfSteel
                    ? new WorldFileV1BoSDecoder()
                    : new WorldFileV1Decoder();
                // Derive the texture atlas from the parent CLP's name. The
                // user typically opens e.g. "BAR.CLP" — the matching atlas is
                // "bar.tex" living in BAR_T.CLP, hashable via BosNameTable.
                WorldTexFile? texFile = World.WorldTex;
                if (World.EngineVersion == EngineVersion.BrotherhoodOfSteel)
                {
                    var levelName = Path.GetFileNameWithoutExtension(lmpFile.Name);
                    texFile = LoadBosLevelTex(levelName) ?? texFile;
                }
                World.WorldData = decoder.Decode(data, texFile);
                _levelViewModel.WorldNode = null;
                _levelViewModel.WorldData = World.WorldData;
                LogText = World.WorldData.ToString();
                MainWindow.tabControl.SelectedIndex = 3; // Level View
                MainWindow.SetViewportText(3, lmpEntry.Label, $"{World.WorldData.WorldElements.Count} elements");
                MainWindow.ResetCamera();
                _modelViewModel.VifModel = null;
                _modelViewModel.Texture = null;
                _modelViewModel.AnimData = null;
                SelectedNodeImage = null;
                break;
            }
            default:
                LogText = $"{lmpEntry.Label}\nNo decoder for extension '{ext}'. First {Math.Min(entry.Length, 256)} bytes:\n\n"
                          + HexDump(lmpFile.FileData, entry.StartOffset, Math.Min(entry.Length, 256));
                MainWindow.tabControl.SelectedIndex = 4; // Log View
                break;
        }
    }

    private void OnWorldEntrySelected(WorldFileTreeViewModel worldFileModel)
    {
        var engineVersion = App.Settings.Get("Core.EngineVersion", EngineVersion.DarkAlliance);
        var lmpFile = worldFileModel.LmpFileProperty;
        var entry = lmpFile.Directory[worldFileModel.Label];
        WorldFileDecoder decoder =
            engineVersion == EngineVersion.ReturnToArms || engineVersion == EngineVersion.JusticeLeagueHeroes
                ? new WorldFileV2Decoder()
                : new WorldFileV1Decoder();
        StringLogger log = new();
            
        if (World == null) return;

        World.WorldData = decoder.Decode(lmpFile.FileData.AsSpan(entry.StartOffset, entry.Length), _worldTreeViewModel?.World.WorldTex);
        worldFileModel.ReloadChildren();
        _levelViewModel.WorldNode = worldFileModel;
        _levelViewModel.WorldData = World.WorldData;
        LogText = log.ToString();
        LogText += World.WorldData.ToString();

        MainWindow.tabControl.SelectedIndex = 3; // Level View
        MainWindow.ResetCamera();
        MainWindow.SetViewportText(3, worldFileModel.Label, ""); // Set Level View Text
    }

    private void OnWorldElementSelected(WorldElementTreeViewModel worldElementModel)
    {
        SelectedNodeImage = worldElementModel.WorldElement.Texture;
        _modelViewModel.Texture = SelectedNodeImage;
        _modelViewModel.AnimData = null;
        _modelViewModel.VifModel = worldElementModel.WorldElement.Model;

        MainWindow.tabControl.SelectedIndex = 1; // Model View
        MainWindow.ResetCamera();
        MainWindow.SetViewportText(1, worldElementModel.Label, ""); // Set Model View Text           
    }

    private void OnYakChildElementSelected(YakChildTreeViewItem childEntry)
    {
        if (childEntry.Value == null) return;
        SelectedNodeImage = TexDecoder.Decode(childEntry.YakFile.FileData.AsSpan()[(childEntry.Value.TextureOffset + childEntry.Value.VifOffset)..]);
        StringLogger log = new();
        _modelViewModel.Texture = SelectedNodeImage;
        _modelViewModel.AnimData = null;
        Model model = new(VifDecoder.Decode(
            log,
            childEntry.YakFile.FileData.AsSpan()
                .Slice(childEntry.Value.VifOffset, childEntry.Value.TextureOffset),
            SelectedNodeImage?.PixelWidth ?? 0,
            SelectedNodeImage?.PixelHeight ?? 0));
        _modelViewModel.VifModel = model;

        LogText += log.ToString();

        MainWindow.tabControl.SelectedIndex = 1; // Model View
        MainWindow.ResetCamera();
        MainWindow.SetViewportText(1, childEntry.Label + " of " + (childEntry.Parent as YakTreeViewItem)?.Label, "");
    }

    private void OnHdrDatChildElementSelected(HdrDatChildTreeViewItem childEntry)
    {
        SelectedNodeImage =
            TexDecoder.Decode(childEntry.CacheFile.FileData.AsSpan()[childEntry.Value.TexOffset..]);
        StringLogger log = new();
        _modelViewModel.Texture = SelectedNodeImage;
        _modelViewModel.AnimData = null;
        Model model = new(VifDecoder.Decode(
            log,
            childEntry.CacheFile.FileData.AsSpan()
                .Slice(childEntry.Value.VifOffset, childEntry.Value.VifLength),
            SelectedNodeImage?.PixelWidth ?? 0,
            SelectedNodeImage?.PixelHeight ?? 0));
        _modelViewModel.VifModel = model;

        LogText += log.ToString();

        MainWindow.tabControl.SelectedIndex = 1; // Model View
        MainWindow.ResetCamera();
        MainWindow.SetViewportText(1, childEntry.Label + " of " + (childEntry.Parent as HdrDatTreeViewItem)?.Label, "");
    }

    private List<AnimData> LoadFirstAnim(LmpFile lmpFile)
    {
        List<AnimData> animList = new();
        var animEntry = lmpFile.FindFirstEntryWithSuffix(".anm");
            
        // If we can't find an animation file, just return an empty animation list
        if (animEntry == null)
        {
            return animList;
        }

        var engineVersion = App.Settings.Get("Core.EngineVersion", EngineVersion.DarkAlliance);
        animList.Add(AnmDecoder.Decode(engineVersion,
            lmpFile.FileData.AsSpan().Slice(animEntry.StartOffset, animEntry.Length)));

        return animList;
    }

    #region INotifyPropertyChanged Members

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    #endregion // INotifyPropertyChanged Members
}