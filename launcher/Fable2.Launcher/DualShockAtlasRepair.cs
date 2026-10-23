using System;
using System.Collections.Generic;

namespace Fable2Launcher;

/// <summary>
/// Non-destructive ABYX atlas repair for the original Fable II GUI bank.
/// The atlas is NOT a 4x4 collection of identical full-size PlayStation buttons:
/// GameFace also samples its animation / progress regions for HOLD prompts.
/// Keep the original animation artwork intact; replace only recognizable button
/// glyph interiors.  Unsupported or ambiguous content is left byte-identical.
/// </summary>
internal static class DualShockAtlasRepair
{
    private const int Width = 256;
    private const int Height = 256;
    private const int CellSize = 64;
    private const int RawSize = Width * Height * 4;
    private const int Bc1Size = Width * Height / 2;
    private const int Bc23Size = Width * Height;

    private enum Icon { XboxX, Cross, Circle, Triangle, Square }
    private enum Compression { Bc1, Bc2, Bc3 }

    private sealed record Candidate(byte[] Argb, string Description, long Smoothness, int Buttons);

    internal static bool TryRepair(byte[] originalBank, int entryStart, int entrySize,
        out byte[] repairedArgb, out string report)
    {
        repairedArgb = Array.Empty<byte>();
        report = "abyx: original animation preserved; texture not altered";
        if (entryStart < 0 || entrySize < 8 || entryStart > originalBank.Length - entrySize)
        {
            report = "abyx: entry boundaries invalid, original kept";
            return false;
        }

        int declaredBytes = (int)ReadBe32(originalBank, entryStart);
        int available = entrySize - 4;
        if (declaredBytes < 1 || declaredBytes > available)
        {
            report = "abyx: unsupported texture payload length, original kept";
            return false;
        }
        int start = entryStart + 4;
        var candidates = new List<Candidate>();

        // The first (top-level) mip is all that is needed: the replacement is
        // emitted as a single raw ARGB mip, just like the existing v9 patch.
        if (declaredBytes >= RawSize && declaredBytes <= RawSize * 3 / 2)
        {
            AddRaw(candidates, originalBank, start, tiled: false);
            AddRaw(candidates, originalBank, start, tiled: true);
        }
        else if (declaredBytes >= Bc23Size && declaredBytes <= Bc23Size * 3 / 2)
        {
            foreach (Compression mode in new[] { Compression.Bc3, Compression.Bc2 })
            foreach (bool tiled in new[] { true, false })
            foreach (bool swap16 in new[] { true, false })
                AddCompressed(candidates, originalBank, start, mode, tiled, swap16);
        }
        else if (declaredBytes >= Bc1Size && declaredBytes <= Bc1Size * 3 / 2)
        {
            foreach (bool tiled in new[] { true, false })
            foreach (bool swap16 in new[] { true, false })
                AddCompressed(candidates, originalBank, start, Compression.Bc1, tiled, swap16);
        }
        else
        {
            report = $"abyx: unsupported original size {declaredBytes}; original atlas kept";
            return false;
        }

        // Wrong BC endian/layout decodes can look smooth in transparent areas.
        // Prefer candidates with recognizable button interiors, then continuity.
        Candidate? best = null;
        foreach (Candidate candidate in candidates)
        {
            if (candidate.Buttons < 2) continue;
            if (best is null || candidate.Buttons > best.Buttons ||
                (candidate.Buttons == best.Buttons && candidate.Smoothness < best.Smoothness))
                best = candidate;
        }
        if (best is null)
        {
            report = "abyx: no reliable button interiors found in original; left untouched " +
                     "(HOLD animation protected; Xbox icons may remain in some menus)";
            return false;
        }

        // A backup made from the older v7-v9 output has all four atlas rows
        // repeated pixel-for-pixel; it cannot recover destroyed HOLD frames.
        if (IsOldGeneratedAtlas(best.Argb))
        {
            report = "abyx: backup already contains the old repeated 4x4 atlas. " +
                     "Original unmodified gui_textures.bnk backup is required to recover HOLD animation";
            return false;
        }

        repairedArgb = (byte[])best.Argb.Clone();
        int changed = ReplaceRecognizedGlyphs(repairedArgb);
        report = $"abyx: preserved original animation; {changed} glyphs patched " +
                 $"(decoded {best.Description}, {declaredBytes} original bytes)";
        return changed >= 2;
    }

