using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Fable2Launcher;

/// <summary>
/// Launcher-side PlayStation prompt replacement for Fable II.
///
/// The game keeps Xbox 360 input semantics internally. This patch only changes
/// GUI artwork. v17 covers controller icons, ABYX and the MenuHighlight badge:
///  - the standalone Controller/icon_button_*.tex textures used by HUD/prompts;
///  - Persistant/abyx.tex, the original dual-stream texture with two
///    icon rows and lower-half HOLD/UI resources, decoded offline from the
///    user-provided original GUI bank. Animation/mask pixels survive.
///
/// MenuHighlight green_top uses Frames/frames_elements.tex (a separate atlas).
/// It also replaces the known LB/RB/LT/RT artwork with L1/R1/L2/R2 artwork.
/// Original texture and header banks are kept as .xbox-prompts-backup files.
/// </summary>
public static class DualShockPromptPatcher
{
    public const string RelativeBankPath = "data/art/gui/gui_textures.bnk";
    public const string RelativeHeaderBankPath = "data/art/gui/gui_texture_headers.bnk";
    public const string BackupSuffix = ".xbox-prompts-backup";

    private const int TocHeaderOffset = 0x11;
    private const int FaceWidth = 64;
    private const int FaceHeight = 64;
    private const int FacePixelBytes = FaceWidth * FaceHeight * 4;
    private const int FaceEntryBytes = FacePixelBytes + 4;

    private enum GlyphKind { Cross, Circle, Square, Triangle }
    private enum TexelLayout { Linear, Tiled2D }

    private sealed record BankEntry(string Name, int RelativeOffset, int Size, int TocMetaOffset);
    private sealed record CompactBank(int BaseOffset, byte[] Toc, List<BankEntry> Entries);
    private sealed record TextureReplacement(int Width, int Height, byte[] LogicalArgb)
    {
        public int EntrySize => checked(4 + Width * Height * 4);
    }

    public static string Apply(string gameDirectory, bool enabled)
    {
        DualShockMenuBadgePatcher.EnsureGameClosed();
        string texturePath = FullPath(gameDirectory, RelativeBankPath);
        string headerPath = FullPath(gameDirectory, RelativeHeaderBankPath);
        string textureBackup = texturePath + BackupSuffix;
        string headerBackup = headerPath + BackupSuffix;
        string logPath = Path.Combine(AppContext.BaseDirectory, "launcher-dualshock-patch.log");

        if (!File.Exists(texturePath))
            throw new FileNotFoundException("Не найден банк интерфейсных текстур Fable II.", texturePath);
        if (!File.Exists(headerPath))
            throw new FileNotFoundException("Не найден банк заголовков интерфейсных текстур Fable II.", headerPath);

        if (!enabled)
        {
            bool restored = false;
            if (File.Exists(textureBackup)) { AtomicCopy(textureBackup, texturePath); restored = true; }
            if (File.Exists(headerBackup)) { AtomicCopy(headerBackup, headerPath); restored = true; }
            string menuRestore = DualShockMenuBadgePatcher.Apply(gameDirectory, enabled: false);
            WriteLog(logPath, (restored
                ? "DualShock OFF: original Xbox prompt banks restored."
                : "DualShock OFF: no prompt backups were present.") + Environment.NewLine +
                "MenuHighlight badge: " + menuRestore);
            return (restored
                ? "DualShock выключен: восстановлены оригинальные подсказки Xbox 360."
                : "DualShock выключен: резервные копии подсказок не найдены.") +
                " MenuHighlight: " + menuRestore;
        }

        // Always patch the currently installed banks, not the backup copies.
        // Keep other installed texture changes, including the native v17 badge.
        // Backups are created once for restoring the initial Xbox prompt state.
        if (!File.Exists(textureBackup)) File.Copy(texturePath, textureBackup, overwrite: false);
        if (!File.Exists(headerBackup)) File.Copy(headerPath, headerBackup, overwrite: false);

        // When coming from keyboard mode, restore the one special MenuHighlight
        // resource to native format before calling the v17 PS badge patcher.
        RestoreNativeBadgeIfKeyboard(texturePath, headerPath, headerBackup);
        byte[] originalTextures = File.ReadAllBytes(texturePath);
        byte[] originalHeaders = File.ReadAllBytes(headerPath);
        Dictionary<string, TextureReplacement> replacements = BuildReplacementSet();

        byte[] patchedTextures = RepackTextureBank(originalTextures, replacements,
            out Dictionary<string, TextureReplacement> patchedEntries, out string atlasReport);
        byte[] patchedHeaders = PatchTextureHeaderBank(originalHeaders, patchedEntries, out int headerCount);
        List<string> patchedNames = patchedEntries.Keys.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase).ToList();

        // Do not touch the live files until both banks have been generated and
        // validated in memory.
        ValidatePatchedBanks(patchedTextures, patchedHeaders, replacements, patchedEntries);
        // Verify the repacked ABYX prompt atlas. It is NOT the MenuHighlight badge.
        string pauseAReport = VerifyPromptAtlas(patchedTextures, patchedEntries);
        AtomicWrite(texturePath, patchedTextures);
        AtomicWrite(headerPath, patchedHeaders);
        // Confirmed selector material: MenuHighlight.green_top -> frames_elements.
        // Do not rewrite FAC/BGF or any controller input binding.
        string menuBadgeReport = DualShockMenuBadgePatcher.Apply(gameDirectory, enabled: true);

        var sb = new StringBuilder();
        sb.AppendLine("Fable II DualShock prompts v17 / native MenuHighlight badge / preserved HOLD resources");
        sb.AppendLine("Texture source: " + texturePath);
        sb.AppendLine("Header source: " + headerPath);
        sb.AppendLine($"Patched textures: {patchedNames.Count}; patched headers: {headerCount}");
        foreach (string name in patchedNames) sb.AppendLine("  " + name);
        sb.AppendLine("Menu atlas: " + atlasReport);
        sb.AppendLine("ABYX prompt atlas: " + pauseAReport);
        sb.AppendLine("MenuHighlight badge: " + menuBadgeReport);
        sb.AppendLine("A->Cross, B->Circle, X->Square, Y->Triangle; LB->L1, RB->R1, LT->L2, RT->R2");
        WriteLog(logPath, sb.ToString());

