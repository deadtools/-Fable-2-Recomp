using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Fable2Launcher;

/// <summary>
/// Fable II GOTY (original user-provided GUI bank): 256x256 ABYX atlas.
/// The first two 64px rows contain the A/B/Y/X face-button artwork.
/// The bottom half contains independent UI mask/hold elements and MUST NOT
/// be overwritten by additional face buttons. The original alpha channel
/// (all 65536 pixels) and the bottom-half ARGB pixels remain unchanged.
/// Xbox guest input mapping is untouched; only GameFace graphics change.
/// </summary>
internal static class DualShockAtlasArtwork
{
    private const int AtlasArgbBytes = 256 * 256 * 4;
    private const string AtlasSha256 = "2E1173A3833420F91F9AE38B815F011CE7D59DF4EB80783CA9F89DA480327426";
    private const string OriginalAtlasEntrySha256 = "7F06C675015B69BDDF5B3EB22C1A4A6B80610E36A3D65EF821539277D7C34281";
    // Known v9/v11 repeated-icon replacement from this exact game build;
    // repair that bank too if an older launcher saved it as the backup.
    private const string OldPatchedAtlasEntrySha256 = "2F76765F0578EB9B4D450852B0D7D47C426F1E29AD4FE00FEF5BBABE1EE9FD09";

    private static readonly string CompressedBase64 =
        "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" +
        "qFChQoUKFSpUqERN/j94qLsj";

    internal static bool TryGetReplacement(byte[] bank, int entryStart, int entrySize,
        out byte[] logicalArgb, out string report)
    {
        logicalArgb = Array.Empty<byte>();
        report = "abyx: source atlas unknown, left unchanged to protect HOLD animation";
        if (bank is null || entryStart < 0 || entrySize <= 0 || entryStart > bank.Length - entrySize)
            return false;

        string actual = Convert.ToHexString(SHA256.HashData(bank.AsSpan(entryStart, entrySize)));
        // v13: Earlier launcher versions can leave an intermediate atlas in the
        // one-time .xbox-prompts-backup. Exact hashes from v12 are preferred,
        // but a validated layout is also safe to replace with the user-supplied,
        // checksum-protected original PS atlas. The game's action mapping is
        // never changed. Unsupported layouts remain unmodified.
        bool hashKnown = actual.Equals(OriginalAtlasEntrySha256, StringComparison.Ordinal) ||
                         actual.Equals(OldPatchedAtlasEntrySha256, StringComparison.Ordinal);
        bool knownCompressedLayout = entrySize == 16212 &&
            ReadBe32(bank, entryStart) == 1 &&
            ReadBe32(bank, entryStart + 4) == 48 &&
            ReadBe32(bank, entryStart + 12) == 3 &&
            ReadBe32(bank, entryStart + 16) == 11748 &&
            ReadBe32(bank, entryStart + 20) == 4464;
        bool knownRawLayout = entrySize == AtlasArgbBytes + 4 &&
            ReadBe32(bank, entryStart) == (uint)AtlasArgbBytes;
        if (!hashKnown && !knownCompressedLayout && !knownRawLayout)
        {
            report = $"abyx: unsupported source sha256={actual}, size={entrySize}; original retained";
            return false;
        }

        try
        {
            logicalArgb = GetLogicalArgb();
            string mode = hashKnown ? "verified original/legacy hash" :
                knownCompressedLayout ? "compatible original compressed layout" :
                "compatible raw atlas from an earlier patch";
            report = "abyx: PS face icons replaced in 2 rows; original HOLD masks, " +
                     $"bottom-half pixels and all alpha preserved ({mode}; source sha256={actual})";
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException)
        {
            logicalArgb = Array.Empty<byte>();
            report = "abyx: embedded atlas validation failed; original retained: " + ex.Message;
            return false;
        }
    }


    private static uint ReadBe32(byte[] bytes, int offset) =>
        ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) |
        ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];

    internal static byte[] GetLogicalArgb()
    {
        byte[] zipped = Convert.FromBase64String(CompressedBase64);
        using var compressed = new MemoryStream(zipped, writable: false);
        using var decompressor = new ZLibStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream(AtlasArgbBytes);
        decompressor.CopyTo(output);
        byte[] result = output.ToArray();
        if (result.Length != AtlasArgbBytes)
            throw new InvalidDataException($"Unexpected ABYX image size: {result.Length}");
        if (!Convert.ToHexString(SHA256.HashData(result)).Equals(AtlasSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Embedded ABYX image checksum mismatch");
        return result;
    }
}
