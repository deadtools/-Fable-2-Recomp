// v17: the MenuHighlight green_top / green_top_translucent material samples
// Frames/frames_elements.tex, not Persistant/abyx.tex or frontendmainmenu.fac.
// C# 5 compatible: shared by the .NET 8 launcher and PowerShell 5.1 Add-Type.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Fable2Launcher
{
public static class DualShockMenuBadgePatcher
{
    public const string ResourceName = @"Art\GUI\Frames\frames_elements.tex";
    public const string BackupSuffix = ".dualshock-v17-frames-elements.original";

    public sealed class Result
    {
        public byte[] Textures = new byte[0];
        public byte[] Headers = new byte[0];
        public bool Changed;
        public bool Found;
        public string Message = "";
    }
    private sealed class Entry
    {
        public string Name = "";
        public int Offset, Size, Meta;
    }
    private sealed class Bank
    {
        public int Base;
        public byte[] Toc = new byte[0];
        public List<Entry> Entries = new List<Entry>();
    }

    public static Result Transform(byte[] textures, byte[] headers, bool enabled, bool requireTarget)
    {
        if (textures == null || headers == null) throw new ArgumentNullException("banks");
        Bank tex = ReadBank(textures), hdr = ReadBank(headers);
        Entry te = Find(tex, ResourceName), he = Find(hdr, ResourceName);
        var result = new Result { Textures = textures, Headers = headers };
        if (te == null && he == null && !requireTarget)
        {
            result.Message = "NOT PRESENT: frames_elements.tex (minimal/test bank); no badge changed.";
            return result;
        }
        if (te == null || he == null)
            throw new InvalidDataException("Missing frames_elements.tex texture/header pair.");
        result.Found = true;
        CheckHeader(headers, hdr, he, te.Size);
        byte[] current = GetEntry(textures, tex, te);
        string hash = Hash(current);
        if (hash != DualShockMenuBadgeArtwork.OriginalSha256 &&
            hash != DualShockMenuBadgeArtwork.PatchedSha256)
            throw new InvalidDataException("Unknown frames_elements.tex revision: SHA256=" + hash +
                "; size=" + te.Size + ". Banks left unchanged by the badge patcher.");

        byte[] replacement = DualShockMenuBadgeArtwork.Get(enabled);
        bool pixelsAlreadyCorrect = Equal(current, replacement);
        int headerOffset = checked(hdr.Base + he.Offset);
        bool headerAlreadyCorrect = Read32(headers, headerOffset + 4) == replacement.Length;
        if (pixelsAlreadyCorrect && headerAlreadyCorrect)
        {
            result.Message = "ALREADY VERIFIED: frames_elements.tex " +
                (enabled ? "contains the PS cross" : "contains the original green A") +
                "; entry SHA256=" + hash + "; native 512x512 header verified.";
            return result;
        }

        result.Textures = pixelsAlreadyCorrect ? textures : ReplaceEntry(textures, tex, te, replacement);
        result.Headers = (byte[])headers.Clone();
        Write32(result.Headers, headerOffset + 4, replacement.Length);
        // Everything except this one native entry and its header length must survive.
        VerifyResources(textures, result.Textures, replacement);
        for (int i = 0; i < headers.Length; i++)
            if ((i < headerOffset + 4 || i >= headerOffset + 8) && headers[i] != result.Headers[i])
                throw new InvalidDataException("An unrelated texture header was modified.");
        Bank checkedHeaders = ReadBank(result.Headers);
        CheckHeader(result.Headers, checkedHeaders, Find(checkedHeaders, ResourceName), replacement.Length);
        result.Changed = true;
        result.Message = (enabled ? "PATCHED: MenuHighlight green A -> PS cross" : "RESTORED: original green A") +
            "; Frames/frames_elements.tex; native format retained; all other resources unchanged; SHA256=" +
            Hash(replacement);
        return result;
    }

    public static void EnsureGameClosed()
    {
        Process[] games = Process.GetProcessesByName("fable_2");
        bool running = games.Length != 0;
        foreach (Process game in games) game.Dispose();
        if (running) throw new IOException("Close Fable II before changing GUI banks. The game was NOT terminated.");
    }

    public static string Apply(string gameDirectory, bool enabled)
    {
        return Apply(gameDirectory, enabled, false);
    }

    public static string Apply(string gameDirectory, bool enabled, bool requireTarget)
    {
        string root = Path.GetFullPath(gameDirectory);
        string folder = Path.Combine(root, "data", "art", "gui");
        string texturePath = Path.Combine(folder, "gui_textures.bnk");
        string headerPath = Path.Combine(folder, "gui_texture_headers.bnk");
        string logPath = Path.Combine(root, "launcher-dualshock-menu-v17.log");
        try
        {
            if (!File.Exists(texturePath) || !File.Exists(headerPath))
                throw new FileNotFoundException("The GUI texture/header banks were not found under " + folder);
            EnsureGameClosed();

            using (var gate = new FileStream(texturePath + ".dualshock-v17.lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None))
            {
                byte[] oldTextures = File.ReadAllBytes(texturePath);
                byte[] oldHeaders = File.ReadAllBytes(headerPath);
                Result result = Transform(oldTextures, oldHeaders, enabled, requireTarget);
                if (result.Changed)
                {
                    // Entry-level backups do not revert unrelated mods on restoration.
                    Bank hdr = ReadBank(oldHeaders);
                    byte[] originalHeader = GetEntry(oldHeaders, hdr, Find(hdr, ResourceName));
                    Write32(originalHeader, 4, DualShockMenuBadgeArtwork.OriginalSize);
                    EnsureBackup(texturePath + BackupSuffix + ".tex", DualShockMenuBadgeArtwork.Get(false));
                    EnsureBackup(headerPath + BackupSuffix + ".header", originalHeader);
                    CommitPair(texturePath, headerPath, oldTextures, oldHeaders, result.Textures, result.Headers);
                }
                string message = result.Message + Environment.NewLine +
                    "Texture bank: " + texturePath + Environment.NewLine +
                    "Header bank: " + headerPath + Environment.NewLine +
                    "guiscripts.bnk, FAC/BGF, controller bindings and abyx.tex were NOT modified by v17.";
                WriteLog(logPath, message);
                return result.Message;
            }
        }
        catch (Exception ex)
        {
            WriteLog(logPath, "ERROR: " + ex.Message);
            throw;
        }
    }

    private static void EnsureBackup(string path, byte[] data)
    {
        if (File.Exists(path))
        {
            if (!Equal(File.ReadAllBytes(path), data))
                throw new IOException("Existing v17 backup differs; refusing to overwrite it: " + path);
            return;
        }
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(data, 0, data.Length);
            file.Flush(true);
        }
        if (!Equal(File.ReadAllBytes(path), data)) throw new IOException("Backup verification failed: " + path);
    }

    // Two-file transaction with exact pre-write hashes and automatic rollback.
    // File.Replace rollback files are deleted ONLY on success or verified rollback.
    private static void CommitPair(string texturePath, string headerPath, byte[] oldTex, byte[] oldHdr,
        byte[] newTex, byte[] newHdr)
    {
        string id = Guid.NewGuid().ToString("N");
        string[] paths = { texturePath, headerPath };
        byte[][] originals = { oldTex, oldHdr }, replacements = { newTex, newHdr };
        string[] temps = { texturePath + ".v17-" + id + ".tmp", headerPath + ".v17-" + id + ".tmp" };
        string[] rollback = { texturePath + ".v17-" + id + ".rollback", headerPath + ".v17-" + id + ".rollback" };
        bool[] swapped = { false, false };
        try
        {
            for (int i = 0; i < 2; i++)
            {
                using (var f = new FileStream(temps[i], FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { f.Write(replacements[i], 0, replacements[i].Length); f.Flush(true); }
                if (Hash(File.ReadAllBytes(temps[i])) != Hash(replacements[i]))
                    throw new IOException("Staged GUI bank verification failed.");
            }
            for (int i = 0; i < 2; i++)
            {
                if (Hash(File.ReadAllBytes(paths[i])) != Hash(originals[i]))
                    throw new IOException("GUI bank changed concurrently: " + paths[i]);
                File.Replace(temps[i], paths[i], rollback[i], true);
                swapped[i] = true;
            }
            for (int i = 0; i < 2; i++)
                if (Hash(File.ReadAllBytes(paths[i])) != Hash(replacements[i]))
                    throw new IOException("Installed GUI bank verification failed: " + paths[i]);
        }
        catch (Exception installError)
        {
            string rollbackError = "";
            for (int i = 1; i >= 0; i--)
            {
                if (!swapped[i]) continue;
                try
                {
                    if (Hash(File.ReadAllBytes(rollback[i])) != Hash(originals[i]))
                        throw new IOException("Rollback copy failed checksum validation.");
                    File.Replace(rollback[i], paths[i], null, true);
                    if (Hash(File.ReadAllBytes(paths[i])) != Hash(originals[i]))
                        throw new IOException("Restored GUI bank failed checksum validation.");
                }
                catch (Exception ex) { rollbackError += " " + paths[i] + ": " + ex.Message; }
            }
            if (rollbackError.Length != 0)
                throw new IOException("Install failed: " + installError.Message +
                    ". ROLLBACK REQUIRES ATTENTION:" + rollbackError +
                    ". Keep any .rollback files next to the banks.", installError);
            throw;
        }
        finally
        {
            foreach (string temp in temps) TryDelete(temp);
        }
        foreach (string backup in rollback) TryDelete(backup);
    }

    private static Bank ReadBank(byte[] bytes)
    {
        if (bytes.Length < 21 || Read32(bytes, 4) != 3 || bytes[8] != 0)
            throw new InvalidDataException("Expected a compact texture bank (version 3, flag 0).");
        int baseOffset = Read32(bytes, 0), packed = Read32(bytes, 9), expected = Read32(bytes, 13);
        if (baseOffset < 21 || baseOffset >= bytes.Length || packed < 3 || packed > baseOffset - 17 ||
            expected < 4 || expected > 32 * 1024 * 1024)
            throw new InvalidDataException("Invalid BNK header boundaries.");
        // Original banks omit Adler32 and may end with a partial DEFLATE marker.
        // Read exactly the declared uncompressed TOC length; never decode into data.
        byte[] toc = new byte[expected];
        using (var input = new MemoryStream(bytes, 19, packed - 2, false))
        using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
        {
            int got = 0;
            while (got < expected)
            {
                int n = deflate.Read(toc, got, expected - got);
                if (n == 0) throw new InvalidDataException("Truncated BNK table.");
                got += n;
            }
        }
        var bank = new Bank { Base = baseOffset, Toc = toc };
        int count = Read32(toc, 0), at = 4;
        if (count < 1 || count > 100000) throw new InvalidDataException("Invalid BNK entry count.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var encoding = new UTF8Encoding(false, true);
        for (int i = 0; i < count; i++)
        {
            int length = Read32(toc, at); at = checked(at + 4);
            if (length < 2 || length > 4096 || at > toc.Length - length - 8 || toc[at + length - 1] != 0)
                throw new InvalidDataException("Invalid BNK resource name.");
            string name = encoding.GetString(toc, at, length - 1).Replace('/', '\\');
            if (name.IndexOf('\0') >= 0 || !names.Add(name))
                throw new InvalidDataException("Duplicate/invalid BNK resource name.");
            at += length;
            var entry = new Entry { Name = name, Meta = at, Offset = Read32(toc, at), Size = Read32(toc, at + 4) };
            if (entry.Size < 1 || (long)baseOffset + entry.Offset + entry.Size > bytes.Length)
                throw new InvalidDataException("BNK entry outside bank: " + name);
            bank.Entries.Add(entry); at += 8;
        }
        if (at != toc.Length) throw new InvalidDataException("Unexpected compact BNK table tail.");
        var sorted = new List<Entry>(bank.Entries);
        sorted.Sort(delegate(Entry a, Entry b) { return a.Offset.CompareTo(b.Offset); });
        int end = 0;
        foreach (Entry e in sorted)
        {
            if (e.Offset < end) throw new InvalidDataException("Overlapping BNK entries.");
            end = checked(e.Offset + e.Size);
        }
        return bank;
    }

    private static byte[] ReplaceEntry(byte[] original, Bank bank, Entry target, byte[] replacement)
    {
        int delta = checked(replacement.Length - target.Size);
        byte[] output = new byte[checked(original.Length + delta)];
        int start = checked(bank.Base + target.Offset), end = checked(start + target.Size);
        Buffer.BlockCopy(original, 0, output, 0, start);
        Buffer.BlockCopy(replacement, 0, output, start, replacement.Length);
        Buffer.BlockCopy(original, end, output, start + replacement.Length, original.Length - end);
        byte[] toc = (byte[])bank.Toc.Clone();
        foreach (Entry e in bank.Entries)
        {
            if (object.ReferenceEquals(e, target)) Write32(toc, e.Meta + 4, replacement.Length);
            else if (e.Offset >= target.Offset + target.Size) Write32(toc, e.Meta, checked(e.Offset + delta));
        }
        byte[] compressed;
        using (var stream = new MemoryStream())
        {
            // zlib header + raw DEFLATE; no Adler trailer, as in the game banks.
            stream.WriteByte(0x78); stream.WriteByte(0x9C);
            using (var deflate = new DeflateStream(stream, CompressionLevel.Optimal, true))
                deflate.Write(toc, 0, toc.Length);
            compressed = stream.ToArray();
        }
        if (compressed.Length > bank.Base - 17) throw new InvalidDataException("BNK table exceeds data base.");
        Array.Clear(output, 17, bank.Base - 17);
        Buffer.BlockCopy(compressed, 0, output, 17, compressed.Length);
        Write32(output, 9, compressed.Length);
        Write32(output, 13, toc.Length);
        return output;
    }

    private static void VerifyResources(byte[] original, byte[] patched, byte[] replacement)
    {
        Bank before = ReadBank(original), after = ReadBank(patched);
        if (before.Entries.Count != after.Entries.Count) throw new InvalidDataException("BNK entry count changed.");
        var index = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (Entry e in after.Entries) index.Add(e.Name, e);
        foreach (Entry e in before.Entries)
        {
            Entry next;
            if (!index.TryGetValue(e.Name, out next)) throw new InvalidDataException("BNK resource lost.");
            byte[] expected = string.Equals(e.Name, ResourceName, StringComparison.OrdinalIgnoreCase)
                ? replacement : GetEntry(original, before, e);
            if (!Equal(expected, GetEntry(patched, after, next)))
                throw new InvalidDataException("BNK resource verification failed: " + e.Name);
        }
    }

    private static void CheckHeader(byte[] data, Bank bank, Entry entry, int textureSize)
    {
        if (entry == null || entry.Size != 84) throw new InvalidDataException("Invalid menu badge texture header.");
        int o = bank.Base + entry.Offset;
        int length = Read32(data, o + 4);
        if (Read32(data, o + 16) != 512 || Read32(data, o + 20) != 512 ||
            Read32(data, o + 24) != 39 || Read32(data, o + 28) != 1 ||
            (length != textureSize && length != DualShockMenuBadgeArtwork.OriginalSize &&
             length != DualShockMenuBadgeArtwork.PatchedSize))
            throw new InvalidDataException("Unsupported menu badge dimensions/format/header length.");
    }
    private static Entry Find(Bank bank, string name)
    {
        foreach (Entry e in bank.Entries)
            if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }
    private static byte[] GetEntry(byte[] data, Bank bank, Entry entry)
    {
        if (entry == null) throw new InvalidDataException("Resource not found.");
        byte[] result = new byte[entry.Size];
        Buffer.BlockCopy(data, bank.Base + entry.Offset, result, 0, result.Length);
        return result;
    }
    private static int Read32(byte[] data, int offset)
    {
        if (offset < 0 || offset > data.Length - 4) throw new InvalidDataException("BNK word outside data.");
        uint value = ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
                     ((uint)data[offset + 2] << 8) | data[offset + 3];
        if (value > int.MaxValue) throw new InvalidDataException("BNK value too large.");
        return (int)value;
    }
    private static void Write32(byte[] data, int offset, int value)
    {
        if (value < 0 || offset < 0 || offset > data.Length - 4) throw new InvalidDataException("Invalid BNK word write.");
        data[offset] = (byte)(value >> 24); data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8); data[offset + 3] = (byte)value;
    }
    private static bool Equal(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    private static string Hash(byte[] data) { return DualShockMenuBadgeArtwork.Hash(data); }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void WriteLog(string path, string text)
    {
        try { File.WriteAllText(path, DateTime.Now.ToString("s") + Environment.NewLine + text + Environment.NewLine,
            new UTF8Encoding(false)); } catch { }
    }
}
}
