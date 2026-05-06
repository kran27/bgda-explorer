using JetBlackEngineLib;
using JetBlackEngineLib.Data.DataContainers;
using System;
using System.IO;

namespace JetBlackEngineLib.Tests;

[TestFixture]
public class ClpFileTests
{
    /// <summary>
    /// Build a synthetic CLP archive matching the layout reverse-engineered from
    /// Fallout: Brotherhood of Steel, then verify ClpFile reads it back correctly.
    /// </summary>
    private static byte[] BuildClp(int sectorSize, (uint hash, byte[] payload)[] entries)
    {
        // The on-disk format lets entry data live at any sector offset (the
        // directory's +0x04 dataOffsetSectors field points wherever it likes),
        // but the builder lays them out sequentially for test simplicity and
        // records each entry's actual offset in the directory.
        var totalDataSize = 0;
        foreach (var (_, p) in entries)
        {
            totalDataSize += (p.Length + sectorSize - 1) & ~(sectorSize - 1);
        }
        var dirOffset = sectorSize + totalDataSize;
        var dirEntries = entries.Length;
        var dirSize = (dirEntries * 20 + sectorSize - 1) & ~(sectorSize - 1);
        var fileSize = dirOffset + dirSize;

        // packedDirOffset such that packedDirOffset * sectorSize == dirOffset
        // and 2 * packedDirOffset * sectorSize >= fileSize so the algorithm picks this sector size.
        var packedDirOffset = (uint)(dirOffset / sectorSize);

        var data = new byte[fileSize];

        // Header
        BitConverter.GetBytes((uint)0x434C4D50).CopyTo(data, 0); // "CLMP"
        // reserved at +4
        BitConverter.GetBytes(packedDirOffset).CopyTo(data, 8);
        BitConverter.GetBytes((uint)0xDEADBEEF).CopyTo(data, 0xC); // hash placeholder
        BitConverter.GetBytes((uint)dirEntries).CopyTo(data, 0x10);
        // reserved at +0x14

        // File data — store entries sequentially (still valid since the +0x04
        // offset field can describe any layout). Record each entry's actual
        // sector offset so the directory can point back to it.
        var cursor = sectorSize;
        var entryDataSectors = new uint[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            entryDataSectors[i] = (uint)(cursor / sectorSize);
            entries[i].payload.CopyTo(data, cursor);
            cursor += entries[i].payload.Length;
            var pad = (-cursor) & (sectorSize - 1);
            cursor += pad;
        }

        // Directory: 20-byte entries, hash + dataOffsetSectors + 0 + size + 0
        for (var i = 0; i < entries.Length; i++)
        {
            var slotOff = dirOffset + i * 20;
            BitConverter.GetBytes(entries[i].hash).CopyTo(data, slotOff);
            BitConverter.GetBytes(entryDataSectors[i]).CopyTo(data, slotOff + 4);
            // pad zero at +0x08
            BitConverter.GetBytes((uint)entries[i].payload.Length).CopyTo(data, slotOff + 12);
            // pad zero at +0x10
        }

        return data;
    }

    [Test]
    public void Reads_small_archive_with_256_byte_sectors()
    {
        var entries = new (uint hash, byte[] payload)[]
        {
            (0x11111111u, new byte[] {1, 2, 3, 4}),
            (0x22222222u, new byte[300]), // forces multi-sector spillover
            (0x33333333u, new byte[] {5, 6, 7, 8, 9}),
        };
        for (var i = 0; i < entries[1].payload.Length; i++)
        {
            entries[1].payload[i] = (byte)(i & 0xff);
        }

        var data = BuildClp(0x100, entries);
        var clp = new ClpFile(EngineVersion.BrotherhoodOfSteel, "test.clp", data, 0, data.Length);
        clp.ReadDirectory();

        Assert.That(clp.Directory.Count, Is.EqualTo(3));

        var values = new System.Collections.Generic.List<LmpFile.EntryInfo>(clp.Directory.Values);
        Assert.That(values[0].Length, Is.EqualTo(4));
        Assert.That(values[1].Length, Is.EqualTo(300));
        Assert.That(values[2].Length, Is.EqualTo(5));

        Assert.That(data[values[0].StartOffset + 0], Is.EqualTo(1));
        Assert.That(data[values[0].StartOffset + 3], Is.EqualTo(4));
        Assert.That(data[values[2].StartOffset + 0], Is.EqualTo(5));
        Assert.That(data[values[2].StartOffset + 4], Is.EqualTo(9));
    }