    // Testable separately: only central glyph areas of recognized 64x64 regions
    // may change; everything outside the 20px central radius remains identical.
    internal static int ReplaceRecognizedGlyphs(byte[] atlasArgb)
    {
        if (atlasArgb.Length != RawSize)
            throw new ArgumentException("ABYX logical ARGB data must be 256x256", nameof(atlasArgb));

        Icon[] byColumn = { Icon.XboxX, Icon.Circle, Icon.Triangle, Icon.Square };
        int changed = 0;
        for (int row = 0; row < 4; row++)
        for (int col = 0; col < 4; col++)
        {
            int cx = col * CellSize + CellSize / 2;
            int cy = row * CellSize + CellSize / 2;
            if (!TryFindButtonBackground(atlasArgb, cx, cy, out byte backR, out byte backG, out byte backB))
                continue;

            PaintGlyphInterior(atlasArgb, cx, cy, backR, backG, backB, byColumn[col]);
            changed++;
        }
        return changed;
    }

    private static bool TryFindButtonBackground(byte[] argb, int cx, int cy,
        out byte backR, out byte backG, out byte backB)
    {
        backR = backG = backB = 0;
        // Sample the dark interior immediately outside the Xbox glyph.
        // White animated progress arcs live further out and are never touched.
        var samples = new List<(int bright, byte r, byte g, byte b)>();
        int valid = 0;
        for (int dy = -22; dy <= 22; dy++)
        for (int dx = -22; dx <= 22; dx++)
        {
            int rr = dx * dx + dy * dy;
            if (rr < 18 * 18 || rr > 22 * 22) continue;
            int i = PixelOffset(cx + dx, cy + dy);
            if (argb[i] < 160) continue;
            valid++;
            int bright = (argb[i + 1] + argb[i + 2] + argb[i + 3]) / 3;
            samples.Add((bright, argb[i + 1], argb[i + 2], argb[i + 3]));
        }
        // A hold-progress frame with a transparent centre is not a button.
        if (valid < 235) return false;
        samples.Sort(static (a, b) => a.bright.CompareTo(b.bright));
        int take = Math.Max(1, samples.Count / 3);
        int rSum = 0, gSum = 0, bSum = 0;
        for (int i = 0; i < take; i++)
        {
            rSum += samples[i].r; gSum += samples[i].g; bSum += samples[i].b;
        }
        backR = (byte)(rSum / take);
        backG = (byte)(gSum / take);
        backB = (byte)(bSum / take);
        if ((backR + backG + backB) / 3 > 160) return false;

        int opaque = 0, glyph = 0;
        for (int dy = -15; dy <= 15; dy++)
        for (int dx = -15; dx <= 15; dx++)
        {
            if (dx * dx + dy * dy > 15 * 15) continue;
            int i = PixelOffset(cx + dx, cy + dy);
            if (argb[i] < 160) continue;
            opaque++;
            int difference = Math.Abs(argb[i + 1] - backR) +
                             Math.Abs(argb[i + 2] - backG) +
                             Math.Abs(argb[i + 3] - backB);
            if (difference >= 100) glyph++;
        }
        // Require a real inner symbol, but not a fully different central frame.
        return opaque >= 420 && glyph >= 12 && glyph <= 350;
    }

    private static void PaintGlyphInterior(byte[] argb, int cx, int cy,
        byte backgroundR, byte backgroundG, byte backgroundB, Icon glyph)
    {
        (byte sr, byte sg, byte sb) = glyph switch
        {
            Icon.XboxX => ((byte)73, (byte)149, (byte)255),
            Icon.Cross => ((byte)174, (byte)184, (byte)229),
            Icon.Circle => ((byte)242, (byte)76, (byte)40),
            Icon.Triangle => ((byte)73, (byte)188, (byte)173),
            Icon.Square => ((byte)222, (byte)137, (byte)190),
            _ => ((byte)255, (byte)255, (byte)255),
        };
        for (int dy = -20; dy <= 20; dy++)
        for (int dx = -20; dx <= 20; dx++)
        {
            double radius = Math.Sqrt(dx * dx + dy * dy);
            if (radius >= 20) continue;
            int index = PixelOffset(cx + dx, cy + dy);
            if (argb[index] < 160) continue;
            double erase = radius < 17 ? 1.0 : (20 - radius) / 3.0;
            int samples = 0;
            for (int sy = 0; sy < 4; sy++)
            for (int sx = 0; sx < 4; sx++)
                if (IsGlyph(glyph, dx + (sx - 1.5) * 0.25, dy + (sy - 1.5) * 0.25)) samples++;
            double symbol = samples / 16.0;
            int r = (int)Math.Round(backgroundR * (1 - symbol) + sr * symbol);
            int g = (int)Math.Round(backgroundG * (1 - symbol) + sg * symbol);
            int b = (int)Math.Round(backgroundB * (1 - symbol) + sb * symbol);
            argb[index + 1] = Lerp(argb[index + 1], r, erase);
            argb[index + 2] = Lerp(argb[index + 2], g, erase);
            argb[index + 3] = Lerp(argb[index + 3], b, erase);
            // Original alpha is never modified. Hold masks and outer rings stay intact.
        }
    }

