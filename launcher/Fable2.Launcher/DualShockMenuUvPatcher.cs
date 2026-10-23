using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Fable2Launcher;

/// <summary>
/// Fable II's round A selector is baked into a GameFace presentation's UV map.
/// The material uses Persistant/abyx.tga (Xbox A/B/Y/X in four 64px columns).
/// Switching only the two A-select UVs from column 0 to column 3 shows the
/// original blue Xbox X without changing its action binding or HOLD animation.
/// This is independent of the .tex texture re-encoding used for PS prompts.
/// </summary>
internal static class DualShockMenuUvPatcher
{
    private const string Target = @"art\gui\gameface\frontendmainmenu.fac";
    internal const string BackupSuffix = ".dualshock-menu-original.bak";
    private const int TocStart = 17;

    // Both the game-supplied game-data layout and older unpacker layouts exist.
    private static readonly string[] RelativeBanks =
    {
        Path.Combine("Data", "guiscripts.bnk"),
        Path.Combine("data", "art", "gui", "guiscripts.bnk"),
    };

    private static readonly Regex OriginalA = new(
        @"(?m)(^222321799\r?\n0\.25\r?\n0\.25\r?\n)0(\r?\n0\.75\r?\n)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex XboxX = new(
        @"(?m)^222321799\r?\n0\.25\r?\n0\.25\r?\n0\.75\r?\n0\.75\r?\n",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private sealed record Entry(string Name, int Offset, int UncompressedBytes,
        int CompressedBytes, int ChunkCount, int MetaOffset);
    private sealed record Bank(int BaseOffset, byte[] Toc, List<Entry> Entries);

    internal static string Apply(string gameRoot, bool enabled)
    {
        var findings = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relative in RelativeBanks)
        {
            string path = Path.GetFullPath(Path.Combine(gameRoot, relative));
            if (!visited.Add(path)) continue;
            string backup = path + BackupSuffix;
            if (!File.Exists(path)) continue;

            if (!enabled)
            {
                if (File.Exists(backup))
                {
                    AtomicWrite(path, File.ReadAllBytes(backup));
                    findings.Add(relative + ": original GameFace menu restored");
                }
                continue;
            }

            byte[] source = File.ReadAllBytes(path);
            if (TryPatchBank(source, out byte[] patched, out string report))
            {
                if (!File.Exists(backup)) File.Copy(path, backup, overwrite: false);
                AtomicWrite(path, patched);
                findings.Add(relative + ": " + report);
            }
            else
            {
                findings.Add(relative + ": " + report);
            }
        }
        return findings.Count == 0
            ? "guiscripts.bnk was not found in Data/ or data/art/gui/; no menu UV changes"
            : string.Join("; ", findings);
    }

