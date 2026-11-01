using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Fable2Launcher;

/// <summary>
/// Patches the TEN verified, static visual sprites in Fable II PubGames SWF8.
/// Does not modify ActionScript, timelines, input constants, text or game logic.
/// Original files: data/pubgames/{wizardstower,keystone,magedice}/game.swf.
/// The fingerprints below were measured in the user-supplied game files.
/// </summary>
internal static class PubGameSwfPatch
{
    internal const int IconSize = 128;
    internal const string Revision = "pubgames-prompts-v22.1";
    private const int MaxMovieBytes = 32 * 1024 * 1024;
    internal sealed record Slot(string Key, ushort Id, string Hash, int X, int Y, int Side);
    internal static readonly Slot[] Slots =
    {
        new("A", 85, "1FF689A6088D8C8498CD446C270BC117868335CF23F7C7F4E30580213B9376E4", -93, -99, 948),
        new("B", 88, "BC210B874F865589FCC97445FABA22D2B7ED2C806B8661DFEFF001F35A01A0E9", -96, -100, 949),
        new("X", 91, "9C79CB4F06B4E6044008965CD13463A9A223B824389F01F56DA471255705C451", -96, -99, 923),
        new("Y", 93, "1E8C71196C9EB7F6B4CE40A93AD5B2D08D122471DBD3032A44AEA8B1A0F7AEA2", -74, -89, 913),
        new("LS", 96, "348286E0DA1F57813B72902F954D07BA08BC8A499B9D23849748B69ACBFE69F7", -112, -120, 960),
        new("RS", 99, "58502056CDC7154083C1BF921EA746F13862CD37E7CE210B70205AD26A6F6F04", -112, -120, 960),
        new("LT", 103, "63D6198ED0A4B8E0F81D3D4F1195B857DEBE764D7A86A23EE6714223A54B2C73", -34, -66, 840),
        new("RT", 107, "8FF92052514F37AAA4C8E5776DF5E611E8D1724B86158DB623153193498CB23E", -24, -66, 840),
        new("LB", 112, "D85B13017B1979029DA9783A4DD11B6AB09305EBF3E8A1E02C0E8D669D4CA2F1", -158, -64, 839),
        new("RB", 116, "680D2A11DD429BD0FE1CAA2E7653100AC8F0D23107AE0F282A11C1FE3CF0EC91", -158, -64, 839),
    };
    internal sealed record Tag(int Code, byte[] Data, byte[] Raw);
    internal sealed record Movie(byte[] Header, bool Compressed, List<Tag> Tags);

    internal static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    internal static void ValidateOriginal(byte[] data)
    {
        Movie movie = Read(data);
        foreach (Slot slot in Slots)
        {
            Tag[] matches = movie.Tags.Where(t => t.Code == 39 && Id(t) == slot.Id).ToArray();
            if (matches.Length != 1 || Hash(matches[0].Data) != slot.Hash)
                throw new InvalidDataException($"Unsupported PubGames sprite {slot.Key} (ID {slot.Id}). " +
                    "No file was modified. Do not delete the original SWF backup.");
        }
    }

