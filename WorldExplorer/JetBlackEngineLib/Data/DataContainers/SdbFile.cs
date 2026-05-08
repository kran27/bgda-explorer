using System.IO;
using System.Text;

namespace JetBlackEngineLib.Data.DataContainers;

/// <summary>
/// Reader for the Fallout: Brotherhood of Steel ".SDB" string database.
/// GLOBAL.SDB holds item / effect identifiers; GTEXT.SDB holds player-visible
/// game text (dialogue, descriptions).
///
/// Layout (little-endian):
///   - Header (12 bytes):
///       0x00  u32  magic (0x000005DB in both shipped files)
///       0x04  u32  indexOffset (byte offset of the hash table)
///       0x08  u32  numRecords (count of valid entries in the hash table)
///   - String pool: UTF-16LE null-terminated strings packed from offset 0x0C
///     to indexOffset.
///   - Hash table: sparse array of 8-byte records at indexOffset, running to
///     EOF. Empty slots are 8 zero bytes. Each used record:
///       u32  storedHash  (upper 24 bits = the actual content hash;
///                         low 8 bits = the slot's index byte for verification
///                         after linear-probe insertion)
///       u32  stringOffset (absolute byte offset of UTF-16 string)
///
/// SDB hashes do not match .CLP entry hashes; this is a separate hash space
/// for in-game item / dialogue strings, not a CLP filename map.
/// </summary>
public class SdbFile
{
    public const uint Magic = 0x000005DB;

    public string Name { get; }
    public byte[] FileData { get; }
    public List<Record> Records { get; } = new();

    public SdbFile(string name, byte[] data)
    {
        Name = name;
        FileData = data;
    }

    public static SdbFile Read(string path)
    {
        return new SdbFile(Path.GetFileName(path), File.ReadAllBytes(path));
    }

    public void ReadDirectory()
    {
        Records.Clear();
        if (FileData.Length < 12)
        {
            return;
        }

        var magic = BitConverter.ToUInt32(FileData, 0);
        if (magic != Magic)
        {
            throw new InvalidDataException($"Not an SDB file (magic 0x{magic:X8})");
        }

        var indexOffset = (int)BitConverter.ToUInt32(FileData, 4);
        var declaredRecords = (int)BitConverter.ToUInt32(FileData, 8);

        if (indexOffset >= FileData.Length || indexOffset < 12)
        {
            throw new InvalidDataException(
                $"SDB index offset 0x{indexOffset:X} outside file (length 0x{FileData.Length:X})");
        }

        var totalSlots = (FileData.Length - indexOffset) / 8;
        for (var slot = 0; slot < totalSlots; slot++)
        {
            var recOff = indexOffset + slot * 8;
            var hash = BitConverter.ToUInt32(FileData, recOff);
            var strOff = (int)BitConverter.ToUInt32(FileData, recOff + 4);
            if (hash == 0 && strOff == 0)
            {
                continue;
            }
            var text = strOff > 0 && strOff < indexOffset - 1
                ? ReadUtf16Z(FileData, strOff, indexOffset - strOff)
                : null;
            Records.Add(new Record(slot, hash, strOff, text));
            if (Records.Count >= declaredRecords && declaredRecords > 0)
            {
                break;
            }
        }
    }

    private static string? ReadUtf16Z(byte[] data, int offset, int max)
    {
        var end = offset;
        var limit = Math.Min(offset + max, data.Length - 1);
        while (end + 1 < limit && (data[end] != 0 || data[end + 1] != 0))
        {
            end += 2;
        }
        if (end == offset) return string.Empty;
        try
        {
            return Encoding.Unicode.GetString(data, offset, end - offset);
        }
        catch
        {
            return null;
        }
    }

    public sealed record Record(int Slot, uint Hash, int StringOffset, string? Text);
}
