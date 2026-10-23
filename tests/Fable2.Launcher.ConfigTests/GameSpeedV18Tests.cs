using Fable2Launcher;
using System.Globalization;
using System.Text;

internal static class GameSpeedV18Tests
{
    private static int _checks;
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Game speed v18: " + message);
        _checks++;
    }
    public static void Run(string root)
    {
        Check(new Fable2ConfigValues().GameSpeed == 1.0, "default is original speed");
        Check(GameSpeedSettings.Parse(null) == 1.0, "missing -> default");
        foreach (string text in new[] { "bad", "", "NaN", "Infinity", "-Infinity", "0,5" })
            Check(GameSpeedSettings.Parse(text) == 1.0, "invalid/nonfinite -> default: " + text);
        Check(GameSpeedSettings.Normalize(-10) == 0.1, "lower clamp");
        Check(GameSpeedSettings.Normalize(100) == 2.0, "upper clamp");
        Check(GameSpeedSettings.Normalize(0.15) == 0.2, "midpoint away from zero");
        Check(GameSpeedSettings.Normalize(0.64) == 0.6, "round to nearest tenth");
        Check(GameSpeedSettings.Parse("1") == 1.0, "TOML integer accepted");
        Check(GameSpeedSettings.Parse("5e-1") == 0.5, "TOML exponent accepted");

        var savedCulture = CultureInfo.CurrentCulture;
        var savedUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (string culture in new[] { "en-US", "ru-RU", "de-DE" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
                for (int i = 1; i <= 20; i++)
                {
                    double speed = i / 10.0;
                    Check(GameSpeedSettings.Normalize(speed) == speed, "all 20 values: " + culture);
                    Check(GameSpeedSettings.FromStep(i) == speed, "slider -> speed");
                    Check(GameSpeedSettings.ToStep(speed) == i, "speed -> slider");
                    string text = GameSpeedSettings.Serialize(speed);
                    Check(text == speed.ToString("0.0", CultureInfo.InvariantCulture), "invariant TOML decimal");
                    Check(GameSpeedSettings.Parse(text) == speed, "parse/serialize roundtrip");
                    string path = Path.Combine(root, "speed-" + culture + ".toml");
                    Fable2ConfigFile.WriteGameSpeed(path, speed);
                    Check(Fable2ConfigFile.Read(path).GameSpeed == speed, "file roundtrip");
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = savedCulture;
            CultureInfo.CurrentUICulture = savedUiCulture;
        }

        string config = Path.Combine(root, "speed-preservation.toml");
        const string original = "# keep\r\n[general]\r\nconfig_version=1\r\n[gameplay] # comment\r\ngame_speed = 1 # note\r\nother_option = 7\r\n[input]\r\nkeyboard_gamepad_map = 'E:A,Q:LT'\r\nmouse_look=false\r\n[patches]\r\nhigh_tick_rate=true\r\nrealtime_texture_morphing=false\r\nhero_dog_texture_readback=true\r\n[perf]\r\nhotfunc_yield_every=9\r\n[custom]\r\nmod = 'preserve'\r\n";
        File.WriteAllText(config, original);
        Fable2ConfigFile.WriteGameSpeed(config, 0.3);
        string changed = File.ReadAllText(config);
        Check(changed == original.Replace("game_speed = 1 # note", "game_speed = 0.3 # note"),
            "speed-only save changes no input/graphics/patch values, comments or CRLF");
        Check(File.ReadAllText(config + ".launcher-backup") == original, "one-time backup");
        Fable2ConfigFile.WriteGameSpeed(config, 0.3);
        Check(File.ReadAllText(config) == changed, "speed-only write idempotent");
        var advanced = Fable2ConfigFile.Read(config);
        advanced.HotFuncYieldEvery = 4;
        Fable2ConfigFile.Write(config, advanced);
        var reread = Fable2ConfigFile.Read(config);
        Check(reread.GameSpeed == 0.3, "advanced save preserves speed");
        Check(reread.KeyboardGamepadMap == "E:A,Q:LT", "input mapping unchanged");
        Check(!reread.RealtimeTextureMorphing && reread.HeroDogTextureReadback, "texture fix flags unchanged");
        Check(File.ReadAllText(config).Contains("mod = 'preserve'"), "unknown content kept");
        Check(File.ReadAllText(config + ".launcher-backup") == original, "original backup not overwritten");
        foreach (string invalid in new[] { "[general]\nconfig_version=2\n", "[gameplay]\ngame_speed=1\ngame_speed=2\n" })
        {
            File.WriteAllText(config, invalid);
            bool refused = false;
            try { Fable2ConfigFile.WriteGameSpeed(config, 0.5); }
            catch (FormatException) { refused = true; }
            Check(refused, "unsafe config refused");
            Check(File.ReadAllText(config) == invalid, "unsafe config unchanged");
        }

        byte[] marker = Encoding.ASCII.GetBytes(GameSpeedSettings.CapabilityMarker);
        foreach (int offset in new[] { 0, 1, 65535, 65536, 65536 + marker.Length - 2, 131072 })
        {
            byte[] binary = new byte[offset + marker.Length + 16];
            Buffer.BlockCopy(marker, 0, binary, offset, marker.Length);
            using var stream = new MemoryStream(binary);
            Check(GameSpeedSettings.ContainsMarker(stream), "capability marker across chunk boundaries");
            using var shortReads = new ShortReadStream(binary);
            Check(GameSpeedSettings.ContainsMarker(shortReads), "short reads handled");
        }
        using (var truncated = new MemoryStream(marker[..^1]))
            Check(!GameSpeedSettings.ContainsMarker(truncated), "partial marker rejected");
        using (var empty = new MemoryStream())
            Check(!GameSpeedSettings.ContainsMarker(empty), "empty EXE rejected");
        string fakeExe = Path.Combine(root, "speed-test.exe");
        File.WriteAllBytes(fakeExe, marker);
        Check(GameSpeedSettings.SupportsRuntime(fakeExe), "actual executable inspected");
        File.WriteAllText(fakeExe, "old executable");
        Check(!GameSpeedSettings.SupportsRuntime(fakeExe), "old EXE not approved by filename");
        Check(!GameSpeedSettings.SupportsRuntime(fakeExe + ".missing"), "missing EXE rejected");
        Console.WriteLine($"Game-speed v18 regression checks passed: {_checks}.");
    }
    private sealed class ShortReadStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 7));
    }
}
