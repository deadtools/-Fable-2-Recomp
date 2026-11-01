using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Fable2Launcher;

internal static class ConfigurationSafetyTests
{
    private static int _checks;
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Configuration safety: " + description);
        _checks++;
    }

    private static bool Fails(Action action)
    {
        try { action(); return false; }
        catch (IOException) { return true; }
        catch (FormatException) { return true; }
    }

    private static string PrepareFolder(string root, string name)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static Dictionary<string, string> Settings() =>
        GraphicsSettings.Create("1440p", "3", "5", "fxaa", "borderless", true, "60");

    private static void NoStagingFiles(string root)
    {
        Check(!Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Any(file => file.Contains(".launcher-txn-", StringComparison.Ordinal)),
            "transaction staging file left behind");
    }

    public static void Run(string root)
    {
        string good = PrepareFolder(root, "config-transaction-good");
        string runtime = Path.Combine(good, "fable_2.toml");
        string preferences = Path.Combine(good, "launcher-settings.toml");
        string advanced = Path.Combine(good, "fable2_config.toml");
        string sdkOriginal = "custom_mod = 71\r\ngame_data_root = \"D:\\Fable2\\Game\\\"\r\nresolution_scale = 1\r\n";
        string prefOriginal = "# User choices\r\nresolution_scale = 1\r\n";
        string advancedOriginal = "[general]\r\nconfig_version = 1\r\n[gameplay]\r\ngame_speed = 1.0\r\n[patches]\r\n" +
                                  "realtime_texture_morphing = false\r\nhero_dog_texture_readback = true\r\n" +
                                  "skip_intro_videos = true\r\n[custom]\r\ntest = 777\r\n";
        File.WriteAllText(runtime, sdkOriginal);
        File.WriteAllText(preferences, prefOriginal);
        File.WriteAllText(advanced, advancedOriginal);

        GraphicsConfigurationTransaction.Save(good, Settings(), GraphicsSettings.ManagedKeys, 1.3);
        Check(LauncherConfigFile.ReadValues(runtime)["resolution_scale"] == "3", "SDK resolution scale not saved");
        Check(LauncherConfigFile.ReadValues(preferences)["window_width"] == "2560", "launcher preferences not saved");
        Check(Fable2ConfigFile.Read(advanced).GameSpeed == 1.3, "game speed not saved");
        Check(File.ReadAllText(runtime).Contains("custom_mod = 71"), "SDK unknown key changed");
        Check(File.ReadAllText(advanced).Contains("test = 777") &&
              File.ReadAllText(advanced).Contains("skip_intro_videos = true"),
              "advanced settings changed during graphics save");
        Check(File.ReadAllText(runtime + ".launcher-backup") == sdkOriginal &&
              File.ReadAllText(preferences + ".launcher-backup") == prefOriginal &&
              File.ReadAllText(advanced + ".launcher-backup") == advancedOriginal,
              "transaction original backups missing");
        Check(File.ReadAllText(runtime + ".sdk-path-recovery-backup") == sdkOriginal,
              "SDK broken path recovery backup missing");
        NoStagingFiles(good);

        string invalid = PrepareFolder(root, "config-transaction-invalid");
        runtime = Path.Combine(invalid, "fable_2.toml");
        preferences = Path.Combine(invalid, "launcher-settings.toml");
        advanced = Path.Combine(invalid, "fable2_config.toml");
        File.WriteAllText(runtime, "custom_mod = 1\nresolution_scale = 1\n");
        File.WriteAllText(preferences, "resolution_scale = 1\n");
        File.WriteAllText(advanced, "[general]\nconfig_version = 2\n");
        byte[][] snapshots = [File.ReadAllBytes(runtime), File.ReadAllBytes(preferences), File.ReadAllBytes(advanced)];
        Check(Fails(() => GraphicsConfigurationTransaction.Save(invalid, Settings(), GraphicsSettings.ManagedKeys, 1.5)),
              "unsupported advanced config must fail preflight");
        Check(File.ReadAllBytes(runtime).SequenceEqual(snapshots[0]) &&
              File.ReadAllBytes(preferences).SequenceEqual(snapshots[1]) &&
              File.ReadAllBytes(advanced).SequenceEqual(snapshots[2]),
              "preflight failure changed live files");
        NoStagingFiles(invalid);

        string rollback = PrepareFolder(root, "config-transaction-rollback");
        runtime = Path.Combine(rollback, "fable_2.toml");
        preferences = Path.Combine(rollback, "launcher-settings.toml");
        advanced = Path.Combine(rollback, "fable2_config.toml");
        File.WriteAllText(runtime, "resolution_scale=1\n");
        File.WriteAllText(preferences, "resolution_scale=1\n");
        File.WriteAllText(advanced, "[general]\nconfig_version=1\n[gameplay]\ngame_speed=1.0\n");
        snapshots = [File.ReadAllBytes(runtime), File.ReadAllBytes(preferences), File.ReadAllBytes(advanced)];
        Check(Fails(() => GraphicsConfigurationTransaction.Save(rollback, Settings(),
            GraphicsSettings.ManagedKeys, 1.6,
            beforeCommit: index => { if (index == 1) throw new IOException("injected failure"); })),
            "injected commit error was not propagated");
        Check(File.ReadAllBytes(runtime).SequenceEqual(snapshots[0]) &&
              File.ReadAllBytes(preferences).SequenceEqual(snapshots[1]) &&
              File.ReadAllBytes(advanced).SequenceEqual(snapshots[2]),
              "mid-commit failure was not rolled back byte for byte");
        NoStagingFiles(rollback);

        // Regression: recovery marker must be written even if nothing needed
        // migration, so explicitly selecting GPU mode later is respected.
        string noConfig = PrepareFolder(root, "visual-recovery-new");
        string noConfigPath = Path.Combine(noConfig, "fable2_config.toml");
        Check(!StabilityRecovery.TryRestoreCompatibilityReadback(noConfigPath, out _),
              "empty directory must not migrate");
        Check(File.Exists(Path.Combine(noConfig, StabilityRecovery.MarkerFileName)),
              "missing config did not create the one-time marker");
        var gpu = new Fable2ConfigValues { RealtimeTextureMorphing = true, HeroDogTextureReadback = false };
        Fable2ConfigFile.Write(noConfigPath, gpu);
        Check(!StabilityRecovery.TryRestoreCompatibilityReadback(noConfigPath, out _) &&
              Fable2ConfigFile.Read(noConfigPath).RealtimeTextureMorphing,
              "later explicit GPU choice was reverted");

        string alreadyCompatible = PrepareFolder(root, "visual-recovery-compatible");
        string compatiblePath = Path.Combine(alreadyCompatible, "fable2_config.toml");
        Fable2ConfigFile.Write(compatiblePath, new Fable2ConfigValues());
        Check(!StabilityRecovery.TryRestoreCompatibilityReadback(compatiblePath, out _),
              "compatible config should not be changed");
        Check(File.Exists(Path.Combine(alreadyCompatible, StabilityRecovery.MarkerFileName)),
              "compatible config did not mark one-time recovery complete");
        Fable2ConfigFile.Write(compatiblePath, gpu);
        Check(!StabilityRecovery.TryRestoreCompatibilityReadback(compatiblePath, out _) &&
              Fable2ConfigFile.Read(compatiblePath).RealtimeTextureMorphing,
              "user-selected GPU mode was undone after compatible startup");

        string legacy = PrepareFolder(root, "visual-recovery-legacy");
        string legacyPath = Path.Combine(legacy, "fable2_config.toml");
        Fable2ConfigFile.Write(legacyPath, gpu);
        string before = File.ReadAllText(legacyPath);
        Check(StabilityRecovery.TryRestoreCompatibilityReadback(legacyPath, out string message) &&
              message.Contains("Readback", StringComparison.Ordinal), "legacy GPU migration did not run");
        var migrated = Fable2ConfigFile.Read(legacyPath);
        // v22 policy: both experimental texture paths are disabled (stable mode).
        Check(!migrated.RealtimeTextureMorphing && !migrated.HeroDogTextureReadback,
              "legacy GPU mode was not replaced with the v22 stable mode");
        Check(File.ReadAllText(legacyPath + ".stability-recovery-v22-backup") == before,
              "legacy stability recovery backup not preserved");
        Check(File.Exists(Path.Combine(legacy, StabilityRecovery.MarkerFileName)),
              "legacy recovery marker not written");
        NoStagingFiles(legacy);

        // Pin every advanced patch toggle across both directions. Earlier
        // audits incorrectly reported some managed keys as unsaved; the
        // roundtrip tests make sure that does not regress in future edits.
        string togglesPath = Path.Combine(root, "advanced-all-patches.toml");
        var switches = new Fable2ConfigValues
        {
            UnlockWebsite = false, UnlockCe = false, SkipIntroVideos = true,
            HighTickRate = true, HigherHfTickRate = true,
            DisableMotionBlur = true, RealtimeTextureMorphing = false,
            HeroDogTextureReadback = true,
        };
        Fable2ConfigFile.Write(togglesPath, switches);
        var toggles = Fable2ConfigFile.Read(togglesPath);
        Check(!toggles.UnlockWebsite && !toggles.UnlockCe && toggles.SkipIntroVideos &&
              toggles.HighTickRate && toggles.HigherHfTickRate &&
              toggles.DisableMotionBlur && !toggles.RealtimeTextureMorphing &&
              toggles.HeroDogTextureReadback,
              "advanced patch booleans failed to persist in the first direction");
        switches.UnlockWebsite = true; switches.UnlockCe = true;
        switches.SkipIntroVideos = false; switches.HighTickRate = false;
        switches.HigherHfTickRate = false; switches.DisableMotionBlur = false;
        switches.RealtimeTextureMorphing = true; switches.HeroDogTextureReadback = false;
        Fable2ConfigFile.Write(togglesPath, switches);
        toggles = Fable2ConfigFile.Read(togglesPath);
        Check(toggles.UnlockWebsite && toggles.UnlockCe && !toggles.SkipIntroVideos &&
              !toggles.HighTickRate && !toggles.HigherHfTickRate &&
              !toggles.DisableMotionBlur && toggles.RealtimeTextureMorphing &&
              !toggles.HeroDogTextureReadback,
              "advanced patch booleans failed to persist in the reverse direction");

        // v22 policy: defaults are the stable mode (no GPU morphing, no forced readback).
        Check(!new Fable2ConfigValues().RealtimeTextureMorphing &&
              !new Fable2ConfigValues().HeroDogTextureReadback,
              "launcher defaults are not the v22 stable texture mode");
        Console.WriteLine($"Configuration transaction/rollback and visual recovery passed: {_checks} checks.");
    }
}