    /// <summary>
    /// Only two selected-A quads are modified; an existing modification is
    /// recognized and left untouched. Unsupported menus are never guessed at.
    /// </summary>
    internal static bool TryRewriteFac(byte[] source, out byte[] patched,
        out string report)
    {
        patched = source;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(source); }
        catch (DecoderFallbackException)
        {
            report = "non-text frontendmainmenu.fac; skipped";
            return false;
        }
        MatchCollection matches = OriginalA.Matches(text);
        if (matches.Count == 0 && XboxX.Matches(text).Count >= 3)
        {
            report = "already patched: two A selectors point to Xbox X atlas cell";
            return false;
        }
        if (matches.Count != 2)
        {
            report = $"expected 2 selected-A quads, found {matches.Count}; skipped";
            return false;
        }
        string edited = OriginalA.Replace(text, m => m.Groups[1].Value + "0.75" + m.Groups[2].Value);
        if (OriginalA.IsMatch(edited) || XboxX.Matches(edited).Count < 3)
            throw new InvalidDataException("GameFace menu UV patch did not pass validation.");
        patched = Encoding.UTF8.GetBytes(edited);
        report = "two selected-A quads redirected from green A to blue Xbox X";
        return true;
    }

    internal static bool TryPatchBank(byte[] source, out byte[] patched, out string report)
    {
        patched = source;
        Bank bank = Parse(source);
        Entry? target = bank.Entries.FirstOrDefault(e =>
            e.Name.Equals(Target, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            report = "frontendmainmenu.fac not present in this GUI bank";
            return false;
        }
        if (target.ChunkCount != 1)
        {
            report = "unsupported multi-chunk FAC; original retained";
            return false;
        }
        byte[] oldPacked = source.AsSpan(bank.BaseOffset + target.Offset,
            target.CompressedBytes).ToArray();
        byte[] oldFac = Inflate(oldPacked, target.UncompressedBytes);
        if (!TryRewriteFac(oldFac, out byte[] newFac, out report)) return false;
        byte[] replacement = DeflateWithoutAdler(newFac);

        byte[] toc = (byte[])bank.Toc.Clone();
        var ordered = bank.Entries.OrderBy(e => e.Offset).ToArray();
        using var payload = new MemoryStream(source.Length - bank.BaseOffset + 512);
        int previousEnd = 0;
        foreach (Entry entry in ordered)
        {
            if (entry.Offset < previousEnd)
                throw new InvalidDataException("Overlapping guiscripts.bnk resources.");
            int gap = entry.Offset - previousEnd;
            if (gap > 0)
                payload.Write(source, bank.BaseOffset + previousEnd, gap);
            int newOffset = checked((int)payload.Position);
            WriteBe32(toc, entry.MetaOffset, (uint)newOffset);
            if (entry == target)
            {
                WriteBe32(toc, entry.MetaOffset + 4, (uint)newFac.Length);
                WriteBe32(toc, entry.MetaOffset + 8, (uint)replacement.Length);
                WriteBe32(toc, entry.MetaOffset + 16, (uint)newFac.Length);
                payload.Write(replacement);
            }
            else
            {
                payload.Write(source, bank.BaseOffset + entry.Offset,
                    entry.CompressedBytes);
            }
            previousEnd = checked(entry.Offset + entry.CompressedBytes);
        }
        int dataLength = source.Length - bank.BaseOffset;
        if (previousEnd < dataLength)
            payload.Write(source, bank.BaseOffset + previousEnd,
                dataLength - previousEnd);

        byte[] compressedToc = DeflateWithoutAdler(toc);
        if (TocStart + compressedToc.Length > bank.BaseOffset)
            throw new InvalidDataException("Updated GUI bank TOC exceeds reserved header.");
        byte[] content = payload.ToArray();
        byte[] next = new byte[checked(bank.BaseOffset + content.Length)];
        Buffer.BlockCopy(source, 0, next, 0, bank.BaseOffset);
        Array.Clear(next, TocStart, bank.BaseOffset - TocStart);
        Buffer.BlockCopy(compressedToc, 0, next, TocStart, compressedToc.Length);
        WriteBe32(next, 9, (uint)compressedToc.Length);
        WriteBe32(next, 13, (uint)toc.Length);
        Buffer.BlockCopy(content, 0, next, bank.BaseOffset, content.Length);

        // Verify all 269 unrelated resources without decompressing them.
        Bank verified = Parse(next);
        if (verified.Entries.Count != bank.Entries.Count)
            throw new InvalidDataException("GUI bank entry count changed after repack.");
        var revised = verified.Entries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        foreach (Entry before in bank.Entries)
        {
            if (!revised.TryGetValue(before.Name, out Entry? after))
                throw new InvalidDataException("GUI bank entry missing after repack: " + before.Name);
            if (before == target)
            {
                byte[] check = Inflate(next.AsSpan(verified.BaseOffset + after.Offset,
                    after.CompressedBytes).ToArray(), after.UncompressedBytes);
                if (!check.AsSpan().SequenceEqual(newFac))
                    throw new InvalidDataException("New GameFace FAC failed validation.");
                continue;
            }
            if (before.CompressedBytes != after.CompressedBytes ||
                !source.AsSpan(bank.BaseOffset + before.Offset, before.CompressedBytes)
                    .SequenceEqual(next.AsSpan(verified.BaseOffset + after.Offset, after.CompressedBytes)))
                throw new InvalidDataException("Unrelated GUI resource changed: " + before.Name);
        }
        patched = next;
        report += "; other GUI resources preserved";
        return true;
    }

    private static Bank Parse(byte[] source)
    {
        if (source.Length < 32768) throw new InvalidDataException("GUI bank too short.");
        int baseOffset = checked((int)ReadBe32(source, 0));
        int packedToc = checked((int)ReadBe32(source, 9));
        int unpackedToc = checked((int)ReadBe32(source, 13));
        if (baseOffset < 128 || baseOffset >= source.Length || packedToc < 10 ||
            TocStart + packedToc > baseOffset || unpackedToc < 16 || unpackedToc > 8 * 1024 * 1024)
            throw new InvalidDataException("Invalid guiscripts.bnk header.");
        byte[] toc = Inflate(source.AsSpan(TocStart, packedToc).ToArray(), unpackedToc);
        uint count = ReadBe32(toc, 0);
        if (count < 1 || count > 100000) throw new InvalidDataException("Invalid GUI entry count.");
        int at = 4;
        var entries = new List<Entry>((int)Math.Min(count, 4096));
        for (uint i = 0; i < count; i++)
        {
            if (at > toc.Length - 4) throw new InvalidDataException("Truncated GUI bank TOC.");
            int nameLength = checked((int)ReadBe32(toc, at)); at += 4;
            if (nameLength < 1 || nameLength > 4096 || nameLength > toc.Length - at - 16)
                throw new InvalidDataException("Invalid GUI asset name.");
            string name = Encoding.UTF8.GetString(toc, at, nameLength).TrimEnd('\0');
            at += nameLength;
            int meta = at;
            int offset = checked((int)ReadBe32(toc, at));
            int uncompressed = checked((int)ReadBe32(toc, at + 4));
            int compressed = checked((int)ReadBe32(toc, at + 8));
            int chunks = checked((int)ReadBe32(toc, at + 12));
            at += 16;
            if (chunks < 1 || chunks > 64 || at > toc.Length - chunks * 4)
                throw new InvalidDataException("Invalid GUI entry chunks: " + name);
            long total = 0;
            for (int c = 0; c < chunks; c++)
                total += ReadBe32(toc, at + c * 4);
            at += chunks * 4;
            if (total != uncompressed || offset < 0 || compressed < 1 ||
                (long)baseOffset + offset + compressed > source.Length)
                throw new InvalidDataException("Invalid GUI entry span: " + name);
            entries.Add(new Entry(name, offset, uncompressed, compressed, chunks, meta));
        }
        if (at != toc.Length) throw new InvalidDataException("Unexpected GUI TOC bytes.");
        return new Bank(baseOffset, toc, entries);
    }

    private static byte[] Inflate(byte[] compressed, int expected)
    {
        byte[] Expand(Stream decompressor)
        {
            using (decompressor)
            using (var output = new MemoryStream(expected))
            {
                decompressor.CopyTo(output);
                byte[] bytes = output.ToArray();
                if (bytes.Length != expected)
                    throw new InvalidDataException($"GUI asset inflate size mismatch ({bytes.Length}/{expected}).");
                return bytes;
            }
        }
        try
        {
            return Expand(new ZLibStream(new MemoryStream(compressed, writable: false),
                CompressionMode.Decompress));
        }
        catch (InvalidDataException)
        {
            if (compressed.Length < 3) throw;
            return Expand(new DeflateStream(new MemoryStream(compressed, 2,
                compressed.Length - 2, writable: false), CompressionMode.Decompress));
        }
    }

    private static byte[] DeflateWithoutAdler(byte[] source)
    {
        using var stream = new MemoryStream();
        using (var z = new ZLibStream(stream, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(source);
        byte[] packed = stream.ToArray();
        if (packed.Length <= 6) throw new InvalidDataException("GUI compression failed.");
        Array.Resize(ref packed, packed.Length - 4);
        return packed;
    }
    private static uint ReadBe32(byte[] data, int at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) |
        ((uint)data[at + 2] << 8) | data[at + 3];
    private static void WriteBe32(byte[] data, int at, uint value)
    {
        data[at] = (byte)(value >> 24);
        data[at + 1] = (byte)(value >> 16);
        data[at + 2] = (byte)(value >> 8);
        data[at + 3] = (byte)value;
    }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        string temporary = path + ".dualshock-uv-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path)) File.Move(temporary, path, overwrite: true);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