    private static bool IsGlyph(Icon glyph, double x, double y)
    {
        const double halfStroke = 1.55;
        return glyph switch
        {
            Icon.XboxX or Icon.Cross => Math.Min(
                SegmentDistance(x, y, -10.5, -10.5, 10.5, 10.5),
                SegmentDistance(x, y, 10.5, -10.5, -10.5, 10.5)) <= halfStroke,
            Icon.Circle => Math.Abs(Math.Sqrt(x * x + y * y) - 13.5) <= halfStroke,
            Icon.Square => Math.Min(
                Math.Min(SegmentDistance(x, y, -12, -12, 12, -12), SegmentDistance(x, y, 12, -12, 12, 12)),
                Math.Min(SegmentDistance(x, y, 12, 12, -12, 12), SegmentDistance(x, y, -12, 12, -12, -12))) <= halfStroke,
            Icon.Triangle => Math.Min(
                Math.Min(SegmentDistance(x, y, 0, -14, 13, 11.5), SegmentDistance(x, y, 13, 11.5, -13, 11.5)),
                SegmentDistance(x, y, -13, 11.5, 0, -14)) <= halfStroke,
            _ => false,
        };
    }

    private static double SegmentDistance(double x, double y, double ax, double ay, double bx, double by)
    {
        double vx = bx - ax, vy = by - ay;
        double t = Math.Clamp(((x - ax) * vx + (y - ay) * vy) / (vx * vx + vy * vy), 0, 1);
        double dx = x - (ax + t * vx), dy = y - (ay + t * vy);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static byte Lerp(int oldValue, int newValue, double t) =>
        (byte)Math.Clamp((int)Math.Round(oldValue * (1 - t) + newValue * t), 0, 255);

    private static int PixelOffset(int x, int y) => (y * Width + x) * 4;

    private static bool IsOldGeneratedAtlas(byte[] argb)
    {
        // v7-v9 emitted four pixel-identical rows of 64px cells. Genuine
        // animation data should differ; this detects unusable patched backups.
        int identicalRows = 0;
        for (int row = 1; row < 4; row++)
        {
            bool same = true;
            for (int y = 0; y < CellSize && same; y++)
            {
                int a = y * Width * 4;
                int b = (row * CellSize + y) * Width * 4;
                for (int x = 0; x < Width * 4; x++)
                    if (argb[a + x] != argb[b + x]) { same = false; break; }
            }
            if (same) identicalRows++;
        }
        return identicalRows == 3;
    }

    private static void AddRaw(List<Candidate> list, byte[] bank, int start, bool tiled)
    {
        var decoded = new byte[RawSize];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int dst = PixelOffset(x, y);
            int src = start + (tiled ? Tiled2DOffset(x, y, Width, 2) : dst);
            Buffer.BlockCopy(bank, src, decoded, dst, 4);
        }
        list.Add(new Candidate(decoded, tiled ? "ARGB/tiled" : "ARGB/linear",
            Smoothness(decoded), CountButtons(decoded)));
    }

    private static void AddCompressed(List<Candidate> list, byte[] bank, int start,
        Compression compression, bool tiled, bool swap16)
    {
        int bpb = compression == Compression.Bc1 ? 8 : 16;
        int log2 = bpb == 8 ? 3 : 4;
        const int blocks = Width / 4;
        byte[] result = new byte[RawSize];
        byte[] block = new byte[16];
        for (int by = 0; by < blocks; by++)
        for (int bx = 0; bx < blocks; bx++)
        {
            int at = start + (tiled ? Tiled2DOffset(bx, by, blocks, log2) :
                                     (by * blocks + bx) * bpb);
            for (int j = 0; j < bpb; j++)
                block[j] = bank[at + (swap16 ? j ^ 1 : j)];
            DecodeBlock(block, compression, result, bx * 4, by * 4);
        }
        list.Add(new Candidate(result,
            $"{compression}/{(tiled ? "tiled" : "linear")}/{(swap16 ? "swap16" : "native")}",
            Smoothness(result), CountButtons(result)));
    }