    internal static byte[] Patch(byte[] original, Func<string, byte[]> artwork)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        ValidateOriginal(original);
        Movie movie = Read(original);
        var definitions = movie.Tags.Where(t => IsDefinition(t.Code)).ToArray();
        var usedIds = new HashSet<int>();
        foreach (Tag tag in definitions)
            if (!usedIds.Add(Id(tag))) throw new InvalidDataException("Duplicate SWF character ID.");
        int nextId = usedIds.Count == 0 ? 1 : usedIds.Max() + 1;
        if (nextId + Slots.Length * 2 > 65535) throw new InvalidDataException("SWF dictionary is full.");
        var pending = Slots.ToDictionary(s => s.Id);
        var output = new List<Tag>(movie.Tags.Count + 2 * Slots.Length);
        foreach (Tag tag in movie.Tags)
        {
            if (tag.Code != 39 || !pending.Remove(Id(tag), out Slot? slot))
            {
                output.Add(tag); // Unchanged tags retain their EXACT original header and bytes.
                continue;
            }
            byte[] pixels = artwork(slot.Key);
            if (pixels is null || pixels.Length != IconSize * IconSize * 4)
                throw new InvalidDataException($"Wrong 128x128 ARGB artwork length: {slot.Key}");
            ushort bitmapId = checked((ushort)nextId++);
            ushort shapeId = checked((ushort)nextId++);
            output.Add(MakeTag(36, Bitmap(bitmapId, pixels)));
            output.Add(MakeTag(32, BitmapShape(shapeId, bitmapId, slot)));
            output.Add(MakeTag(39, StaticSprite(slot.Id, shapeId)));
        }
        if (pending.Count != 0) throw new InvalidDataException("Incomplete prompt replacement.");
        byte[] result = Write(movie with { Tags = output });
        VerifyPreserved(original, result);
        return result;
    }

    // Independently compare all original tags except the ten visual sprites.
    // Includes every DoAction/DoInitAction, frame label and display-list placement.
    internal static void VerifyPreserved(byte[] original, byte[] result)
    {
        Movie before = Read(original), after = Read(result);
        if (!before.Header.AsSpan().SequenceEqual(after.Header))
            throw new InvalidDataException("SWF movie settings changed.");
        if (before.Tags.Count + Slots.Length * 2 != after.Tags.Count)
            throw new InvalidDataException("Unexpected SWF tag count after patch.");
        int i = 0;
        foreach (Tag tag in before.Tags)
        {
            Slot? slot = tag.Code == 39 ? Slots.FirstOrDefault(s => s.Id == Id(tag)) : null;
            if (slot is null)
            {
                if (!tag.Raw.AsSpan().SequenceEqual(after.Tags[i++].Raw))
                    throw new InvalidDataException("A non-prompt SWF tag changed.");
            }
            else
            {
                Tag bitmap = after.Tags[i++], shape = after.Tags[i++], sprite = after.Tags[i++];
                if (bitmap.Code != 36 || shape.Code != 32 || sprite.Code != 39 || Id(sprite) != slot.Id)
                    throw new InvalidDataException("Bad SWF replacement sequence.");
                if (!sprite.Data.AsSpan().SequenceEqual(StaticSprite(slot.Id, Id(shape))))
                    throw new InvalidDataException("Bad SWF sprite binding.");
                if (!shape.Data.AsSpan().SequenceEqual(BitmapShape(Id(shape), Id(bitmap), slot)))
                    throw new InvalidDataException("Bad SWF bitmap placement.");
                if (bitmap.Data.Length < 8 || bitmap.Data[2] != 5 ||
                    ReadU16(bitmap.Data, 3) != IconSize || ReadU16(bitmap.Data, 5) != IconSize)
                    throw new InvalidDataException("Bad lossless bitmap header.");
                byte[] pixels = Inflate(bitmap.Data.AsSpan(7).ToArray(), IconSize * IconSize * 4);
                for (int p = 0; p < pixels.Length; p += 4)
                    if (pixels[p + 1] > pixels[p] || pixels[p + 2] > pixels[p] || pixels[p + 3] > pixels[p])
                        throw new InvalidDataException("SWF bitmap alpha is not premultiplied.");
            }
        }
    }

    internal static ushort Id(Tag tag)
    {
        if (tag.Data.Length < 2) throw new InvalidDataException("Missing SWF character ID.");
        return ReadU16(tag.Data, 0);
    }
    private static bool IsDefinition(int code) => code is
        2 or 6 or 7 or 10 or 11 or 14 or 20 or 21 or 22 or 32 or 33 or 34 or
        35 or 36 or 37 or 39 or 46 or 48 or 60 or 75 or 83 or 84 or 87 or 90 or 91;

    internal static Movie Read(byte[] file)
    {
        if (file.Length < 12 || file.Length > MaxMovieBytes || file[1] != (byte)'W' ||
            file[2] != (byte)'S' || (file[0] != (byte)'F' && file[0] != (byte)'C') || file[3] != 8)
            throw new InvalidDataException("Expected an FWS/CWS version-8 PubGames movie.");
        uint declared = ReadU32(file, 4);
        if (declared < 12 || declared > MaxMovieBytes) throw new InvalidDataException("Invalid SWF length.");
        bool compressed = file[0] == (byte)'C';
        byte[] body = compressed ? Inflate(file.AsSpan(8).ToArray(), checked((int)declared - 8)) : file.AsSpan(8).ToArray();
        if (body.Length != declared - 8) throw new InvalidDataException("Truncated SWF file.");
        int rectBits = body[0] >> 3;
        if (rectBits < 1 || rectBits > 31) throw new InvalidDataException("Invalid SWF RECT.");
        int tagStart = (5 + rectBits * 4 + 7) / 8 + 4; // RECT, frame-rate, frame-count.
        if (tagStart >= body.Length) throw new InvalidDataException("Invalid SWF movie header.");
        byte[] header = new byte[tagStart + 1];
        header[0] = file[3];
        Buffer.BlockCopy(body, 0, header, 1, tagStart);
        var tags = new List<Tag>();
        int offset = tagStart;
        bool ended = false;
        while (offset < body.Length)
        {
            if (tags.Count > 100000) throw new InvalidDataException("Too many SWF tags.");
            int start = offset;
            ushort word = ReadU16(body, offset); offset += 2;
            int code = word >> 6, length = word & 63;
            if (length == 63)
            {
                uint count = ReadU32(body, offset); offset += 4;
                if (count > MaxMovieBytes) throw new InvalidDataException("Oversized SWF tag.");
                length = (int)count;
            }
            if (length > body.Length - offset) throw new InvalidDataException("Truncated SWF tag.");
            byte[] data = body.AsSpan(offset, length).ToArray(); offset += length;
            tags.Add(new Tag(code, data, body.AsSpan(start, offset - start).ToArray()));
            if (code == 0)
            {
                if (length != 0 || offset != body.Length) throw new InvalidDataException("Bad SWF End tag/trailing data.");
                ended = true; break;
            }
        }
        if (!ended) throw new InvalidDataException("SWF End tag is missing.");
        return new Movie(header, compressed, tags);
    }

    internal static byte[] Write(Movie movie)
    {
        using var body = new MemoryStream();
        body.Write(movie.Header, 1, movie.Header.Length - 1);
        foreach (Tag tag in movie.Tags) body.Write(tag.Raw, 0, tag.Raw.Length);
        byte[] uncompressed = body.ToArray();
        if (uncompressed.Length + 8 > MaxMovieBytes) throw new InvalidDataException("Patched SWF exceeds size limit.");
        using var file = new MemoryStream(); using var writer = new BinaryWriter(file, Encoding.UTF8, true);
        writer.Write((byte)(movie.Compressed ? 'C' : 'F')); writer.Write((byte)'W'); writer.Write((byte)'S');
        writer.Write(movie.Header[0]); writer.Write(checked((uint)uncompressed.Length + 8));
        byte[] payload = movie.Compressed ? Deflate(uncompressed) : uncompressed;
        writer.Write(payload); writer.Flush(); return file.ToArray();
    }

    internal static Tag MakeTag(int code, byte[] data)
    {
        if (code < 0 || code > 1023) throw new ArgumentOutOfRangeException(nameof(code));
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write((ushort)((code << 6) | (data.Length < 63 ? data.Length : 63)));
        if (data.Length >= 63) w.Write((uint)data.Length);
        w.Write(data); w.Flush(); return new Tag(code, data, stream.ToArray());
    }

    private static byte[] Bitmap(ushort id, byte[] straightArgb)
    {
        // DefineBitsLossless2 format 5 = ZLIB of premultiplied ARGB pixels.
        byte[] premultiplied = (byte[])straightArgb.Clone();
        for (int i = 0; i < premultiplied.Length; i += 4)
            for (int c = 1; c <= 3; c++)
                premultiplied[i + c] = (byte)((straightArgb[i + c] * straightArgb[i] + 127) / 255);
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(id); w.Write((byte)5); w.Write((ushort)IconSize); w.Write((ushort)IconSize);
        w.Write(Deflate(premultiplied)); w.Flush(); return stream.ToArray();
    }

    private static byte[] BitmapShape(ushort id, ushort bitmapId, Slot slot)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(id);
        w.Write(Rect(0, slot.Side, 0, slot.Side));
        w.Write((byte)1); // One fill style, clipped smoothed bitmap.
        w.Write((byte)0x41); w.Write(bitmapId);
        int scale = checked((int)Math.Round(slot.Side * 65536.0 / IconSize));
        w.Write(Matrix(scale, scale, 0, 0));
        w.Write((byte)0); // No outlines. The PNG includes its own circular boundary.
        var bits = new BitWriter();
        bits.U(1, 4); bits.U(0, 4); // NumFillBits, NumLineBits.
        bits.U(0, 1); bits.U(5, 5); // StyleChange: MoveTo + FillStyle1.
        bits.U(1, 5); bits.S(0, 1); bits.S(0, 1); bits.U(1, 1);
        bits.Line(slot.Side, 0); bits.Line(0, slot.Side);
        bits.Line(-slot.Side, 0); bits.Line(0, -slot.Side);
        bits.U(0, 6); // EndShapeRecord.
        w.Write(bits.Bytes()); w.Flush(); return stream.ToArray();
    }

    private static byte[] StaticSprite(ushort id, ushort shapeId)
    {
        Slot slot = Slots.Single(s => s.Id == id);
        using var place = new MemoryStream(); using var p = new BinaryWriter(place);
        p.Write((byte)6); p.Write((ushort)1); p.Write(shapeId); // HasMatrix + HasCharacter, depth 1.
        p.Write(Matrix(65536, 65536, slot.X, slot.Y)); p.Flush();
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(id); w.Write((ushort)1); w.Write(MakeTag(26, place.ToArray()).Raw);
        w.Write(MakeTag(1, Array.Empty<byte>()).Raw); w.Write(MakeTag(0, Array.Empty<byte>()).Raw);
        w.Flush(); return stream.ToArray();
    }

    private static byte[] Rect(params int[] coords)
    {
        var b = new BitWriter(); int n = coords.Max(SignedBits);
        b.U(n, 5); foreach (int c in coords) b.S(c, n); return b.Bytes();
    }
    private static byte[] Matrix(int sx, int sy, int x, int y)
    {
        var b = new BitWriter(); bool hasScale = sx != 65536 || sy != 65536;
        b.U(hasScale ? 1 : 0, 1);
        if (hasScale) { int n = Math.Max(SignedBits(sx), SignedBits(sy)); b.U(n, 5); b.S(sx, n); b.S(sy, n); }
        b.U(0, 1); int t = Math.Max(SignedBits(x), SignedBits(y));
        b.U(t, 5); b.S(x, t); b.S(y, t); return b.Bytes();
    }
    private static int SignedBits(int value)
    {
        for (int n = 1; n <= 31; n++) if (value >= -(1L << (n - 1)) && value < (1L << (n - 1))) return n;
        throw new InvalidDataException("SWF integer does not fit.");
    }
    private sealed class BitWriter
    {
        private readonly List<byte> data = new(); private int used;
        internal void U(int value, int width)
        {
            if (width < 0 || width > 31) throw new ArgumentOutOfRangeException(nameof(width));
            for (int i = width - 1; i >= 0; i--)
            {
                if (used % 8 == 0) data.Add(0);
                data[^1] |= (byte)(((value >> i) & 1) << (7 - used % 8)); used++;
            }
        }
        internal void S(int value, int width) => U(value, width);
        internal void Line(int dx, int dy)
        {
            int n = Math.Max(2, Math.Max(SignedBits(dx), SignedBits(dy)));
            U(1, 1); U(1, 1); U(n - 2, 4); U(1, 1); S(dx, n); S(dy, n);
        }
        internal byte[] Bytes() => data.ToArray();
    }
    private static ushort ReadU16(byte[] b, int p)
    {
        if (p < 0 || p > b.Length - 2) throw new InvalidDataException("Truncated SWF integer.");
        return (ushort)(b[p] | (b[p + 1] << 8));
    }
    private static uint ReadU32(byte[] b, int p)
    {
        if (p < 0 || p > b.Length - 4) throw new InvalidDataException("Truncated SWF integer.");
        return (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24));
    }
    private static byte[] Deflate(byte[] data)
    {
        using var stream = new MemoryStream();
        using (var z = new ZLibStream(stream, CompressionLevel.Optimal, true)) z.Write(data, 0, data.Length);
        return stream.ToArray();
    }
    private static byte[] Inflate(byte[] compressed, int expected)
    {
        using var input = new MemoryStream(compressed, false);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        byte[] result = new byte[expected]; int offset = 0;
        while (offset < result.Length)
        {
            int n = z.Read(result, offset, result.Length - offset);
            if (n == 0) throw new InvalidDataException("Incomplete SWF zlib stream.");
            offset += n;
        }
        if (z.ReadByte() != -1) throw new InvalidDataException("SWF zlib size differs from header.");
        return result;
    }
}
