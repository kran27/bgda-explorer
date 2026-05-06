using System.IO;

namespace JetBlackEngineLib.Data.DataContainers;

/// <summary>
/// Reader for the Fallout: Brotherhood of Steel ".CLP" (CLumP) container format.
///
/// All little-endian. Layout:
///
///   Header (24 bytes):
///     0x00  u32  magic = 'CLMP'  (stored as ASCII "PMLC" on disk)
///     0x04  u32  reserved (0)
///     0x08  u32  packedDirOffset
///     0x0C  u32  hash / build checksum (unused here)
///     0x10  u32  numValidEntries
///     0x14  u32  reserved (0)
///
///   Directory: at <c>packedDirOffset * sectorSize</c>, runs to EOF.
///   Each slot is 20 bytes:
///     +0x00  u32  hash
///     +0x04  u32  dataOffsetSectors    (entry data lives at this * sectorSize)
///     +0x08  u32  zero
///     +0x0C  u32  size (bytes)
///     +0x10  u32  zero
///   Slots with hash == 0 are empty.
///
///   <c>sectorSize</c> is the largest power of two such that
///   <c>packedDirOffset * sectorSize &lt; fileLength</c>. In shipped data this is
///   either 0x100 (character / inventory archives) or 0x1000 (HUD / sound /
///   armor archives).
///
/// Each entry holds exactly one asset (a TEX, a BGDA-1 mesh, a VAG audio file,
/// a 32-byte name table, etc.). The data is *scattered* through the data area
/// — not laid out in directory order — and the per-entry offset comes from
/// the <c>dataOffsetSectors</c> field. There is no per-entry filename anywhere
/// in the archive; we resolve names externally via DDF + SDB lookup
/// (<see cref="NameResolver"/>).
/// </summary>
public class ClpFile : LmpFile
{
    /// <summary>'CLMP' read as a little-endian uint32 from the on-disk byte sequence "PMLC".</summary>
    public const uint Magic = 0x434C4D50;

    /// <summary>
    /// Optional resolver: given a CLP entry hash, return the entity name that
    /// owns it (typically discovered via the sibling DDF). When set, directory
    /// labels use the entity name; otherwise they fall back to the raw hex hash.
    /// </summary>
    public Func<uint, string?>? NameResolver { get; set; }

    /// <summary>
    /// Optional resolver: given a CLP entry hash, return the file extension the
    /// DDF asset offset implies (".vif", ".tex", ".vag", or null when DDF
    /// doesn't say). DDF position is a more authoritative type signal than
    /// content sniffing — for example, BoS's inventory mesh format isn't
    /// recognized by our content sniffer but DDF still tells us those slots
    /// are meshes. When set, this overrides the sniff result.
    /// </summary>
    public Func<uint, string?>? RoleResolver { get; set; }

    /// <summary>
    /// Lookup from a directory entry's filename label to the underlying CLP
    /// hash. Used by callers that need to consult the DDF mesh↔texture pairing
    /// for a clicked entry.
    /// </summary>
    public IReadOnlyDictionary<string, uint> HashByLabel => _hashByLabel;

    /// <summary>
    /// Optional resolver: given a mesh's CLP hash, return the texture's CLP
    /// hash that DDF says pairs with it (from the same DDF asset record).
    /// When several meshes/textures share an entity name, this picks the
    /// correct partner instead of falling back to "any texture with the same
    /// entity name token".
    /// </summary>
    public Func<uint, uint?>? TexturePairResolver { get; set; }

    private readonly Dictionary<string, uint> _hashByLabel = new();

    public ClpFile(EngineVersion engineVersion, string name, byte[] data, int startOffset, int dataLen)
        : base(engineVersion, name, data, startOffset, dataLen)
    {
    }