    private static void DecodeBlock(byte[] block, Compression compression, byte[] output, int ox, int oy)
    {
        int c = compression == Compression.Bc1 ? 0 : 8;
        int c0 = block[c] | (block[c + 1] << 8);
        int c1 = block[c + 2] | (block[c + 3] << 8);
        int[] r = new int[4], g = new int[4], b = new int[4];
        r[0] = ((c0 >> 11) & 31) * 255 / 31;
        g[0] = ((c0 >> 5) & 63) * 255 / 63;
        b[0] = (c0 & 31) * 255 / 31;
        r[1] = ((c1 >> 11) & 31) * 255 / 31;
        g[1] = ((c1 >> 5) & 63) * 255 / 63;
        b[1] = (c1 & 31) * 255 / 31;
        bool three = compression == Compression.Bc1 && c0 <= c1;
        for (int k = 0; k < 3; k++)
        {
            int[] channel = k == 0 ? r : k == 1 ? g : b;
            channel[2] = three ? (channel[0] + channel[1]) / 2 :
                (channel[0] * 2 + channel[1]) / 3;
            channel[3] = three ? 0 : (channel[0] + channel[1] * 2) / 3;
        }
        uint indices = (uint)(block[c + 4] | (block[c + 5] << 8) |
                              (block[c + 6] << 16) | (block[c + 7] << 24));

        byte[] alpha = new byte[8];
        ulong alphaIndices = 0;
        if (compression == Compression.Bc3)
        {
            alpha[0] = block[0]; alpha[1] = block[1];
            if (alpha[0] > alpha[1])
            {
                for (int j = 2; j < 8; j++)
                    alpha[j] = (byte)(((8 - j) * alpha[0] + (j - 1) * alpha[1]) / 7);
            }
            else
            {
                for (int j = 2; j < 6; j++)
                    alpha[j] = (byte)(((6 - j) * alpha[0] + (j - 1) * alpha[1]) / 5);
                alpha[6] = 0; alpha[7] = 255;
            }
            for (int j = 0; j < 6; j++)
                alphaIndices |= (ulong)block[2 + j] << (8 * j);
        }
        for (int p = 0; p < 16; p++)
        {
            int color = (int)((indices >> (p * 2)) & 3);
            byte a = compression switch
            {
                Compression.Bc1 => (byte)(three && color == 3 ? 0 : 255),
                Compression.Bc2 => (byte)(((block[p / 2] >> (4 * (p & 1))) & 15) * 17),
                Compression.Bc3 => alpha[(int)((alphaIndices >> (3 * p)) & 7)],
                _ => 255,
            };
            int i = PixelOffset(ox + p % 4, oy + p / 4);
            output[i] = a;
            output[i + 1] = (byte)r[color];
            output[i + 2] = (byte)g[color];
            output[i + 3] = (byte)b[color];
        }
    }

    private static int CountButtons(byte[] argb)
    {
        int result = 0;
        for (int row = 0; row < 4; row++)
        for (int col = 0; col < 4; col++)
            if (TryFindButtonBackground(argb, col * 64 + 32, row * 64 + 32,
                out _, out _, out _)) result++;
        return result;
    }

    private static long Smoothness(byte[] argb)
    {
        long score = 0;
        int visible = 0;
        for (int y = 0; y < Height - 1; y += 2)
        for (int x = 0; x < Width - 1; x += 2)
        {
            int p = PixelOffset(x, y);
            int right = p + 4;
            int down = p + Width * 4;
            foreach (int other in new[] { right, down })
            {
                score += Math.Abs(argb[p] - argb[other]) * 2L;
                if (argb[p] > 24 || argb[other] > 24)
                {
                    visible++;
                    for (int c = 1; c < 4; c++)
                        score += Math.Abs(argb[p + c] - argb[other + c]);
                }
            }
        }
        return visible < 200 ? long.MaxValue : score;
    }

    private static uint ReadBe32(byte[] bytes, int index) =>
        ((uint)bytes[index] << 24) | ((uint)bytes[index + 1] << 16) |
        ((uint)bytes[index + 2] << 8) | bytes[index + 3];

    // Xenos 2D block-tiled addressing; works for raw 4-byte pixels and BC blocks.
    private static int Tiled2DOffset(int x, int y, int pitch, int bytesLog2)
    {
        int outer = (((y >> 5) * (pitch >> 5) + (x >> 5)) << 6);
        int inner = (((y >> 1) & 7) << 3) | (x & 7);
        int address = (outer | inner) << bytesLog2;
        int bank = (y >> 4) & 1;
        int pipe = ((x >> 3) & 3) ^ (((y >> 3) & 1) << 1);
        return (((y & 1) << 4) | (pipe << 6) | (bank << 11)) |
               (address & 15) | (((address >> 4) & 1) << 5) |
               (((address >> 5) & 7) << 8) | ((address >> 8) << 12);
    }
}
