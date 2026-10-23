using Fable2Launcher;
using System.Diagnostics;
using System.Text.Json;
using System.Text;

internal static class TextureDiagnosticsTests
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Texture/config regression: " + message);
        _checks++;
    }
    private static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (FormatException) { rejected = true; }
        Check(rejected, message);
    }

    public static void Run(string root)
    {
        const string original = "[general] # version\r\nconfig_version = 1\r\n[patches] # patches\r\nrealtime_texture_morphing=true#keep\r\nhero_dog_texture_readback = false\r\n[other]\r\npath = 'C:\\Folder #1'\r\nitems = [1, \"two#three\"]\r\n";
        string path = Path.Combine(root, "texture-comments.toml");
        File.WriteAllText(path, original);
        var values = Fable2ConfigFile.Read(path);
        Check(values.RealtimeTextureMorphing && !values.HeroDogTextureReadback, "commented header read");
        TextureModeSettings.Apply(values, HeroDogTextureMode.CompatibilityReadback);
        Fable2ConfigFile.Write(path, values);
        string output = File.ReadAllText(path);
        Check(output.Contains("[patches] # patches\r\n"), "section comment/CRLF preserved");
        Check(output.Contains("realtime_texture_morphing=false#keep"), "adjacent inline comment preserved");
        Check(output.Contains("path = 'C:\\Folder #1'"), "literal hash preserved");
        Check(output.Contains("items = [1, \"two#three\"]"), "unowned array preserved");
        Check(File.ReadAllText(path + ".launcher-backup") == original, "original backup byte content");
        Fable2ConfigFile.Write(path, values);
        Check(File.ReadAllText(path) == output, "idempotent second write");
        values = Fable2ConfigFile.Read(path);
        Check(!values.RealtimeTextureMorphing && values.HeroDogTextureReadback, "mode readback roundtrip");

        var doc = new TomlTextDocument("['patches']#quoted table\n\"hero_dog_texture_readback\"=false#note\n");
        doc.SetValue("patches", "hero_dog_texture_readback", "true");
        Check(doc.ToString().Contains("\"hero_dog_texture_readback\"=true#note"), "quoted key retained");
        foreach (string value in new[] { "plain", "hash # not a comment", "quotes \" and \\ slash", "line\nnext\tend", "\b\f\r", "\u0416\U0001F436", "literal \\" })
            Check(TomlTextDocument.Unquote(TomlTextDocument.Quote(value)) == value, "string escape roundtrip");
        Check(TomlTextDocument.Unquote("\"\\u0416\\U0001F436\"") == "\u0416\U0001F436", "Unicode escape decoding");

        string sdk = Path.Combine(root, "sdk-comments.toml");
        File.WriteAllText(sdk, "custom = \"keep # literal\" # real comment\n[other]\nvsync=true\n");
        Check(LauncherConfigFile.ReadValues(sdk)["custom"] == "\"keep # literal\"", "SDK literal hash read");
        LauncherConfigFile.WriteValues(sdk, new Dictionary<string, string> { ["vsync"] = "false" }, new[] { "vsync" });
        string sdkText = File.ReadAllText(sdk);
        Check(sdkText.IndexOf("vsync=false", StringComparison.Ordinal) < 0 && sdkText.IndexOf("vsync = false", StringComparison.Ordinal) < sdkText.IndexOf("[other]", StringComparison.Ordinal), "root key before tables");
        Check(sdkText.Contains("[other]\nvsync=true"), "unrelated table unchanged");

        foreach (string invalid in new[] {
            "[patches]\na=true\n[patches]\nb=false\n", "[patches]\na=true\na=false\n",
            "note=\"\"\"multiline\n[patches]\n\"\"\"\n", "values = [\n1,2]\n", "note=\"unterminated\n" })
        {
            File.WriteAllText(path, invalid);
            Reject(() => Fable2ConfigFile.Write(path, new()), "unsafe/unsupported document rejected");
            Check(File.ReadAllText(path) == invalid, "rejection leaves bytes unchanged");
        }
        File.WriteAllText(path, "[general]\nconfig_version=2\n");
        Reject(() => Fable2ConfigFile.Write(path, new()), "future version not overwritten");
        Check(File.ReadAllText(path).Contains("config_version=2"), "future version preserved");

        foreach (var mode in Enum.GetValues<HeroDogTextureMode>())
        {
            var cfg = new Fable2ConfigValues { HighTickRate = true, HotFuncYieldEvery = 7, MouseLookScale = 700 };
            TextureModeSettings.Apply(cfg, mode);
            Check(TextureModeSettings.Get(cfg) == mode, "mode mapping");
            Check(!(cfg.RealtimeTextureMorphing && cfg.HeroDogTextureReadback), "mutually exclusive flags");
            Check(cfg.HighTickRate && cfg.HotFuncYieldEvery == 7 && cfg.MouseLookScale == 700, "unrelated settings untouched");
        }
        Check(TextureModeSettings.Get(new() { RealtimeTextureMorphing = true, HeroDogTextureReadback = true }) == HeroDogTextureMode.GpuRealtime, "legacy both-on precedence");
        var start = new ProcessStartInfo();
        var compat = new Fable2ConfigValues();
        TextureModeSettings.Apply(compat, HeroDogTextureMode.CompatibilityReadback);
        TextureModeSettings.AddReadbackOverride(start, compat);
        Check(start.ArgumentList.SequenceEqual(new[] { "--readback_resolve_force_addresses=0x12704000" }), "override is one targeted argument, not full global readback");
        Check(LaunchDiagnostics.FormatExitCode(unchecked((int)0xC0000409)) == "0xC0000409", "negative Windows exit code conversion");


        // Launcher-only DualShock prompt swap: exercise the compact GUI bank parser,
        // face-button replacements, one-time backup and restoration without any game data.
        string dualRoot = Path.Combine(root, "dualshock-fixture");
        string dualBank = Path.Combine(dualRoot, "data", "art", "gui", "gui_textures.bnk");
        string dualHeaders = Path.Combine(dualRoot, "data", "art", "gui", "gui_texture_headers.bnk");
        Directory.CreateDirectory(Path.GetDirectoryName(dualBank)!);
        byte[] syntheticBank = BuildSyntheticGuiTextureBank();
        byte[] syntheticHeaders = BuildSyntheticGuiTextureHeaderBank();
        File.WriteAllBytes(dualBank, syntheticBank);
        File.WriteAllBytes(dualHeaders, syntheticHeaders);
        string onStatus = DualShockPromptPatcher.Apply(dualRoot, true);
        Check(onStatus.Contains("DualShock", StringComparison.Ordinal), "DualShock enable status");
        Check(onStatus.Contains("атлас проверен", StringComparison.Ordinal),
            "ABYX prompt atlas is patched, including an earlier unknown raw backup");
        Check(File.Exists(dualBank + DualShockPromptPatcher.BackupSuffix), "DualShock texture backup created");
        Check(File.Exists(dualHeaders + DualShockPromptPatcher.BackupSuffix), "DualShock header backup created");
        byte[] patchedBank = File.ReadAllBytes(dualBank);
        byte[] patchedHeaders = File.ReadAllBytes(dualHeaders);
        Check(!patchedBank.SequenceEqual(syntheticBank), "DualShock texture bank was modified");
        Check(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "launcher-dualshock-patch.log"))
            .Contains("ABYX prompt atlas: verified", StringComparison.Ordinal),
            "ABYX replacement verified against actual repacked bank bytes");
        Check(!patchedHeaders.SequenceEqual(syntheticHeaders), "DualShock header bank was modified");
        Check(File.ReadAllBytes(dualBank + DualShockPromptPatcher.BackupSuffix).SequenceEqual(syntheticBank), "DualShock texture backup remained original");
        Check(File.ReadAllBytes(dualHeaders + DualShockPromptPatcher.BackupSuffix).SequenceEqual(syntheticHeaders), "DualShock header backup remained original");
        string offStatus = DualShockPromptPatcher.Apply(dualRoot, false);
        Check(offStatus.Contains("восстановлены", StringComparison.OrdinalIgnoreCase), "DualShock disable status");
        Check(File.ReadAllBytes(dualBank).SequenceEqual(syntheticBank), "DualShock texture restore is byte-identical");
        Check(File.ReadAllBytes(dualHeaders).SequenceEqual(syntheticHeaders), "DualShock header restore is byte-identical");

        // v17 targets the native MenuHighlight badge, not frontendmainmenu.fac.
        MenuBadgeV17Tests.Run(root);

        // Legacy synthetic atlas detection regression (v10).
        TestV12AuthenticAbyxArtwork();
        TestV13CompatibleAtlasSources();

        byte[] directPatch = (byte[])syntheticBank.Clone();
        int promptCount = DualShockPromptPatcher.PatchBank(directPatch);
        Check(promptCount == 4, "synthetic A/B/X/Y prompt count");
        Check(HasOpaqueGlyph(directPatch, 0x8000 + 0x0000, 64 * 64 * 4), "Cross glyph rendered");
        Check(HasOpaqueGlyph(directPatch, 0x8000 + 0x5000, 64 * 64 * 4), "Circle glyph rendered");
        Check(HasOpaqueGlyph(directPatch, 0x8000 + 0xA000, 64 * 64 * 4), "Square glyph rendered");
        Check(HasOpaqueGlyph(directPatch, 0x8000 + 0xF000, 64 * 64 * 4), "Triangle glyph rendered");

        // Exercise report collection with a synthetic directory, not a game process.
        string fake = Path.Combine(root, "fake-game");
        Directory.CreateDirectory(Path.Combine(fake, "logs"));
        File.WriteAllText(Path.Combine(fake, "fable2_config.toml"), "[general]\nconfig_version=1\n");
        File.WriteAllText(Path.Combine(fake, "fable_2.exe"), "synthetic fixture, not executable");
        File.WriteAllText(Path.Combine(fake, "default.xex"), "must not be copied");
        Directory.CreateDirectory(Path.Combine(fake, "saves"));
        File.WriteAllText(Path.Combine(fake, "saves", "private.sav"), "must not be copied");
        File.WriteAllText(Path.Combine(fake, "logs", "old.log"), "old session\n");
        var plan = new GameLaunchPlan(Path.Combine(fake, "fable_2.exe"), fake, fake);
        var report = LaunchDiagnostics.Begin(plan, new ProcessStartInfo { FileName = plan.Executable });
        report.MarkStarted(123);
        File.WriteAllText(Path.Combine(fake, "logs", "new.log"), "[texture-runtime] test entry\n");
        report.Complete(unchecked((int)0xC0000409));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(report.DirectoryPath, "session.json")));
        Check(json.RootElement.GetProperty("exit_code_hex").GetString() == "0xC0000409", "report exit code");
        Check(File.Exists(Path.Combine(report.DirectoryPath, "logs", "new.log")), "new log copied");
        Check(!File.Exists(Path.Combine(report.DirectoryPath, "logs", "old.log")), "unchanged old log not copied");
        Check(!Directory.EnumerateFiles(report.DirectoryPath, "*", SearchOption.AllDirectories).Any(f => f.EndsWith(".xex") || f.EndsWith(".sav") || f.EndsWith(".exe")), "no game or save content in report");
        Check(File.ReadAllText(Path.Combine(report.DirectoryPath, "texture-runtime.txt")).Contains("test entry"), "runtime evidence collected");
        Console.WriteLine($"Texture/config/diagnostic regression checks passed: {_checks}.");
    }

    private static void TestV12AuthenticAbyxArtwork()
    {
        byte[] atlas = DualShockAtlasArtwork.GetLogicalArgb();
        Check(atlas.Length == 256 * 256 * 4, "v12 authentic ABYX 256x256");
        // Source alpha and lower 128 rows are identical to the original Xbox bank.
        // These are exact SHA256 hashes of the original decoded atlas from user data.
        byte[] bottom = atlas.AsSpan(128 * 256 * 4).ToArray();
        Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bottom)) ==
            "A517C7D3C7C4DD9AC4DDD4FEDAF4F5052FFA2FF02375FE9FA29F5E859440F719", "v12 original bottom-half HOLD resources preserved");
        byte[] alpha = new byte[256 * 256];
        for (int p = 0; p < alpha.Length; p++) alpha[p] = atlas[p * 4];
        Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(alpha)) ==
            "3D420AEE6BB5887021D6B1750599B8CE90243A8ACC1424F2096C91A61F8B2125", "v12 all original alpha channel values preserved");
        // Bottom half may be invisible in a PNG due to transparency, but still
        // contains texture color data used by animations. Test the raw ARGB.
    }

    private static void TestV13CompatibleAtlasSources()
    {
        // Simulate an intermediary launcher version having saved its own raw
        // menu atlas as the one-time backup. The SHA differs from v9/v12, but
        // the supported 256x256 raw layout is unambiguous.
        var raw = new byte[4 + 256 * 256 * 4];
        WriteBe32(raw, 0, 256 * 256 * 4);
        raw[4] = 255; raw[5] = 11;
        Check(DualShockAtlasArtwork.TryGetReplacement(raw, 0, raw.Length,
                out byte[] patched, out string report),
            "v13 repairs an intermediate raw menu atlas backup");
        Check(patched.SequenceEqual(DualShockAtlasArtwork.GetLogicalArgb()),
            "v13 uses checksum-verified PS icons for menu A");
        Check(report.Contains("compatible raw atlas", StringComparison.Ordinal),
            "v13 reports compatible atlas fallback");

        var compressed = new byte[16212];
        WriteBe32(compressed, 0, 1);
        WriteBe32(compressed, 4, 48);
        WriteBe32(compressed, 12, 3);
        WriteBe32(compressed, 16, 11748);
        WriteBe32(compressed, 20, 4464);
        Check(DualShockAtlasArtwork.TryGetReplacement(compressed, 0, compressed.Length,
                out _, out _), "v13 recognizes compatible compressed menu atlas metadata");

        compressed[20] = 0x7f;
        Check(!DualShockAtlasArtwork.TryGetReplacement(compressed, 0, compressed.Length,
                out _, out _), "v13 refuses an unknown atlas layout");
    }

    private static bool HasOpaqueGlyph(byte[] bank, int entryStart, int pixelBytes)
    {
        int start = entryStart + 4;
        int end = start + pixelBytes;
        if (start < 0 || end > bank.Length) return false;
        bool transparent = false, opaque = false;
        for (int i = start; i < end; i += 4)
        {
            transparent |= bank[i] == 0;
            opaque |= bank[i] > 200;
        }
        return transparent && opaque;
    }

    private static byte[] BuildSyntheticGuiTextureBank()
    {
        const int baseOffset = 0x8000;
        var entries = new (string name, int relative, int pixelBytes)[]
        {
            ("Art\\GUI\\Controller\\icon_button_a.tex",  0x0000, 64 * 64 * 4),
            ("Art\\GUI\\Controller\\icon_button_b.tex",  0x5000, 64 * 64 * 4),
            ("Art\\GUI\\Controller\\icon_button_x.tex",  0xA000, 64 * 64 * 4),
            ("Art\\GUI\\Controller\\icon_button_y.tex",  0xF000, 64 * 64 * 4),
            ("Art\\GUI\\Controller\\icon_button_lt.tex", 0x14000, 31 * 14 * 4),
            ("Art\\GUI\\Persistant\\abyx.tex",          0x16000, 256 * 256 * 4),
        };

        using var tocStream = new MemoryStream();
        WriteBe32(tocStream, (uint)entries.Length);
        foreach (var entry in entries)
        {
            byte[] name = Encoding.UTF8.GetBytes(entry.name + "\0");
            WriteBe32(tocStream, (uint)name.Length);
            tocStream.Write(name);
            WriteBe32(tocStream, (uint)entry.relative);
            WriteBe32(tocStream, (uint)(entry.pixelBytes + 4));
        }
        byte[] toc = tocStream.ToArray();
        byte[] tocCompressed;
        using (var compressed = new MemoryStream())
        {
            using (var z = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                z.Write(toc);
            tocCompressed = compressed.ToArray();
        }

        int lastEnd = entries.Max(e => e.relative + e.pixelBytes + 4);
        byte[] bank = new byte[baseOffset + lastEnd + 0x100];
        WriteBe32(bank, 0, baseOffset);
        bank[7] = 3; bank[8] = 0;
        WriteBe32(bank, 0x09, tocCompressed.Length);
        WriteBe32(bank, 0x0D, toc.Length);
        Buffer.BlockCopy(tocCompressed, 0, bank, 0x11, tocCompressed.Length);
        foreach (var entry in entries)
        {
            int start = baseOffset + entry.relative;
            WriteBe32(bank, start, entry.pixelBytes);
            for (int i = 0; i < entry.pixelBytes; i += 4)
            {
                bank[start + 4 + i + 0] = 0x7F;
                bank[start + 4 + i + 1] = 0x12;
                bank[start + 4 + i + 2] = 0x34;
                bank[start + 4 + i + 3] = 0x56;
            }
        }
        return bank;
    }


    private static byte[] BuildSyntheticGuiTextureHeaderBank()
    {
        const int baseOffset = 0x8000;
        string[] names =
        {
            "Art\\GUI\\Controller\\icon_button_a.tex",
            "Art\\GUI\\Controller\\icon_button_b.tex",
            "Art\\GUI\\Controller\\icon_button_x.tex",
            "Art\\GUI\\Controller\\icon_button_y.tex",
            "Art\\GUI\\Controller\\icon_button_lt.tex",
            "Art\\GUI\\Persistant\\abyx.tex",
        };

        using var tocStream = new MemoryStream();
        WriteBe32(tocStream, (uint)names.Length);
        for (int i = 0; i < names.Length; i++)
        {
            byte[] name = Encoding.UTF8.GetBytes(names[i] + "\0");
            WriteBe32(tocStream, (uint)name.Length);
            tocStream.Write(name);
            WriteBe32(tocStream, (uint)(i * 84));
            WriteBe32(tocStream, 84);
        }
        byte[] toc = tocStream.ToArray();
        byte[] tocCompressed;
        using (var compressed = new MemoryStream())
        {
            using (var z = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                z.Write(toc);
            tocCompressed = compressed.ToArray();
        }

        byte[] bank = new byte[baseOffset + names.Length * 84];
        WriteBe32(bank, 0, baseOffset);
        bank[7] = 3; bank[8] = 0;
        WriteBe32(bank, 0x09, tocCompressed.Length);
        WriteBe32(bank, 0x0D, toc.Length);
        Buffer.BlockCopy(tocCompressed, 0, bank, 0x11, tocCompressed.Length);
        for (int i = 0; i < names.Length; i++)
        {
            int o = baseOffset + i * 84;
            WriteBe32(bank, o + 0, unchecked((int)0xFFFFFFFEu));
            WriteBe32(bank, o + 4, 0x430);
            WriteBe32(bank, o + 12, 0xB0);
            WriteBe32(bank, o + 16, 32);
            WriteBe32(bank, o + 20, 32);
            WriteBe32(bank, o + 24, 0x27);
            WriteBe32(bank, o + 28, 1);
            WriteBe32(bank, o + 32, 0x54);
            WriteBe32(bank, o + 80, 2);
        }
        return bank;
    }

    private static void WriteBe32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteBe32(byte[] data, int offset, int value)
    {
        data[offset + 0] = (byte)((uint)value >> 24);
        data[offset + 1] = (byte)((uint)value >> 16);
        data[offset + 2] = (byte)((uint)value >> 8);
        data[offset + 3] = (byte)value;
    }

}