    public override void ReadDirectory()
    {
        Directory.Clear();
        _hashByLabel.Clear();

        if (_dataLen < 24) return;

        var magic = BitConverter.ToUInt32(FileData, _startOffset);
        if (magic != Magic)
        {
            throw new InvalidDataException($"Not a CLP archive (magic 0x{magic:X8})");
        }

        var packedDirOffset = BitConverter.ToUInt32(FileData, _startOffset + 8);
        var numValid = BitConverter.ToUInt32(FileData, _startOffset + 0x10);
        if (packedDirOffset == 0) return;

        // sectorSize = largest power-of-two such that packedDirOffset * 2^k < dataLen.
        var sectorSize = 1;
        while (((long)packedDirOffset << 1) * sectorSize < _dataLen)
        {
            sectorSize <<= 1;
        }

        var dirOffset = (int)(packedDirOffset * (uint)sectorSize);
        if (dirOffset >= _dataLen)
        {
            throw new InvalidDataException(
                $"CLP directory offset 0x{dirOffset:X} outside file (length 0x{_dataLen:X})");
        }

        var totalSlots = (_dataLen - dirOffset) / 20;
        var foundValid = 0;

        for (var slot = 0; slot < totalSlots; ++slot)
        {
            var entryOffset = _startOffset + dirOffset + slot * 20;
            var hash = BitConverter.ToUInt32(FileData, entryOffset);
            if (hash == 0) continue;

            var dataOffsetSectors = BitConverter.ToUInt32(FileData, entryOffset + 4);
            var size = (int)BitConverter.ToUInt32(FileData, entryOffset + 12);

            var fileStart = _startOffset + (int)(dataOffsetSectors * (uint)sectorSize);
            if (dataOffsetSectors > 0 && size > 0 &&
                fileStart >= _startOffset + sectorSize &&
                fileStart + size <= _startOffset + _dataLen)
            {
                var (sniffedExt, embeddedName) = Sniff(FileData, fileStart, size);
                // Prefer the DDF-derived role over content sniffing — DDF tells
                // us the type even when the bytes are in a format the sniffer
                // can't decode (e.g. BoS's inventory mesh format).
                var ext = RoleResolver?.Invoke(hash) ?? sniffedExt;
                AddEntry(slot, hash, fileStart, size, ext, embeddedName);
            }

            foundValid++;
            if (foundValid >= numValid) break;
        }
    }

    private void AddEntry(int slot, uint hash, int start, int length, string ext, string? embeddedName)
    {
        // Label preference: caller-provided entity name (DDF) > embedded name
        // (e.g. VAG header) > raw hash.
        var resolved = NameResolver?.Invoke(hash);
        var idPart = !string.IsNullOrEmpty(resolved)
            ? Sanitize(resolved!)
            : embeddedName ?? $"{hash:X8}";

        var label = $"slot{slot:D3}_{idPart}{ext}";
        if (Directory.ContainsKey(label))
        {
            // Two entries pointing at the same DDF entity (e.g. multiple
            // texture variants) — disambiguate with the raw hash.
            label = $"slot{slot:D3}_{idPart}_{hash:X8}{ext}";
        }
        Directory[label] = new EntryInfo(label, start, length);
        _hashByLabel[label] = hash;
    }

    private static string Sanitize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or ' ' ? '_' : c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Identify the format of the entry's bytes and (where the format embeds
    /// one) recover an internal name to use in the label.
    /// </summary>
    private static (string Extension, string? EmbeddedName) Sniff(byte[] data, int offset, int length)
    {
        if (length < 32) return (".dat", null);

        // VAG (PS2 ADPCM audio): "VAGp" magic, 16-byte name embedded at +0x20.
        if (data[offset] == 'V' && data[offset + 1] == 'A' && data[offset + 2] == 'G' &&
            data[offset + 3] == 'p' && length >= 0x40)
        {
            return (".vag", ReadFixedString(data, offset + 0x20, 16));
        }

        // TEX (PS2 GIF-tagged texture). Header layout:
        //   +0x00  u16 width   +0x02  u16 height   +0x06  u16 length-in-units-of-16
        //   +0x10  u32 gifOffset (always 0x80 in BoS)
        //   +0x0C  byte engine marker (0xC2 or 0xC4 in BoS)
        // The marker check is what distinguishes a real TEX from arbitrary
        // bytes that happen to have plausible width/height values at +0..+3.
        if (LooksLikeTex(data, offset, length))
        {
            return (".tex", null);
        }

        // BGDA-1 style mesh: no magic, byte at +0x12 holds the mesh count and
        // an interleaved (vertStart, vertEnd) offset table starts at +0x28.
        if (LooksLikeBgda1Mesh(data, offset, length))
        {
            return (".vif", null);
        }

        // 32-byte fixed-width name table — used by BoS for sound-event manifests
        // (lists of vag/anm filenames the engine binds together).
        if (length >= 64 && LooksLikeNameTable(data, offset, length))
        {
            return (".names", null);
        }

        // Header-less PS2 ADPCM (used in VA1.CLP voice clips). Body is a
        // multiple of 16 bytes; each frame starts with a predictor/shift byte
        // and a small flag byte. Most frames have flag == 0.
        if (length >= 64 && length % 16 == 0 && LooksLikeAdpcm(data, offset, length))
        {
            return (".adpcm", null);
        }

        return (".dat", null);
    }

