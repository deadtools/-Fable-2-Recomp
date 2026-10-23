using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// Kept compatible with Windows PowerShell 5.1 Add-Type (.NET Framework C# compiler).
// This file is compiled on the user's Windows PC by deploy-dualshock-menu.ps1;
// the standalone installer does not need the .NET 8 SDK.
public static class FableGameFaceBankPatcherV16
{
    private const int TocStart = 17;
    private const string TargetName = @"art\gui\gameface\frontendmainmenu.fac";

    private sealed class Item
    {
        public string Name;
        public int Offset;
        public int RawSize;
        public int PackedSize;
        public int Chunks;
        public int Meta;
    }
    private sealed class Bank
    {
        public int Base;
        public byte[] Toc;
        public List<Item> Items;
    }

    // The two active A selectors use the first column in the 256x256 ABYX atlas.
    // Column 4 is the original blue Xbox X, which has no effect on the A action.
    private static readonly Regex GreenA = new Regex(
        @"(?m)(^222321799\r?\n0\.25\r?\n0\.25\r?\n)0(\r?\n0\.75\r?\n)",
        RegexOptions.CultureInvariant);
    private static readonly Regex BlueX = new Regex(
        @"(?m)^222321799\r?\n0\.25\r?\n0\.25\r?\n0\.75\r?\n0\.75\r?\n",
        RegexOptions.CultureInvariant);