    [Test]
    public void Reads_archive_with_4096_byte_sectors()
    {
        // Bigger archive forces sectorSize 0x1000 (used by HUD/ARMOR/SOUND in BoS).
        var entries = new (uint hash, byte[] payload)[]
        {
            (0xABCD0001u, new byte[5000]),
            (0xABCD0002u, new byte[8000]),
        };
        var data = BuildClp(0x1000, entries);

        var clp = new ClpFile(EngineVersion.BrotherhoodOfSteel, "test.clp", data, 0, data.Length);
        clp.ReadDirectory();

        Assert.That(clp.Directory.Count, Is.EqualTo(2));
        var values = new System.Collections.Generic.List<LmpFile.EntryInfo>(clp.Directory.Values);
        Assert.That(values[0].Length, Is.EqualTo(5000));
        Assert.That(values[1].Length, Is.EqualTo(8000));
        // First file aligned to sectorSize.
        Assert.That(values[0].StartOffset, Is.EqualTo(0x1000));
        // Second file packed after first, padded to sector boundary.
        Assert.That(values[1].StartOffset, Is.EqualTo(0x1000 + 0x2000));
    }

    [Test]
    public void Skips_empty_directory_slots()
    {
        // Build manually: 3 valid entries interleaved with empty slots
        var sectorSize = 0x100;
        var p1 = new byte[64];
        var p2 = new byte[128];
        for (var i = 0; i < p1.Length; i++) p1[i] = 0xAA;
        for (var i = 0; i < p2.Length; i++) p2[i] = 0xBB;

        var fileDataSize = sectorSize * 2; // each padded
        var dirOffset = sectorSize + fileDataSize;
        // 5 slots so: valid, empty, valid, empty, empty
        var slots = 5;
        var dirSize = (slots * 20 + sectorSize - 1) & ~(sectorSize - 1);
        var data = new byte[dirOffset + dirSize];

        BitConverter.GetBytes((uint)0x434C4D50).CopyTo(data, 0);
        BitConverter.GetBytes((uint)(dirOffset / sectorSize)).CopyTo(data, 8);
        BitConverter.GetBytes((uint)2).CopyTo(data, 0x10); // 2 valid entries

        Array.Copy(p1, 0, data, sectorSize, p1.Length);
        Array.Copy(p2, 0, data, sectorSize * 2, p2.Length);

        // slot 0 — data at sector 1 (= sectorSize)
        BitConverter.GetBytes((uint)0xAAAA0001).CopyTo(data, dirOffset + 0 * 20);
        BitConverter.GetBytes((uint)1).CopyTo(data, dirOffset + 0 * 20 + 4);
        BitConverter.GetBytes((uint)p1.Length).CopyTo(data, dirOffset + 0 * 20 + 12);
        // slot 1 empty
        // slot 2 — data at sector 2
        BitConverter.GetBytes((uint)0xBBBB0002).CopyTo(data, dirOffset + 2 * 20);
        BitConverter.GetBytes((uint)2).CopyTo(data, dirOffset + 2 * 20 + 4);
        BitConverter.GetBytes((uint)p2.Length).CopyTo(data, dirOffset + 2 * 20 + 12);

        var clp = new ClpFile(EngineVersion.BrotherhoodOfSteel, "test.clp", data, 0, data.Length);
        clp.ReadDirectory();

        Assert.That(clp.Directory.Count, Is.EqualTo(2));
        var values = new System.Collections.Generic.List<LmpFile.EntryInfo>(clp.Directory.Values);
        Assert.That(values[0].StartOffset, Is.EqualTo(sectorSize));
        Assert.That(values[1].StartOffset, Is.EqualTo(sectorSize * 2));
        Assert.That(data[values[0].StartOffset], Is.EqualTo(0xAA));
        Assert.That(data[values[1].StartOffset], Is.EqualTo(0xBB));
    }

    [Test]
    public void Throws_on_non_clp_data()
    {
        var data = new byte[256];
        BitConverter.GetBytes((uint)0xDEADBEEF).CopyTo(data, 0);
        var clp = new ClpFile(EngineVersion.BrotherhoodOfSteel, "bad.clp", data, 0, data.Length);
        Assert.Throws<InvalidDataException>(() => clp.ReadDirectory());
    }
}