    private static bool LooksLikeTex(byte[] data, int offset, int length)
    {
        if (length < 0x80) return false;
        var w = BitConverter.ToUInt16(data, offset);
        var h = BitConverter.ToUInt16(data, offset + 2);
        var gif = BitConverter.ToUInt32(data, offset + 16);
        var marker = data[offset + 12];
        return w >= 4 && w <= 4096 && h >= 4 && h <= 4096 &&
               gif == 0x80 && (marker == 0xC2 || marker == 0xC4) &&
               data[offset + 15] == 0;
    }

    private static bool LooksLikeBgda1Mesh(byte[] data, int offset, int length)
    {
        if (length < 0x68) return false;
        var numMeshes = data[offset + 0x12];
        if (numMeshes < 1 || numMeshes > 32) return false;
        if (0x28 + numMeshes * 8 > length) return false;
        var prevEnd = 0x68;
        for (var i = 0; i < numMeshes; i++)
        {
            var vertStart = BitConverter.ToInt32(data, offset + 0x28 + i * 8);
            var vertEnd = BitConverter.ToInt32(data, offset + 0x28 + i * 8 + 4);
            if (vertStart < 0x68 || vertEnd > length || vertStart >= vertEnd) return false;
            if (i > 0 && vertStart < prevEnd) return false;
            prevEnd = vertEnd;
        }
        return true;
    }

    private static bool LooksLikeAdpcm(byte[] data, int offset, int length)
    {
        // Every PS2 ADPCM frame is 16 bytes: byte 0 holds predictor (high
        // nibble, 0..4) and shift (low nibble, 0..12); byte 1 holds a small
        // flag byte (0/1/2/3/4/6/7); bytes 2..15 are 4-bit nibble samples.
        // To distinguish audio from coincidentally-aligned vertex/index data,
        // require >= 75% of the inspected frames to have flag == 0 (which is
        // overwhelmingly the case for streaming voice bodies).
        var frames = Math.Min(32, length / 16);
        if (frames < 4) return false;

        var zeroFlagFrames = 0;
        for (var f = 0; f < frames; f++)
        {
            var head = data[offset + f * 16];
            var flags = data[offset + f * 16 + 1];
            var predictor = (head >> 4) & 0xF;
            var shift = head & 0xF;
            if (predictor > 4) return false;
            if (shift > 12) return false;
            if (flags > 7) return false;
            if (flags == 0) zeroFlagFrames++;
        }
        return zeroFlagFrames * 4 >= frames * 3;
    }

    private static bool LooksLikeNameTable(byte[] data, int offset, int length)
    {
        // First two 32-byte records both look like printable-string-then-NUL.
        if (!IsPrintableThenNul(data, offset, 32)) return false;
        if (length < 64) return true;
        return IsPrintableThenNul(data, offset + 32, 32);
    }

    private static bool IsPrintableThenNul(byte[] data, int offset, int recordLen)
    {
        var seenNul = false;
        var ascii = 0;
        for (var i = 0; i < recordLen && offset + i < data.Length; i++)
        {
            var b = data[offset + i];
            if (b == 0) { seenNul = true; continue; }
            if (seenNul) return false; // garbage after the terminator
            if (b < 0x20 || b > 0x7e) return false;
            ascii++;
        }
        return ascii >= 3;
    }

    private static string? ReadFixedString(byte[] data, int offset, int max)
    {
        var end = offset;
        var limit = Math.Min(offset + max, data.Length);
        while (end < limit && data[end] != 0) end++;
        if (end == offset) return null;
        for (var i = offset; i < end; i++)
        {
            var b = data[i];
            if (b < 0x20 || b > 0x7e) return null;
        }
        var s = System.Text.Encoding.ASCII.GetString(data, offset, end - offset);
        // Sanitize for use as a label component.
        var safe = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            safe.Append(c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
        }
        return safe.ToString();
    }
}