    // Writes output only after all bank validation succeeds.
    // Returns PATCHED or ALREADY, followed by a compact diagnostic description.
    public static string Patch(string originalPath, string outputPath)
    {
        byte[] original = File.ReadAllBytes(originalPath);
        Bank bank = Parse(original);
        Item target = null;
        foreach (Item item in bank.Items)
            if (String.Equals(item.Name, TargetName, StringComparison.OrdinalIgnoreCase))
            {
                if (target != null) throw new InvalidDataException("Duplicate frontendmainmenu.fac resources.");
                target = item;
            }
        if (target == null)
            throw new InvalidDataException("No frontendmainmenu.fac in this guiscripts.bnk; bank left unchanged.");
        if (target.Chunks != 1)
            throw new InvalidDataException("Unsupported multi-chunk frontendmainmenu.fac; bank left unchanged.");

        byte[] oldFac = Inflate(original, bank.Base + target.Offset,
                                target.PackedSize, target.RawSize);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(oldFac); }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("frontendmainmenu.fac is not UTF-8; bank left unchanged.");
        }
        int greenCount = GreenA.Matches(text).Count;
        int blueCount = BlueX.Matches(text).Count;
        if (greenCount == 0 && blueCount >= 3)
            return "ALREADY: frontendmainmenu.fac uses the blue X atlas cell";
        if (greenCount != 2)
            throw new InvalidDataException("Unexpected menu UV layout: green A=" + greenCount +
                ", blue X=" + blueCount + "; expected two original A UVs. Bank left unchanged.");
        string edited = GreenA.Replace(text, delegate(Match match)
        {
            return match.Groups[1].Value + "0.75" + match.Groups[2].Value;
        });
        if (GreenA.Matches(edited).Count != 0 || BlueX.Matches(edited).Count != blueCount + 2)
            throw new InvalidDataException("Edited menu UVs failed their validation.");
        byte[] newFac = new UTF8Encoding(false).GetBytes(edited);
        byte[] newPacked = DeflateWithoutAdler(newFac);

        var toc = (byte[])bank.Toc.Clone();
        var ordered = new List<Item>(bank.Items);
        ordered.Sort(delegate(Item a, Item b) { return a.Offset.CompareTo(b.Offset); });
        MemoryStream payload = new MemoryStream(original.Length - bank.Base + 4096);
        int previous = 0;
        foreach (Item item in ordered)
        {
            if (item.Offset < previous)
                throw new InvalidDataException("Overlapping GUI bank asset spans.");
            if (item.Offset > previous)
                payload.Write(original, bank.Base + previous, item.Offset - previous);
            int nextRelativeOffset = checked((int)payload.Position);
            WriteBe32(toc, item.Meta, (uint)nextRelativeOffset);
            if (Object.ReferenceEquals(item, target))
            {
                WriteBe32(toc, item.Meta + 4, (uint)newFac.Length);
                WriteBe32(toc, item.Meta + 8, (uint)newPacked.Length);
                WriteBe32(toc, item.Meta + 16, (uint)newFac.Length);
                payload.Write(newPacked, 0, newPacked.Length);
            }
            else
                payload.Write(original, bank.Base + item.Offset, item.PackedSize);
            previous = checked(item.Offset + item.PackedSize);
        }
        if (previous < original.Length - bank.Base)
            payload.Write(original, bank.Base + previous, original.Length - bank.Base - previous);

        byte[] compressedToc = DeflateWithoutAdler(toc);
        if (TocStart + compressedToc.Length >= bank.Base)
            throw new InvalidDataException("Repacked TOC does not fit the original bank header.");
        byte[] data = payload.ToArray();
        byte[] rebuilt = new byte[checked(bank.Base + data.Length)];
        Buffer.BlockCopy(original, 0, rebuilt, 0, bank.Base);
        Array.Clear(rebuilt, TocStart, bank.Base - TocStart);
        Buffer.BlockCopy(compressedToc, 0, rebuilt, TocStart, compressedToc.Length);
        WriteBe32(rebuilt, 9, (uint)compressedToc.Length);
        WriteBe32(rebuilt, 13, (uint)toc.Length);
        Buffer.BlockCopy(data, 0, rebuilt, bank.Base, data.Length);

        // Round-trip parse and prove every unrelated compressed resource is identical.
        Bank check = Parse(rebuilt);
        if (check.Items.Count != bank.Items.Count)
            throw new InvalidDataException("Repacked GUI asset count changed.");
        var lookup = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        foreach (Item after in check.Items)
        {
            if (lookup.ContainsKey(after.Name))
                throw new InvalidDataException("Repacked GUI has duplicate entry names.");
            lookup.Add(after.Name, after);
        }
        int verified = 0;
        foreach (Item before in bank.Items)
        {
            Item after;
            if (!lookup.TryGetValue(before.Name, out after))
                throw new InvalidDataException("Repacked GUI missing resource: " + before.Name);
            if (Object.ReferenceEquals(before, target))
            {
                byte[] testFac = Inflate(rebuilt, check.Base + after.Offset,
                                         after.PackedSize, after.RawSize);
                if (!EqualBytes(testFac, newFac))
                    throw new InvalidDataException("Repacked frontendmainmenu.fac differs from intended content.");
            }
            else
            {
                if (before.RawSize != after.RawSize || before.Chunks != after.Chunks ||
                    before.PackedSize != after.PackedSize ||
                    !EqualRange(original, bank.Base + before.Offset,
                        rebuilt, check.Base + after.Offset, before.PackedSize))
                    throw new InvalidDataException("Unrelated GUI resource was changed: " + before.Name);
                verified++;
            }
        }
        File.WriteAllBytes(outputPath, rebuilt);
        return "PATCHED: 2 menu A UV coordinates changed to Xbox X; " + verified +
               " unrelated resources preserved byte-for-byte";
    }

    private static Bank Parse(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 32768)
            throw new InvalidDataException("guiscripts.bnk is missing or too small.");
        int baseOffset = NonNegativeU32(bytes, 0);
        int compressedSize = NonNegativeU32(bytes, 9);
        int rawSize = NonNegativeU32(bytes, 13);
        if (baseOffset < 256 || baseOffset >= bytes.Length || compressedSize < 8 ||
            TocStart + (long)compressedSize > baseOffset || rawSize < 16 || rawSize > 16000000)
            throw new InvalidDataException("Unsupported guiscripts.bnk header; unchanged.");
        byte[] toc = Inflate(bytes, TocStart, compressedSize, rawSize);
        int count = NonNegativeU32(toc, 0);
        if (count < 1 || count > 100000)
            throw new InvalidDataException("Invalid guiscripts.bnk resource count.");
        int pos = 4;
        List<Item> entries = new List<Item>(count);
        HashSet<string> unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            if (pos > toc.Length - 4) throw new InvalidDataException("Truncated TOC entry.");
            int nameBytes = NonNegativeU32(toc, pos); pos += 4;
            if (nameBytes < 2 || nameBytes > 4096 || nameBytes > toc.Length - pos - 16)
                throw new InvalidDataException("Invalid GUI resource name length.");
            string name = Encoding.UTF8.GetString(toc, pos, nameBytes).TrimEnd('\0');
            pos += nameBytes;
            int metadata = pos;
            int offset = NonNegativeU32(toc, pos);
            int uncompressed = NonNegativeU32(toc, pos + 4);
            int compressed = NonNegativeU32(toc, pos + 8);
            int chunks = NonNegativeU32(toc, pos + 12);
            pos += 16;
            if (chunks < 1 || chunks > 64 || chunks * 4 > toc.Length - pos)
                throw new InvalidDataException("Invalid chunks for: " + name);
            long sumChunks = 0;
            for (int j = 0; j < chunks; j++)
                sumChunks += ReadBe32(toc, pos + j * 4);
            pos += chunks * 4;
            if (sumChunks != uncompressed || compressed <= 0 ||
                baseOffset + (long)offset + compressed > bytes.Length || !unique.Add(name))
                throw new InvalidDataException("Invalid resource span / duplicate name: " + name);
            entries.Add(new Item { Name = name, Offset = offset, RawSize = uncompressed,
                PackedSize = compressed, Chunks = chunks, Meta = metadata });
        }
        if (pos != toc.Length)
            throw new InvalidDataException("Unrecognized trailing data in GUI TOC.");
        return new Bank { Base = baseOffset, Toc = toc, Items = entries };
    }

    private static byte[] Inflate(byte[] data, int start, int count, int expected)
    {
        if (start < 0 || count < 3 || (long)start + count > data.Length ||
            data[start] != 0x78)
            throw new InvalidDataException("Unsupported GUI zlib/DEFLATE stream.");
        using (MemoryStream src = new MemoryStream(data, start + 2, count - 2, false))
        using (DeflateStream deflate = new DeflateStream(src, CompressionMode.Decompress))
        using (MemoryStream dst = new MemoryStream(expected))
        {
            deflate.CopyTo(dst);
            byte[] result = dst.ToArray();
            if (result.Length != expected)
                throw new InvalidDataException("GUI decompression length mismatch: " +
                    result.Length + " versus " + expected);
            return result;
        }
    }

    private static byte[] DeflateWithoutAdler(byte[] source)
    {
        using (MemoryStream dst = new MemoryStream())
        {
            // Game banks use a 2-byte zlib header + raw DEFLATE, no Adler32.
            dst.WriteByte(0x78);
            dst.WriteByte(0x9c);
            using (DeflateStream compressor = new DeflateStream(dst,
                CompressionMode.Compress, true))
                compressor.Write(source, 0, source.Length);
            return dst.ToArray();
        }
    }
    private static uint ReadBe32(byte[] bytes, int at)
    {
        if (at < 0 || at > bytes.Length - 4)
            throw new InvalidDataException("Truncated GUI bank integer.");
        return ((uint)bytes[at] << 24) | ((uint)bytes[at + 1] << 16) |
               ((uint)bytes[at + 2] << 8) | bytes[at + 3];
    }
    private static int NonNegativeU32(byte[] bytes, int at)
    {
        uint value = ReadBe32(bytes, at);
        if (value > 0x7fffffffU)
            throw new InvalidDataException("Unsupported large GUI bank field.");
        return (int)value;
    }
    private static void WriteBe32(byte[] bytes, int at, uint value)
    {
        if (at < 0 || at > bytes.Length - 4)
            throw new InvalidDataException("Invalid GUI bank write offset.");
        bytes[at] = (byte)(value >> 24);
        bytes[at + 1] = (byte)(value >> 16);
        bytes[at + 2] = (byte)(value >> 8);
        bytes[at + 3] = (byte)value;
    }
    private static bool EqualBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        return EqualRange(a, 0, b, 0, a.Length);
    }
    private static bool EqualRange(byte[] a, int ia, byte[] b, int ib, int count)
    {
        if (ia < 0 || ib < 0 || count < 0 || (long)ia + count > a.Length ||
            (long)ib + count > b.Length) return false;
        for (int i = 0; i < count; i++)
            if (a[ia + i] != b[ib + i]) return false;
        return true;
    }
}