        bool menuAtlas = patchedNames.Any(n => n.EndsWith("\\Persistant\\abyx.tex", StringComparison.OrdinalIgnoreCase));
        int lr = patchedNames.Count(n => IsShoulderOrTrigger(n));
        return $"DualShock включён (v17): заменено {patchedNames.Count} GUI-текстур " +
               $"(атлас проверен: {(menuAtlas ? "да" : "нет — см. лог")}, " +
               $"значок MenuHighlight: {menuBadgeReport}, L1/L2/R1/R2: {lr}).";
    }

    /// <summary>
    /// Third display mode. The game still sees Xbox guest actions; the UI artwork
    /// shows the host keyboard/mouse key actually bound to each guest button.
    /// The full 512px frame atlas is repacked as raw ARGB only for the verified
    /// original/v17 game resource, preserving every other menu element.
    /// </summary>
    public static string ApplyKeyboardMouse(string gameDirectory, string keyboardGamepadMap)
    {
        DualShockMenuBadgePatcher.EnsureGameClosed();
        string texturePath = FullPath(gameDirectory, RelativeBankPath);
        string headerPath = FullPath(gameDirectory, RelativeHeaderBankPath);
        if (!File.Exists(texturePath) || !File.Exists(headerPath))
            throw new FileNotFoundException("Fable II GUI texture/header banks are missing.");
        string textureBackup = texturePath + BackupSuffix;
        string headerBackup = headerPath + BackupSuffix;
        if (!File.Exists(textureBackup)) File.Copy(texturePath, textureBackup, false);
        if (!File.Exists(headerBackup)) File.Copy(headerPath, headerBackup, false);

        byte[] oldTextures = File.ReadAllBytes(texturePath);
        byte[] oldHeaders = File.ReadAllBytes(headerPath);
        string cachePath = texturePath + ".keyboard-prompts-cache.txt";
        string mapHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("keyboard-prompts-1.6.2-menu-a-no-green-v6\n" + keyboardGamepadMap)));
        string oldTextureHash = Convert.ToHexString(SHA256.HashData(oldTextures));
        string oldHeaderHash = Convert.ToHexString(SHA256.HashData(oldHeaders));
        if (File.Exists(cachePath) &&
            File.ReadAllText(cachePath).Trim() == mapHash + "\n" + oldTextureHash + "\n" + oldHeaderHash)
            return "Keyboard/Mouse prompts are current (cached); no bank changes required.";
        var bindings = KeyboardMousePromptArtwork.ResolveBindings(keyboardGamepadMap);
        Dictionary<string, TextureReplacement> replacements = BuildKeyboardReplacementSet(bindings);
        CheckMenuBadgeSource(oldTextures, texturePath);
        byte[] keyboardAtlas = KeyboardMousePromptArtwork.CreateAtlas(bindings);
        byte[] newTextures = RepackTextureBank(oldTextures, replacements,
            out Dictionary<string, TextureReplacement> replaced, out string atlasReport, keyboardAtlas);
        byte[] newHeaders = PatchTextureHeaderBank(oldHeaders, replaced, out int headerCount);
        ValidatePatchedBanks(newTextures, newHeaders, replacements, replaced);
        if (!replaced.ContainsKey(KeyboardMousePromptArtwork.BadgeResourceName) ||
            !replaced.ContainsKey(@"Art\GUI\Persistant\abyx.tex"))
            throw new InvalidDataException("Keyboard prompts require both the MenuHighlight badge and ABYX atlas.");
        // The player's minigame scan explicitly lists these two GameFace
        // controller resources. Do not silently call the mini-game compatible
        // if either one was absent from the actual game bank after repacking.
        bool faceA = replaced.ContainsKey(@"Art\GUI\Controller\icon_button_a.tex");
        bool faceB = replaced.ContainsKey(@"Art\GUI\Controller\icon_button_b.tex");
        if (!faceA || !faceB)
            throw new InvalidDataException("Keyboard/Mouse GameFace A/B resources are missing. " +
                "Patch was not installed; verify your GUI banks. A=" + faceA + ", B=" + faceB);


        // The alpha/mask portion of ABYX is unmodified; verify the actual BNK data.
        CompactBank check = ReadCompactBank(newTextures);
        BankEntry? atlas = check.Entries.FirstOrDefault(e => IsAbyxAtlas(e.Name));
        if (atlas is null || atlas.Size != keyboardAtlas.Length + 4)
            throw new InvalidDataException("The ABYX keyboard atlas failed verification.");
        var badge = check.Entries.FirstOrDefault(e => e.Name.Equals(
            KeyboardMousePromptArtwork.BadgeResourceName, StringComparison.OrdinalIgnoreCase));
        if (badge is null) throw new InvalidDataException("Missing MenuHighlight badge after rebuild.");
        byte[] badgeEntry = newTextures.AsSpan(check.BaseOffset + badge.RelativeOffset, badge.Size).ToArray();

        // Save the recovery marker before mutating either bank. A later failed
        // commit can roll back the banks without leaving an unrecognized RAW badge.
        string hashPath = texturePath + ".keyboard-badge.sha256";
        File.WriteAllText(hashPath, Convert.ToHexString(SHA256.HashData(badgeEntry)), new UTF8Encoding(false));
        if (!oldTextures.AsSpan().SequenceEqual(newTextures) || !oldHeaders.AsSpan().SequenceEqual(newHeaders))
            CommitBankPair(texturePath, headerPath, oldTextures, oldHeaders, newTextures, newHeaders);
        // The next launch reuses the prepared artwork only if the keyboard map
        // AND both complete bank hashes still match this successful install.
        File.WriteAllText(cachePath, mapHash + "\n" +
            Convert.ToHexString(SHA256.HashData(newTextures)) + "\n" +
            Convert.ToHexString(SHA256.HashData(newHeaders)), new UTF8Encoding(false));

        string message = $"Keyboard/Mouse prompts: {replaced.Count} UI textures, {headerCount} headers; " +
            $"ABYX HOLD masks preserved; MenuHighlight A={KeyboardMousePromptArtwork.Label(bindings, "A")}, " +
            $"B={KeyboardMousePromptArtwork.Label(bindings, "B")}. " +
            "GameFace A/B texture replacements verified in the GUI bank. " +
            "Mouse art LMB/MMB/RMB is embedded in the launcher. " +
            "Guest actions and keyboard mappings are unchanged.";
        WriteLog(Path.Combine(AppContext.BaseDirectory, "launcher-keyboard-prompts.log"),
            message + Environment.NewLine + atlasReport + Environment.NewLine +
            "Bindings: " + keyboardGamepadMap + Environment.NewLine + "Texture bank: " + texturePath);
        return message;
    }

    private static Dictionary<string, TextureReplacement> BuildKeyboardReplacementSet(
        IReadOnlyDictionary<string, string> bindings)
    {
        var result = new Dictionary<string, TextureReplacement>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string guest, int size) => result[path] =
            new TextureReplacement(size, size,
                KeyboardMousePromptArtwork.RenderCircularKey(KeyboardMousePromptArtwork.Label(bindings, guest), size));
        Add(@"Art\GUI\Controller\icon_button_a.tex", "A", 64);
        Add(@"Art\GUI\Controller\icon_button_b.tex", "B", 64);
        Add(@"Art\GUI\Controller\icon_button_x.tex", "X", 64);
        Add(@"Art\GUI\Controller\icon_button_y.tex", "Y", 64);
        Add(@"Art\GUI\Controller\icon_button_lt.tex", "LT", 64);
        Add(@"Art\GUI\Controller\LeftButton.tex", "LB", 64);
        Add(@"Art\GUI\Controller\RightButton.tex", "RB", 64);
        Add(@"Art\GUI\Controller\LeftTrigger.tex", "LT", 64);
        Add(@"Art\GUI\Controller\RightTrigger.tex", "RT", 64);
        Add(@"Art\GUI\Controller\24x24\LeftButton.tex", "LB", 32);
        Add(@"Art\GUI\Controller\24x24\RightButton.tex", "RB", 32);
        Add(@"Art\GUI\Controller\24x24\LeftTrigger.tex", "LT", 32);
        Add(@"Art\GUI\Controller\24x24\RightTrigger.tex", "RT", 32);
        Add(@"Art\GUI\Controller\start.tex", "Pause", 64);
        Add(@"Art\GUI\Controller\back.tex", "Select", 64);
        Add(@"Art\GUI\Controller\24x24\Start.tex", "Pause", 32);
        Add(@"Art\GUI\Controller\24x24\Back.tex", "Select", 32);
        // Directional controls use the same guest input mapping as ABXY.
        void AddDirectional(string stem, string guest, int size) =>
            Add(@"Art\GUI\Controller\" + stem + ".tex", guest, size);
        AddDirectional("LeftUp", "StickUp", 64);
        AddDirectional("LeftDown", "StickDown", 64);
        AddDirectional("LeftLeft", "StickLeft", 64);
        AddDirectional("LeftRight", "StickRight", 64);
        AddDirectional(@"24x24\LeftUp", "StickUp", 32);
        AddDirectional(@"24x24\LeftDown", "StickDown", 32);
        AddDirectional(@"24x24\LeftLeft", "StickLeft", 32);
        AddDirectional(@"24x24\LeftRight", "StickRight", 32);
        // In mouse-look mode the right stick is the mouse, not an Xbox icon.
        void AddLiteral(string stem, string text, int size) =>
            result[@"Art\GUI\Controller\" + stem + ".tex"] =
                new TextureReplacement(size, size, KeyboardMousePromptArtwork.RenderCircularKey(text, size));
        AddLiteral("Left", "MOVE", 64);
        AddLiteral("Right", "MOUSE", 64);
        AddLiteral("DPad", "↑↓", 64);
        AddLiteral(@"24x24\DPad", "↑↓", 32);
        AddLiteral(@"24x24\Left", "MOVE", 32);
        AddLiteral(@"24x24\Right", "MOUSE", 32);
        AddLiteral("RightUp", "M↑", 64);
        AddLiteral("RightDown", "M↓", 32);
        AddLiteral("RightLeft", "M←", 64);
        AddLiteral("RightRight", "M→", 64);
        AddLiteral(@"24x24\RightUp", "M↑", 32);
        AddLiteral(@"24x24\RightDown", "M↓", 32);
        AddLiteral(@"24x24\RightLeft", "M←", 32);
        AddLiteral(@"24x24\RightRight", "M→", 32);
        result[KeyboardMousePromptArtwork.BadgeResourceName] =
            new TextureReplacement(512, 512, KeyboardMousePromptArtwork.CreateMenuBadge(bindings));
        return result;
    }

    private static void CheckMenuBadgeSource(byte[] textures, string texturePath)
    {
        CompactBank bank = ReadCompactBank(textures);
        var entry = bank.Entries.FirstOrDefault(e => e.Name.Equals(
            KeyboardMousePromptArtwork.BadgeResourceName, StringComparison.OrdinalIgnoreCase));
        if (entry is null) throw new InvalidDataException("Missing Frames/frames_elements.tex in game bank.");
        byte[] bytes = textures.AsSpan(bank.BaseOffset + entry.RelativeOffset, entry.Size).ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (hash == DualShockMenuBadgeArtwork.OriginalSha256 ||
            hash == DualShockMenuBadgeArtwork.PatchedSha256) return;
        string marker = texturePath + ".keyboard-badge.sha256";
        if (entry.Size == 512 * 512 * 4 + 4 && File.Exists(marker) &&
            hash.Equals(File.ReadAllText(marker).Trim(), StringComparison.OrdinalIgnoreCase)) return;

        // v10 recovery: older launcher builds could leave the raw keyboard
        // MenuHighlight entry in place while the small marker file was absent
        // (for example after a diagnostic/recovery cycle).  Do not reject our
        // own texture in that case.  First prove that the complete GUI bank is
        // exactly the bank recorded by the keyboard-prompt cache; then recreate
        // the marker from the current entry.
        string cachePath = texturePath + ".keyboard-prompts-cache.txt";
        if (entry.Size == 512 * 512 * 4 + 4 && File.Exists(cachePath))
        {
            try
            {
                string[] cache = File.ReadAllLines(cachePath);
                string currentBankHash = Convert.ToHexString(SHA256.HashData(textures));
                if (cache.Length >= 2 &&
                    currentBankHash.Equals(cache[1].Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(marker, hash, new UTF8Encoding(false));
                    return;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Compatibility with the v6-v9 MenuHighlight texture observed in the
        // user's installed game.  This exact hash is our generated E badge, not
        // an arbitrary third-party texture.  Recreate the missing marker once.
        const string LegacyKeyboardBadgeSha256 =
            "CDDDA0D26B1A68AF03AE943225A23F731310321E8270D9A79AEB0D3C0F03B944";
        if (entry.Size == 512 * 512 * 4 + 4 &&
            hash.Equals(LegacyKeyboardBadgeSha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.WriteAllText(marker, hash, new UTF8Encoding(false)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return;
        }

        throw new InvalidDataException("Unrecognized MenuHighlight texture. Keyboard prompts were NOT installed " +
            "to avoid overwriting another modification. SHA256=" + hash);
    }

    private static void RestoreNativeBadgeIfKeyboard(string texturePath, string headerPath, string originalHeaderBackup)
    {
        if (!File.Exists(texturePath + ".keyboard-badge.sha256")) return;
        byte[] textures = File.ReadAllBytes(texturePath);
        CompactBank bank = ReadCompactBank(textures);
        var entry = bank.Entries.FirstOrDefault(e => e.Name.Equals(
            KeyboardMousePromptArtwork.BadgeResourceName, StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Size != 512 * 512 * 4 + 4) return;
        byte[] currentEntry = textures.AsSpan(bank.BaseOffset + entry.RelativeOffset, entry.Size).ToArray();
        string marker = File.ReadAllText(texturePath + ".keyboard-badge.sha256").Trim();
        if (!Convert.ToHexString(SHA256.HashData(currentEntry)).Equals(marker, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("MenuHighlight badge was modified externally. Will not overwrite it.");
        if (!File.Exists(originalHeaderBackup))
            throw new FileNotFoundException("Original GUI header backup is required to restore PlayStation badge.");
        byte[] headers = File.ReadAllBytes(headerPath);
        byte[] backupHeaders = File.ReadAllBytes(originalHeaderBackup);
        CompactBank hdr = ReadCompactBank(headers), backup = ReadCompactBank(backupHeaders);
        var targetHeader = hdr.Entries.First(e => e.Name.Equals(
            KeyboardMousePromptArtwork.BadgeResourceName, StringComparison.OrdinalIgnoreCase));
        var sourceHeader = backup.Entries.First(e => e.Name.Equals(
            KeyboardMousePromptArtwork.BadgeResourceName, StringComparison.OrdinalIgnoreCase));
        if (targetHeader.Size != sourceHeader.Size || sourceHeader.Size < 84)
            throw new InvalidDataException("Frame header entry layout mismatch.");
        int sourceAt = backup.BaseOffset + sourceHeader.RelativeOffset;
        if (ReadBe32(backupHeaders, sourceAt + 24) == 2)
            throw new InvalidDataException("Saved native frame header is unexpectedly raw; refusing restore.");
        byte[] restoredHeaders = (byte[])headers.Clone();
        int destinationAt = hdr.BaseOffset + targetHeader.RelativeOffset;
        Buffer.BlockCopy(backupHeaders, sourceAt, restoredHeaders,
            destinationAt, sourceHeader.Size);
        // Backups can have been created while v17 PlayStation prompts were
        // already active; their frame header then has the patched entry size.
        // We restore the original 75 KB native entry below, so fix its length.
        WriteBe32(restoredHeaders, destinationAt + 4,
            (uint)DualShockMenuBadgeArtwork.OriginalSize);
        // Replace just one texture resource with the embedded, checksum-verified
        // original native asset. The v17 patcher then makes the PlayStation badge.
        byte[] restoredTextures = RepackSingleNativeEntry(textures,
            KeyboardMousePromptArtwork.BadgeResourceName, DualShockMenuBadgeArtwork.Get(false));
        CommitBankPair(texturePath, headerPath, textures, headers, restoredTextures, restoredHeaders);
    }

    private static byte[] RepackSingleNativeEntry(byte[] original, string target, byte[] nativeEntry)
    {
        CompactBank doc = ReadCompactBank(original);
        byte[] toc = (byte[])doc.Toc.Clone();
        var sorted = doc.Entries.OrderBy(e => e.RelativeOffset).ToList();
        using var data = new MemoryStream(original.Length - doc.BaseOffset);
        int previous = 0;
        foreach (BankEntry entry in sorted)
        {
            int gap = entry.RelativeOffset - previous;
            if (gap < 0) throw new InvalidDataException("Overlapping GUI bank entries.");
            if (gap > 0) data.Write(original, doc.BaseOffset + previous, gap);
            int offset = checked((int)data.Position);
            bool match = entry.Name.Equals(target, StringComparison.OrdinalIgnoreCase);
            byte[] blob = match ? nativeEntry : original.AsSpan(doc.BaseOffset + entry.RelativeOffset, entry.Size).ToArray();
            data.Write(blob);
            WriteBe32(toc, entry.TocMetaOffset, (uint)offset);
            WriteBe32(toc, entry.TocMetaOffset + 4, (uint)blob.Length);
            previous = entry.RelativeOffset + entry.Size;
        }
        if (previous < original.Length - doc.BaseOffset)
            data.Write(original, doc.BaseOffset + previous, original.Length - doc.BaseOffset - previous);
        byte[] packed = CompressTocWithoutAdler(toc);
        if (packed.Length + TocHeaderOffset > doc.BaseOffset)
            throw new InvalidDataException("TOC exceeds GUI bank header area.");
        byte[] content = data.ToArray();
        byte[] result = new byte[doc.BaseOffset + content.Length];
        Buffer.BlockCopy(original, 0, result, 0, doc.BaseOffset);
        Array.Clear(result, TocHeaderOffset, doc.BaseOffset - TocHeaderOffset);
        WriteBe32(result, 0x09, (uint)packed.Length);
        WriteBe32(result, 0x0D, (uint)toc.Length);
        Buffer.BlockCopy(packed, 0, result, TocHeaderOffset, packed.Length);
        Buffer.BlockCopy(content, 0, result, doc.BaseOffset, content.Length);
        return result;
    }

    private static void CommitBankPair(string texturePath, string headerPath,
        byte[] oldTextures, byte[] oldHeaders, byte[] newTextures, byte[] newHeaders)
    {
        try
        {
            AtomicWrite(texturePath, newTextures);
            AtomicWrite(headerPath, newHeaders);
        }
        catch
        {
            try { AtomicWrite(texturePath, oldTextures); AtomicWrite(headerPath, oldHeaders); }
            catch { /* The original .xbox-prompts-backup files are also available. */ }
            throw;
        }
    }

    public static bool RestoreIfPresent(string gameDirectory)
    {
        DualShockMenuBadgePatcher.EnsureGameClosed();
        bool restored = false;
        string texturePath = FullPath(gameDirectory, RelativeBankPath);
        string headerPath = FullPath(gameDirectory, RelativeHeaderBankPath);
        if (File.Exists(texturePath + BackupSuffix))
        {
            AtomicCopy(texturePath + BackupSuffix, texturePath);
            restored = true;
        }
        if (File.Exists(headerPath + BackupSuffix))
        {
            AtomicCopy(headerPath + BackupSuffix, headerPath);
            restored = true;
        }
        if (File.Exists(texturePath) && File.Exists(headerPath))
            DualShockMenuBadgePatcher.Apply(gameDirectory, enabled: false);
        return restored;
    }

    /// <summary>
    /// Backward-compatible test helper used by the existing launcher regression
    /// fixture. It only patches fixed-size A/B/X/Y entries in-place.
    /// Production Apply() uses the v12 two-bank repacker above.
    /// </summary>
    public static int PatchBank(byte[] bank)
    {
        if (bank is null) throw new ArgumentNullException(nameof(bank));
        CompactBank doc = ReadCompactBank(bank);
        int count = 0;
        foreach (BankEntry entry in doc.Entries)
        {
            GlyphKind? glyph = MapFaceEntry(entry.Name);
            if (glyph is null || entry.Size != FaceEntryBytes) continue;
            int absolute = checked(doc.BaseOffset + entry.RelativeOffset);
            if (!TryReadPixelByteCount(bank, absolute, out int pixelBytes) || pixelBytes != FacePixelBytes) continue;
            TexelLayout layout = DetectLayout(bank, absolute + 4, FaceWidth, FaceHeight);
            WriteLogicalTexture(bank, absolute + 4, RenderPlayStationButton(glyph.Value), FaceWidth, FaceHeight, layout);
            count++;
        }
        return count;
    }

    private static Dictionary<string, TextureReplacement> BuildReplacementSet()
    {
        var r = new Dictionary<string, TextureReplacement>(StringComparer.OrdinalIgnoreCase)
        {
            [@"Art\GUI\Controller\icon_button_a.tex"] = new(64, 64, RenderPlayStationButton(GlyphKind.Cross)),
            [@"Art\GUI\Controller\icon_button_b.tex"] = new(64, 64, RenderPlayStationButton(GlyphKind.Circle)),
            [@"Art\GUI\Controller\icon_button_x.tex"] = new(64, 64, RenderPlayStationButton(GlyphKind.Square)),
            [@"Art\GUI\Controller\icon_button_y.tex"] = new(64, 64, RenderPlayStationButton(GlyphKind.Triangle)),
            // The original animated abyx atlas is patched separately below.
            // NEVER replace its animation frames with a 4x4 grid of full buttons.

            [@"Art\GUI\Controller\LeftButton.tex"] = new(64, 64, DualShockPromptArtwork.Get("L1", 64)),
            [@"Art\GUI\Controller\RightButton.tex"] = new(64, 64, DualShockPromptArtwork.Get("R1", 64)),
            [@"Art\GUI\Controller\LeftTrigger.tex"] = new(64, 64, DualShockPromptArtwork.Get("L2", 64)),
            [@"Art\GUI\Controller\RightTrigger.tex"] = new(64, 64, DualShockPromptArtwork.Get("R2", 64)),
            [@"Art\GUI\Controller\24x24\LeftButton.tex"] = new(32, 32, DualShockPromptArtwork.Get("L1", 32)),
            [@"Art\GUI\Controller\24x24\RightButton.tex"] = new(32, 32, DualShockPromptArtwork.Get("R1", 32)),
            [@"Art\GUI\Controller\24x24\LeftTrigger.tex"] = new(32, 32, DualShockPromptArtwork.Get("L2", 32)),
            [@"Art\GUI\Controller\24x24\RightTrigger.tex"] = new(32, 32, DualShockPromptArtwork.Get("R2", 32)),
            // A separate LT texture is referenced by some HUD prompts.
            [@"Art\GUI\Controller\icon_button_lt.tex"] = new(64, 64, DualShockPromptArtwork.Get("L2", 64)),
        };
        return r;
    }

    private static byte[] RepackTextureBank(
        byte[] original,
        IReadOnlyDictionary<string, TextureReplacement> replacements,
        out Dictionary<string, TextureReplacement> patchedEntries, out string atlasReport,
        byte[]? keyboardAtlas = null)
    {
        CompactBank doc = ReadCompactBank(original);
        byte[] toc = (byte[])doc.Toc.Clone();
        List<BankEntry> sorted = doc.Entries.OrderBy(e => e.RelativeOffset).ToList();
        patchedEntries = new Dictionary<string, TextureReplacement>(StringComparer.OrdinalIgnoreCase);
        atlasReport = "Persistant/abyx.tex not found — original atlas untouched";

        using var data = new MemoryStream(original.Length - doc.BaseOffset + 512 * 1024);
        int previousOriginalEnd = 0;
        foreach (BankEntry entry in sorted)
        {
            if (entry.RelativeOffset < previousOriginalEnd)
                throw new InvalidDataException("Перекрывающиеся записи в gui_textures.bnk.");

            int gap = entry.RelativeOffset - previousOriginalEnd;
            if (gap > 0)
                data.Write(original, doc.BaseOffset + previousOriginalEnd, gap);

            int newRelative = checked((int)data.Position);
            // Actual atlas from the user game: only two rows of buttons.
            // Original HOLD/UI masks in the lower half must stay unchanged.
            // Validated offline decode and complete alpha preservation.
            TextureReplacement? atlasReplacement = null;
            if (IsAbyxAtlas(entry.Name))
            {
                if (keyboardAtlas is not null)
                {
                    atlasReplacement = new TextureReplacement(256, 256, keyboardAtlas);
                    atlasReport = "Keyboard atlas generated from the original HOLD-safe 256px base; " +
                        "two 64px icon rows replaced, lower half preserved.";
                }
                else
                {
                    int atlasStart = checked(doc.BaseOffset + entry.RelativeOffset);
                    if (DualShockAtlasArtwork.TryGetReplacement(original, atlasStart, entry.Size,
                        out byte[] repairedPixels, out string diagnosis))
                        atlasReplacement = new TextureReplacement(256, 256, repairedPixels);
                    atlasReport = diagnosis;
                }
            }
            bool isRegularReplacement = TryResolveReplacement(entry.Name, replacements,
                out TextureReplacement? regularReplacement);
            if (atlasReplacement is not null || isRegularReplacement)
            {
                TextureReplacement replacement = atlasReplacement ?? regularReplacement!;
                byte[] payload = CreateRawTextureEntry(replacement);
                data.Write(payload, 0, payload.Length);
                WriteBe32(toc, entry.TocMetaOffset, checked((uint)newRelative));
                WriteBe32(toc, entry.TocMetaOffset + 4, checked((uint)payload.Length));
                patchedEntries[entry.Name] = replacement;
            }
            else
            {
                data.Write(original, doc.BaseOffset + entry.RelativeOffset, entry.Size);
                WriteBe32(toc, entry.TocMetaOffset, checked((uint)newRelative));
                WriteBe32(toc, entry.TocMetaOffset + 4, checked((uint)entry.Size));
            }
            previousOriginalEnd = checked(entry.RelativeOffset + entry.Size);
        }

        int originalDataLength = original.Length - doc.BaseOffset;
        if (previousOriginalEnd < originalDataLength)
            data.Write(original, doc.BaseOffset + previousOriginalEnd, originalDataLength - previousOriginalEnd);

        byte[] compressedToc = CompressTocWithoutAdler(toc);
        if (TocHeaderOffset + compressedToc.Length > doc.BaseOffset)
            throw new InvalidDataException("Новый TOC gui_textures.bnk не помещается до области данных.");

        byte[] payloadData = data.ToArray();
        byte[] output = new byte[checked(doc.BaseOffset + payloadData.Length)];
        Buffer.BlockCopy(original, 0, output, 0, Math.Min(doc.BaseOffset, original.Length));
        WriteBe32(output, 0x09, checked((uint)compressedToc.Length));
        WriteBe32(output, 0x0D, checked((uint)toc.Length));
        Array.Clear(output, TocHeaderOffset, doc.BaseOffset - TocHeaderOffset);
        Buffer.BlockCopy(compressedToc, 0, output, TocHeaderOffset, compressedToc.Length);
        Buffer.BlockCopy(payloadData, 0, output, doc.BaseOffset, payloadData.Length);
        return output;
    }

    private static byte[] PatchTextureHeaderBank(
        byte[] originalHeaders,
        IReadOnlyDictionary<string, TextureReplacement> patchedEntries,
        out int headerCount)
    {
        byte[] output = (byte[])originalHeaders.Clone();
        CompactBank doc = ReadCompactBank(output);
        headerCount = 0;

        foreach (BankEntry entry in doc.Entries)
        {
            if (!patchedEntries.TryGetValue(entry.Name, out TextureReplacement? replacement))
                continue;
            if (entry.Size < 84)
                throw new InvalidDataException($"Слишком короткий заголовок текстуры: {entry.Name}");

            int o = checked(doc.BaseOffset + entry.RelativeOffset);
            // Convert the selected resource to the same raw ARGB texture format
            // used by Fable II's original icon_button_a/b/x/y resources.
            WriteBe32(output, o + 4, checked((uint)replacement.EntrySize));
            WriteBe32(output, o + 12, 0x000000A2);
            WriteBe32(output, o + 16, checked((uint)replacement.Width));
            WriteBe32(output, o + 20, checked((uint)replacement.Height));
            WriteBe32(output, o + 24, 0x00000002); // raw ARGB
            WriteBe32(output, o + 28, 0x00000001);
            WriteBe32(output, o + 32, 0x00000054);
            WriteBe32(output, o + 80, 0x0000000D);
            headerCount++;
        }
        return output;
    }

    /// <summary>
    /// Verify the ABYX prompt/footer atlas after repacking. The green selection
    /// badge is a DIFFERENT resource, Frames/frames_elements.tex, checked by v17.
    /// No FAC/BGF or input semantics are modified here.
    /// </summary>
    private static string VerifyPromptAtlas(byte[] textures,
        IReadOnlyDictionary<string, TextureReplacement> patchedEntries)
    {
        const string atlasName = @"Art\GUI\Persistant\abyx.tex";
        if (!patchedEntries.ContainsKey(atlasName))
            return "not updated; some ABYX prompt icons may remain Xbox";

        CompactBank bank = ReadCompactBank(textures);
        BankEntry? entry = bank.Entries.FirstOrDefault(e =>
            e.Name.Replace('/', '\\').Equals(atlasName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            throw new InvalidDataException("ABYX atlas was not found after repack.");

        byte[] logicalArgb = DualShockAtlasArtwork.GetLogicalArgb();
        byte[] expected = CreateRawTextureEntry(new TextureReplacement(256, 256, logicalArgb));
        int absolute = checked(bank.BaseOffset + entry.RelativeOffset);
        if (entry.Size != expected.Length ||
            !textures.AsSpan(absolute, entry.Size).SequenceEqual(expected))
            throw new InvalidDataException("ABYX prompt atlas differs from the verified PS artwork after repack.");
        return "verified: 256x256 PS atlas (A=Cross) stored in gui_textures.bnk";
    }

    private static void ValidatePatchedBanks(
        byte[] textures,
        byte[] headers,
        IReadOnlyDictionary<string, TextureReplacement> replacements,
        IReadOnlyDictionary<string, TextureReplacement> patchedEntries)
    {
        CompactBank texDoc = ReadCompactBank(textures);
        CompactBank hdrDoc = ReadCompactBank(headers);
        var tex = texDoc.Entries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var hdr = hdrDoc.Entries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

        foreach ((string name, TextureReplacement r) in patchedEntries)
        {
            if (!tex.TryGetValue(name, out BankEntry? te) || te.Size != r.EntrySize)
                throw new InvalidDataException($"Проверка нового texture entry не пройдена: {name}");
            if (!hdr.TryGetValue(name, out BankEntry? he) || he.Size < 84)
                throw new InvalidDataException($"Не найден заголовок новой текстуры: {name}");

            int ho = checked(hdrDoc.BaseOffset + he.RelativeOffset);
            if (ReadBe32(headers, ho + 4) != (uint)r.EntrySize ||
                ReadBe32(headers, ho + 16) != (uint)r.Width ||
                ReadBe32(headers, ho + 20) != (uint)r.Height ||
                ReadBe32(headers, ho + 24) != 2u)
                throw new InvalidDataException($"Проверка параметров новой текстуры не пройдена: {name}");
        }

        // The originally studied Russian GOTY bank contained all 14.
        // Keeping the lower bound at 5 lets synthetic/minimal fixtures remain useful.
        if (patchedEntries.Count < 5)
            throw new InvalidDataException("Найдено слишком мало ресурсов DualShock; патч отменён.");
    }

    private static CompactBank ReadCompactBank(byte[] bank)
    {
        if (bank.Length < TocHeaderOffset + 8)
            throw new InvalidDataException("BNK слишком мал или повреждён.");

        int baseOffset = checked((int)ReadBe32(bank, 0));
        int tocCompressedSize = checked((int)ReadBe32(bank, 0x09));
        int tocUncompressedSize = checked((int)ReadBe32(bank, 0x0D));
        if (baseOffset <= 0 || baseOffset >= bank.Length)
            throw new InvalidDataException("Некорректная база данных BNK.");
        if (tocCompressedSize <= 0 || TocHeaderOffset + tocCompressedSize > bank.Length)
            throw new InvalidDataException("Некорректный сжатый TOC BNK.");
        if (tocUncompressedSize <= 4 || tocUncompressedSize > 32 * 1024 * 1024)
            throw new InvalidDataException("Некорректный размер TOC BNK.");

        byte[] compressed = new byte[tocCompressedSize];
        Buffer.BlockCopy(bank, TocHeaderOffset, compressed, 0, compressed.Length);
        byte[] toc = InflateTocLenient(compressed, tocUncompressedSize);
        if (toc.Length < tocUncompressedSize)
            throw new InvalidDataException("TOC BNK распакован не полностью.");
        if (toc.Length != tocUncompressedSize) Array.Resize(ref toc, tocUncompressedSize);

        uint count = ReadBe32(toc, 0);
        if (count == 0 || count > 100000)
            throw new InvalidDataException("Некорректное количество записей TOC BNK.");

        int off = 4;
        var entries = new List<BankEntry>((int)Math.Min(count, 4096u));
        for (uint i = 0; i < count; i++)
        {
            if (off + 4 > toc.Length) throw new InvalidDataException("TOC обрезан.");
            int nameLen = checked((int)ReadBe32(toc, off));
            if (nameLen <= 0 || nameLen > 4096 || off + 4 + nameLen + 8 > toc.Length)
                throw new InvalidDataException("Некорректная запись TOC.");

            string name = Encoding.UTF8.GetString(toc, off + 4, nameLen).TrimEnd('\0');
            int meta = off + 4 + nameLen;
            int relative = checked((int)ReadBe32(toc, meta));
            int size = checked((int)ReadBe32(toc, meta + 4));
            long absolute = (long)baseOffset + relative;
            if (relative < 0 || size < 0 || absolute < 0 || absolute + size > bank.LongLength)
                throw new InvalidDataException("Запись TOC выходит за границы банка.");

            entries.Add(new BankEntry(name, relative, size, meta));
            off = meta + 8;
        }
        return new CompactBank(baseOffset, toc, entries);
    }

    private static byte[] InflateTocLenient(byte[] compressed, int expected)
    {
        try
        {
            using var input = new MemoryStream(compressed, writable: false);
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(expected);
            z.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            // Original Fable II compact banks commonly omit the zlib Adler-32
            // trailer. Skip the two-byte zlib header and inflate raw DEFLATE.
            if (compressed.Length <= 2) throw;
            using var input = new MemoryStream(compressed, 2, compressed.Length - 2, writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(expected);
            deflate.CopyTo(output);
            return output.ToArray();
        }
    }

    private static byte[] CompressTocWithoutAdler(byte[] toc)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(toc, 0, toc.Length);
        byte[] full = ms.ToArray();
        if (full.Length <= 6) throw new InvalidDataException("Не удалось сжать TOC BNK.");
        // Match Fable II's compact BNK convention: zlib header + DEFLATE body,
        // without the final Adler-32 checksum bytes.
        Array.Resize(ref full, full.Length - 4);
        return full;
    }

    private static byte[] CreateRawTextureEntry(TextureReplacement replacement)
    {
        int pixels = checked(replacement.Width * replacement.Height * 4);
        if (replacement.LogicalArgb.Length != pixels)
            throw new InvalidDataException("Некорректный размер подготовленной DualShock-текстуры.");

        byte[] entry = new byte[checked(pixels + 4)];
        WriteBe32(entry, 0, checked((uint)pixels));
        WriteLogicalTexture(entry, 4, replacement.LogicalArgb,
            replacement.Width, replacement.Height, TexelLayout.Tiled2D);
        return entry;
    }

    private static bool IsAbyxAtlas(string entryName) =>
        entryName.Replace('/', '\\').EndsWith(@"\Persistant\abyx.tex", StringComparison.OrdinalIgnoreCase);

    private static bool TryResolveReplacement(
        string entryName,
        IReadOnlyDictionary<string, TextureReplacement> replacements,
        out TextureReplacement? replacement)
    {
        if (replacements.TryGetValue(entryName, out replacement))
            return true;

        string normalized = entryName.Replace('/', '\\');
        if (!normalized.StartsWith(@"Art\GUI\Controller\", StringComparison.OrdinalIgnoreCase))
            return false;

        string file = Path.GetFileName(normalized).ToLowerInvariant();
        string? canonical = file switch
        {
            _ when IsVariant(file, "icon_button_a") => @"Art\GUI\Controller\icon_button_a.tex",
            _ when IsVariant(file, "icon_button_b") => @"Art\GUI\Controller\icon_button_b.tex",
            _ when IsVariant(file, "icon_button_x") => @"Art\GUI\Controller\icon_button_x.tex",
            _ when IsVariant(file, "icon_button_y") => @"Art\GUI\Controller\icon_button_y.tex",
            _ when IsVariant(file, "icon_button_lt") => @"Art\GUI\Controller\icon_button_lt.tex",
            _ when IsVariant(file, "leftbutton") => @"Art\GUI\Controller\LeftButton.tex",
            _ when IsVariant(file, "rightbutton") => @"Art\GUI\Controller\RightButton.tex",
            _ when IsVariant(file, "lefttrigger") => @"Art\GUI\Controller\LeftTrigger.tex",
            _ when IsVariant(file, "righttrigger") => @"Art\GUI\Controller\RightTrigger.tex",
            _ => null,
        };

        return canonical is not null && replacements.TryGetValue(canonical, out replacement);
    }

    private static bool IsVariant(string fileName, string stem)
    {
        if (!fileName.StartsWith(stem, StringComparison.Ordinal) || !fileName.EndsWith(".tex", StringComparison.Ordinal))
            return false;
        ReadOnlySpan<char> middle = fileName.AsSpan(stem.Length, fileName.Length - stem.Length - 4);
        foreach (char ch in middle)
            if (!char.IsDigit(ch) && ch != '_' && ch != '-')
                return false;
        return true;
    }

    private static bool IsShoulderOrTrigger(string path)
    {
        string n = path.Replace('/', '\\');
        return n.EndsWith("\\LeftButton.tex", StringComparison.OrdinalIgnoreCase) ||
               n.EndsWith("\\RightButton.tex", StringComparison.OrdinalIgnoreCase) ||
               n.EndsWith("\\LeftTrigger.tex", StringComparison.OrdinalIgnoreCase) ||
               n.EndsWith("\\RightTrigger.tex", StringComparison.OrdinalIgnoreCase) ||
               n.EndsWith("\\icon_button_lt.tex", StringComparison.OrdinalIgnoreCase);
    }

    private static GlyphKind? MapFaceEntry(string path)
    {
        string file = Path.GetFileName(path.Replace('\\', '/')).ToLowerInvariant();
        if (!file.StartsWith("icon_button_", StringComparison.Ordinal) ||
            !file.EndsWith(".tex", StringComparison.Ordinal)) return null;
        string stem = file.Substring("icon_button_".Length, file.Length - "icon_button_".Length - 4);
        while (stem.Length > 1 && char.IsDigit(stem[^1])) stem = stem[..^1];
        return stem switch
        {
            "a" => GlyphKind.Cross,
            "b" => GlyphKind.Circle,
            "x" => GlyphKind.Square,
            "y" => GlyphKind.Triangle,
            _ => null,
        };
    }

    private static bool TryReadPixelByteCount(byte[] bank, int entryOffset, out int pixelBytes)
    {
        pixelBytes = 0;
        if (entryOffset < 0 || entryOffset + 4 > bank.Length) return false;
        int be = checked((int)ReadBe32(bank, entryOffset));
        if (be > 0 && be <= FacePixelBytes * 4) { pixelBytes = be; return true; }
        int le = BitConverter.ToInt32(bank, entryOffset);
        if (le > 0 && le <= FacePixelBytes * 4) { pixelBytes = le; return true; }
        return false;
    }

    private static TexelLayout DetectLayout(byte[] bank, int pixelStart, int width, int height)
    {
        long linear = LayoutScore(bank, pixelStart, width, height, TexelLayout.Linear);
        long tiled = LayoutScore(bank, pixelStart, width, height, TexelLayout.Tiled2D);
        return tiled < linear ? TexelLayout.Tiled2D : TexelLayout.Linear;
    }

    private static long LayoutScore(byte[] bank, int start, int width, int height, TexelLayout layout)
    {
        long score = 0;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            ReadLogicalPixel(bank, start, layout, x, y, width, out byte a, out byte r, out byte g, out byte b);
            int luma = (r * 3 + g * 6 + b) / 10;
            if (x + 1 < width)
            {
                ReadLogicalPixel(bank, start, layout, x + 1, y, width, out byte a2, out byte r2, out byte g2, out byte b2);
                int l2 = (r2 * 3 + g2 * 6 + b2) / 10;
                score += Math.Abs(luma - l2) + Math.Abs(a - a2) * 2L;
            }
            if (y + 1 < height)
            {
                ReadLogicalPixel(bank, start, layout, x, y + 1, width, out byte a2, out byte r2, out byte g2, out byte b2);
                int l2 = (r2 * 3 + g2 * 6 + b2) / 10;
                score += Math.Abs(luma - l2) + Math.Abs(a - a2) * 2L;
            }
            if ((x < 3 || x >= width - 3 || y < 3 || y >= height - 3) && a > 32) score += a * 3L;
        }
        return score;
    }

    private static void ReadLogicalPixel(byte[] data, int start, TexelLayout layout, int x, int y, int width,
        out byte a, out byte r, out byte g, out byte b)
    {
        int rel = layout == TexelLayout.Linear
            ? ((y * width + x) * 4)
            : Tiled2DOffset(x, y, width, 2);
        int i = start + rel;
        a = data[i + 0]; r = data[i + 1]; g = data[i + 2]; b = data[i + 3];
    }

    private static void WriteLogicalTexture(
        byte[] destination,
        int start,
        byte[] logicalArgb,
        int width,
        int height,
        TexelLayout layout)
    {
        if (logicalArgb.Length != checked(width * height * 4))
            throw new ArgumentException("Invalid logical texture size.");
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int src = (y * width + x) * 4;
            int rel = layout == TexelLayout.Linear ? src : Tiled2DOffset(x, y, width, 2);
            int dst = start + rel;
            destination[dst + 0] = logicalArgb[src + 0];
            destination[dst + 1] = logicalArgb[src + 1];
            destination[dst + 2] = logicalArgb[src + 2];
            destination[dst + 3] = logicalArgb[src + 3];
        }
    }

    // Xenos / Xbox 360 2D tiled address. bytesPerBlockLog2=2 for 32-bit ARGB.
    private static int Tiled2DOffset(int x, int y, int pitchAligned, int bytesPerBlockLog2)
    {
        int outerBlocks = (((y >> 5) * (pitchAligned >> 5) + (x >> 5)) << 6);
        int innerBlocks = (((y >> 1) & 0b111) << 3) | (x & 0b111);
        int outerInnerBytes = (outerBlocks | innerBlocks) << bytesPerBlockLog2;
        int bank = (y >> 4) & 1;
        int pipe = ((x >> 3) & 0b11) ^ (((y >> 3) & 1) << 1);
        int yLsb = y & 1;
        return ((yLsb << 4) | (pipe << 6) | (bank << 11)) |
               (outerInnerBytes & 0b1111) |
               (((outerInnerBytes >> 4) & 1) << 5) |
               (((outerInnerBytes >> 5) & 0b111) << 8) |
               ((outerInnerBytes >> 8) << 12);
    }

    private static byte[] RenderPlayStationButton(GlyphKind glyph)
    {
        const int ss = 4;
        var result = new byte[FacePixelBytes];
        double cx = (FaceWidth - 1) * 0.5;
        double cy = (FaceHeight - 1) * 0.5;

        (byte sr, byte sg, byte sb) = glyph switch
        {
            GlyphKind.Triangle => ((byte)73, (byte)188, (byte)173),
            GlyphKind.Circle => ((byte)242, (byte)76, (byte)40),
            GlyphKind.Cross => ((byte)174, (byte)184, (byte)229),
            GlyphKind.Square => ((byte)222, (byte)137, (byte)190),
            _ => ((byte)255, (byte)255, (byte)255),
        };

        for (int y = 0; y < FaceHeight; y++)
        for (int x = 0; x < FaceWidth; x++)
        {
            int sumA = 0, sumR = 0, sumG = 0, sumB = 0;
            for (int sy = 0; sy < ss; sy++)
            for (int sx = 0; sx < ss; sx++)
            {
                double px = x + (sx + 0.5) / ss;
                double py = y + (sy + 0.5) / ss;
                double dx = px - (cx + 0.5);
                double dy = py - (cy + 0.5);
                double radius = Math.Sqrt(dx * dx + dy * dy);

                int a = 0, r = 0x49, g = 0x49, b = 0x49;
                if (radius <= 29.0)
                {
                    a = 255; r = 241; g = 241; b = 241;
                    if (radius <= 25.8) { r = 24; g = 36; b = 48; }
                    if (IsSymbolSample(glyph, px, py, cx + 0.5, cy + 0.5))
                    { r = sr; g = sg; b = sb; }
                }
                sumA += a; sumR += r; sumG += g; sumB += b;
            }
            int samples = ss * ss;
            int i = (y * FaceWidth + x) * 4;
            result[i + 0] = (byte)(sumA / samples);
            result[i + 1] = (byte)(sumR / samples);
            result[i + 2] = (byte)(sumG / samples);
            result[i + 3] = (byte)(sumB / samples);
        }
        return result;
    }

    private static bool IsSymbolSample(GlyphKind glyph, double px, double py, double cx, double cy)
    {
        double stroke = 3.1;
        double r = 14.0;
        return glyph switch
        {
            GlyphKind.Cross => Math.Min(
                SegmentDistance(px, py, cx - 10.5, cy - 10.5, cx + 10.5, cy + 10.5),
                SegmentDistance(px, py, cx + 10.5, cy - 10.5, cx - 10.5, cy + 10.5)) <= stroke * 0.5,
            GlyphKind.Circle => Math.Abs(Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)) - r) <= stroke * 0.5,
            GlyphKind.Square => RectOutlineDistance(px, py, cx - 12.5, cy - 12.5, cx + 12.5, cy + 12.5) <= stroke * 0.5,
            GlyphKind.Triangle => TriangleOutlineDistance(px, py,
                cx, cy - 14.5, cx + 13.2, cy + 11.5, cx - 13.2, cy + 11.5) <= stroke * 0.5,
            _ => false,
        };
    }

    private static double SegmentDistance(double px, double py, double ax, double ay, double bx, double by)
    {
        double vx = bx - ax, vy = by - ay;
        double wx = px - ax, wy = py - ay;
        double vv = vx * vx + vy * vy;
        double t = vv <= 1e-12 ? 0 : Math.Clamp((wx * vx + wy * vy) / vv, 0.0, 1.0);
        double dx = px - (ax + t * vx), dy = py - (ay + t * vy);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double RectOutlineDistance(double px, double py, double l, double t, double r, double b)
    {
        return Math.Min(Math.Min(SegmentDistance(px, py, l, t, r, t), SegmentDistance(px, py, r, t, r, b)),
                        Math.Min(SegmentDistance(px, py, r, b, l, b), SegmentDistance(px, py, l, b, l, t)));
    }

    private static double TriangleOutlineDistance(double px, double py,
        double ax, double ay, double bx, double by, double cx, double cy)
    {
        return Math.Min(SegmentDistance(px, py, ax, ay, bx, by),
            Math.Min(SegmentDistance(px, py, bx, by, cx, cy), SegmentDistance(px, py, cx, cy, ax, ay)));
    }

    private static string FullPath(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static uint ReadBe32(byte[] data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length) throw new EndOfStreamException();
        return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
               ((uint)data[offset + 2] << 8) | data[offset + 3];
    }

    private static void WriteBe32(byte[] data, int offset, uint value)
    {
        if (offset < 0 || offset + 4 > data.Length) throw new EndOfStreamException();
        data[offset + 0] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static void AtomicWrite(string path, byte[] data)
    {
        string temp = path + ".dualshock-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void AtomicCopy(string source, string destination)
    {
        string temp = destination + ".dualshock-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temp, overwrite: true);
            File.Move(temp, destination, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void WriteLog(string path, string text)
    {
        try { File.WriteAllText(path, text, new UTF8Encoding(false)); }
        catch { /* diagnostics must never block game launch */ }
    }
}
