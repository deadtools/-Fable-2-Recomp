using System.IO.Compression;
using System.Text;
using Fable2Launcher;

internal static class MenuBadgeV17Tests
{
    private static int _checks;
    private static void Check(bool value, string description)
    {
        if (!value) throw new InvalidOperationException("Menu badge v17: " + description);
        _checks++;
    }
    private static void MustReject(Action action, string description)
    {
        bool rejected = false;
        try { action(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, description);
    }

    public static void Run(string root)
    {
        byte[] original = DualShockMenuBadgeArtwork.Get(false);
        byte[] patched = DualShockMenuBadgeArtwork.Get(true);
        Check(original.Length == 75408 && patched.Length == 127796, "native entry sizes");
        Check(DualShockMenuBadgeArtwork.Hash(original) == DualShockMenuBadgeArtwork.OriginalSha256, "original asset hash");
        Check(DualShockMenuBadgeArtwork.Hash(patched) == DualShockMenuBadgeArtwork.PatchedSha256, "patched asset hash");
        Check(Get32(original, 0) == 1 && Get32(patched, 0) == 1 && Get32(patched, 12) == 3, "native color/alpha codecs retained");
        Check(original.AsSpan(Get32(original, 16), Get32(original, 20))
            .SequenceEqual(patched.AsSpan(Get32(patched, 16), Get32(patched, 20))), "compressed alpha stream byte-identical");

        const string target = DualShockMenuBadgePatcher.ResourceName;
        const string other = @"Art\GUI\Persistant\unchanged_test.tex";
        byte[] sentinel = Enumerable.Range(0, 300).Select(i => (byte)(i * 13)).ToArray();
        byte[] tex = Bank((target, original), (other, sentinel));
        byte[] targetHeader = Header(original.Length);
        byte[] headers = Bank((target, targetHeader), (other, Header(sentinel.Length)));
        byte[] untouchedTex = (byte[])tex.Clone(), untouchedHeaders = (byte[])headers.Clone();
        var first = DualShockMenuBadgePatcher.Transform(tex, headers, true, true);
        Check(first.Found && first.Changed && first.Message.StartsWith("PATCHED:"), "native badge patch is applied");
        Check(Extract(first.Textures, target).SequenceEqual(patched), "patched entry exact match");
        Check(Extract(first.Textures, other).SequenceEqual(sentinel), "unrelated texture remains byte-identical");
        byte[] nextHeader = Extract(first.Headers, target);
        Check(Get32(nextHeader, 4) == patched.Length, "native length header updated");
        Check(nextHeader.Take(4).SequenceEqual(targetHeader.Take(4)) &&
              nextHeader.Skip(8).SequenceEqual(targetHeader.Skip(8)), "all other native header fields unchanged");
        Check(Extract(first.Headers, other).SequenceEqual(Extract(headers, other)), "unrelated header unchanged");
        Check(tex.SequenceEqual(untouchedTex) && headers.SequenceEqual(untouchedHeaders), "input arrays immutable");

        var second = DualShockMenuBadgePatcher.Transform(first.Textures, first.Headers, true, true);
        Check(!second.Changed && second.Message.StartsWith("ALREADY VERIFIED:"), "idempotent patch");
        Check(second.Textures.SequenceEqual(first.Textures) && second.Headers.SequenceEqual(first.Headers), "second application does not rewrite banks");
        var restored = DualShockMenuBadgePatcher.Transform(first.Textures, first.Headers, false, true);
        Check(restored.Changed && Extract(restored.Textures, target).SequenceEqual(original), "targeted restore");
        Check(Extract(restored.Textures, other).SequenceEqual(sentinel) && restored.Headers.SequenceEqual(headers), "restore preserves other resources");
        Check(!DualShockMenuBadgePatcher.Transform(restored.Textures, restored.Headers, false, true).Changed, "restore is idempotent");

        // A stale length from the old/new native entry must be repaired, not
        // misreported as ALREADY merely because a different FAC was changed.
        var lengthRepair = DualShockMenuBadgePatcher.Transform(first.Textures, headers, true, true);
        Check(lengthRepair.Changed && lengthRepair.Headers.SequenceEqual(first.Headers), "stale header size is repaired");
        byte[] wrong = (byte[])original.Clone(); wrong[1000] ^= 1;
        MustReject(() => DualShockMenuBadgePatcher.Transform(Bank((target, wrong), (other, sentinel)), headers, true, true), "unknown texture revision rejected");
        byte[] wrongHeader = (byte[])targetHeader.Clone(); Put32(wrongHeader, 24, 2);
        MustReject(() => DualShockMenuBadgePatcher.Transform(tex, Bank((target, wrongHeader)), true, true), "wrong pixel format rejected");
        MustReject(() => DualShockMenuBadgePatcher.Transform(Bank((other, sentinel)), Bank((other, Header(sentinel.Length))), true, true), "installer requires actual target");
        var minimal = DualShockMenuBadgePatcher.Transform(Bank((other, sentinel)), Bank((other, Header(sentinel.Length))), true, false);
        Check(!minimal.Found && !minimal.Changed, "legacy minimal test bank safely skipped");
        byte[] malformed = (byte[])tex.Clone(); Put32(malformed, 9, int.MaxValue);
        MustReject(() => DualShockMenuBadgePatcher.Transform(malformed, headers, true, true), "out-of-bounds compressed table rejected");
        Console.WriteLine($"MenuHighlight native badge v17 regression checks passed: {_checks}.");
    }

    private static byte[] Header(int size)
    {
        byte[] data = new byte[84];
        data[0] = 255; data[1] = 255; data[2] = 255; data[3] = 254;
        Put32(data, 4, size); Put32(data, 12, 0xB0);
        Put32(data, 16, 512); Put32(data, 20, 512); Put32(data, 24, 39);
        Put32(data, 28, 1); Put32(data, 32, 84); Put32(data, 80, 2);
        return data;
    }
    private static byte[] Bank(params (string name, byte[] bytes)[] entries)
    {
        using var table = new MemoryStream();
        Write32(table, entries.Length);
        int offset = 0;
        foreach (var e in entries)
        {
            byte[] name = Encoding.UTF8.GetBytes(e.name + "\0");
            Write32(table, name.Length); table.Write(name);
            Write32(table, offset); Write32(table, e.bytes.Length);
            offset += e.bytes.Length + 8; // intentional gaps to test their preservation
        }
        using var packed = new MemoryStream();
        packed.WriteByte(0x78); packed.WriteByte(0x9C);
        using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal, true))
            deflate.Write(table.ToArray());
        byte[] output = new byte[32768 + offset + 16];
        Put32(output, 0, 32768); Put32(output, 4, 3); output[8] = 0;
        Put32(output, 9, (int)packed.Length); Put32(output, 13, (int)table.Length);
        packed.ToArray().CopyTo(output, 17);
        offset = 32768;
        foreach (var e in entries)
        {
            e.bytes.CopyTo(output, offset); offset += e.bytes.Length;
            Array.Fill(output, (byte)0xC7, offset, 8); offset += 8;
        }
        return output;
    }
    private static byte[] Extract(byte[] bank, string name)
    {
        using var input = new MemoryStream(bank, 19, Get32(bank, 9) - 2, false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        byte[] table = new byte[Get32(bank, 13)]; deflate.ReadExactly(table);
        int at = 4;
        for (int i = 0; i < Get32(table, 0); i++)
        {
            int length = Get32(table, at); at += 4;
            string found = Encoding.UTF8.GetString(table, at, length - 1); at += length;
            int offset = Get32(table, at), size = Get32(table, at + 4); at += 8;
            if (found == name) return bank.AsSpan(Get32(bank, 0) + offset, size).ToArray();
        }
        throw new InvalidDataException("Test resource not found: " + name);
    }
    private static int Get32(byte[] data, int at) =>
        (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];
    private static void Put32(byte[] data, int at, int value)
    {
        data[at] = (byte)(value >> 24); data[at + 1] = (byte)(value >> 16);
        data[at + 2] = (byte)(value >> 8); data[at + 3] = (byte)value;
    }
    private static void Write32(Stream stream, int value)
    { byte[] data = new byte[4]; Put32(data, 0, value); stream.Write(data); }
}
