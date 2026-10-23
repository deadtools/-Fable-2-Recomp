using System.Globalization;
using System.IO;
using System.Text;

namespace Fable2Launcher;

/// <summary>
/// The data model for fable2_config.toml (the recomp's own user config, separate
/// from the ReXGlue SDK cvar config fable_2.toml). Defaults mirror the C++
/// fable2::config::Values struct in src/core/fable2_config.h.
/// </summary>
public class Fable2ConfigValues
{
    /// <summary>Built-in default keyboard -> guest gamepad map (see the C++ header).</summary>
    public const string DefaultKeyboardGamepadMap =
        "LMB:X,RMB:Y,MMB:B,Shift:A,E:A," +
        "Q:LT,Tab:RB,R:RT,Z:LB,B:B," +
        "W:StickUp,S:StickDown,A:StickLeft,D:StickRight," +
        "Escape:Pause,M:Select," +
        "Up:Up,Down:Down,Left:Left,Right:Right";

    // [gameplay]
    public double GameSpeed { get; set; } = GameSpeedSettings.Default;

    // [input]
    public string KeyboardGamepadMap { get; set; } = DefaultKeyboardGamepadMap;
    public bool MouseLook { get; set; } = true;
    public int MouseLookScale { get; set; } = 256;  // range 1..4096 (cvar constraint)

    // [patches]
    public bool UnlockWebsite { get; set; } = true;
    public bool UnlockCe { get; set; } = true;
    public bool SkipIntroVideos { get; set; } = false;
    public bool HighTickRate { get; set; } = false;       // unstable
    public bool HigherHfTickRate { get; set; } = false;    // unstable
    public bool DisableMotionBlur { get; set; } = false;
    public bool RealtimeTextureMorphing { get; set; } = true;
    public bool HeroDogTextureReadback { get; set; } = false;

    // [perf]
    public int HotFuncYieldEvery { get; set; } = 1;  // 1 = original, 0 = never yield
}

/// <summary>One host-key -> guest-input binding (a "Key:Button" pair).</summary>
public class KeybindEntry
{
    public string Key { get; set; } = "";
    public string Button { get; set; } = "";
}

/// <summary>
/// Reads and writes fable2_config.toml non-destructively: it only updates the
/// managed keys (the [input] and [patches] values the launcher exposes),
/// preserving every comment, unknown key and unknown section in the file.
/// </summary>
public static class Fable2ConfigFile
{
    // Host key names understood by rex::ui::ParseVirtualKey (see
    // src/ui/keybinds.cpp) plus the mouse-button aliases from
    // src/input/keyboard_gamepad.h. Used to populate the key dropdowns.
    public static readonly string[] KeyNames =
    {
        "A","B","C","D","E","F","G","H","I","J","K","L","M",
        "N","O","P","Q","R","S","T","U","V","W","X","Y","Z",
        "0","1","2","3","4","5","6","7","8","9",
        "F1","F2","F3","F4","F5","F6","F7","F8","F9","F10","F11","F12","F13","F14",
        "F15","F16","F17","F18","F19","F20","F21","F22","F23","F24",
        "Backtick","Minus","Plus","Comma","Period","Semicolon","Slash","Backslash",
        "LBracket","RBracket","Quote",
        "Escape","Return","Space","Tab","Backspace","Delete","Insert","Home","End",
        "PageUp","PageDown","Left","Right","Up","Down","Shift","Control","Alt",
        "PrintScreen","Pause","CapsLock","NumLock","ScrollLock",
        "Numpad0","Numpad1","Numpad2","Numpad3","Numpad4","Numpad5","Numpad6",
        "Numpad7","Numpad8","Numpad9","NumpadEnter","NumpadPlus","NumpadMinus",
        "NumpadStar","NumpadSlash",
        "LMB","RMB","MMB","XMB1","XMB2",
    };

    // Guest-input names understood by FillGuestTarget (see
    // src/input/keyboard_gamepad.h). The canonical names are used when writing.
    public static readonly string[] ButtonNames =
    {
        "A","B","X","Y","LB","RB","LT","RT",
        "Up","Down","Left","Right","Pause","Select","L3","R3",
        "StickUp","StickDown","StickLeft","StickRight",
    };

    /// <summary>Read the fable2_config.toml values. Missing file -> built-in defaults.</summary>
    public static Fable2ConfigValues Read(string path)
    {
        var values = new Fable2ConfigValues();
        if (!File.Exists(path)) return values;

        var document = new TomlTextDocument(File.ReadAllText(path));
        string? version = document.GetValue("general", "config_version");
        if (version is not null && version != "1")
            throw new FormatException("Версия fable2_config.toml не поддерживается; настройки не изменены.");
        foreach (var entry in document.Assignments)
        {
            string section = entry.Section;
            string key = entry.Key;
            string rawValue = entry.Value;

            switch (section)
            {
                case "gameplay":
                    if (key == "game_speed") values.GameSpeed = GameSpeedSettings.Parse(rawValue);
                    break;
                case "input":
                    if (key == "keyboard_gamepad_map") values.KeyboardGamepadMap = Unquote(rawValue);
                    else if (key == "mouse_look") values.MouseLook = ParseBool(rawValue, values.MouseLook);
                    else if (key == "mouse_look_scale") values.MouseLookScale = ParseInt(rawValue, values.MouseLookScale);
                    break;
                case "patches":
                    if (key == "unlock_website") values.UnlockWebsite = ParseBool(rawValue, values.UnlockWebsite);
                    else if (key == "unlock_ce") values.UnlockCe = ParseBool(rawValue, values.UnlockCe);
                    else if (key == "skip_intro_videos") values.SkipIntroVideos = ParseBool(rawValue, values.SkipIntroVideos);
                    else if (key == "high_tick_rate") values.HighTickRate = ParseBool(rawValue, values.HighTickRate);
                    else if (key == "higher_hf_tick_rate") values.HigherHfTickRate = ParseBool(rawValue, values.HigherHfTickRate);
                    else if (key == "disable_motion_blur") values.DisableMotionBlur = ParseBool(rawValue, values.DisableMotionBlur);
                    else if (key == "realtime_texture_morphing") values.RealtimeTextureMorphing = ParseBool(rawValue, values.RealtimeTextureMorphing);
                    else if (key == "hero_dog_texture_readback") values.HeroDogTextureReadback = ParseBool(rawValue, values.HeroDogTextureReadback);
                    break;
                case "perf":
                    if (key == "hotfunc_yield_every") values.HotFuncYieldEvery = ParseInt(rawValue, values.HotFuncYieldEvery);
                    break;
            }
        }
        return values;
    }

    /// <summary>
    /// Write the managed values to fable2_config.toml non-destructively: each
    /// managed key is updated in place (keeping its line's indentation, spacing
    /// and any inline comment); managed keys missing from the file are appended
    /// to their section (creating the section if it does not exist). Every other
    /// line (comments, unknown keys, unknown sections) is left untouched. A
    /// one-time .launcher-backup copy is made before the first write.
    /// </summary>
    public static void Write(string path, Fable2ConfigValues values)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string? original = File.Exists(path) ? File.ReadAllText(path) : null;
        var document = new TomlTextDocument(original ??
            "# fable2_config.toml - пользовательская конфигурация Fable 2 Recomp\n");
        string? version = document.GetValue("general", "config_version");
        if (version is not null && version != "1")
            throw new FormatException("Версия fable2_config.toml не поддерживается; настройки не изменены.");

        var managed = new (string section, string key, string value)[]
        {
            // config_version is the format marker the game requires to load the file.
            ("general", "config_version", "1"),
            ("gameplay", "game_speed", GameSpeedSettings.Serialize(values.GameSpeed)),
            ("input", "keyboard_gamepad_map", Quote(values.KeyboardGamepadMap)),
            ("input", "mouse_look", values.MouseLook ? "true" : "false"),
            ("input", "mouse_look_scale", values.MouseLookScale.ToString(CultureInfo.InvariantCulture)),
            ("patches", "unlock_website", values.UnlockWebsite ? "true" : "false"),
            ("patches", "unlock_ce", values.UnlockCe ? "true" : "false"),
            ("patches", "skip_intro_videos", values.SkipIntroVideos ? "true" : "false"),
            ("patches", "high_tick_rate", values.HighTickRate ? "true" : "false"),
            ("patches", "higher_hf_tick_rate", values.HigherHfTickRate ? "true" : "false"),
            ("patches", "disable_motion_blur", values.DisableMotionBlur ? "true" : "false"),
            ("patches", "realtime_texture_morphing", values.RealtimeTextureMorphing ? "true" : "false"),
            ("patches", "hero_dog_texture_readback", values.HeroDogTextureReadback ? "true" : "false"),
            ("perf", "hotfunc_yield_every", values.HotFuncYieldEvery.ToString(CultureInfo.InvariantCulture)),
        };

        foreach (var (section, key, value) in managed)
            document.SetValue(section, key, value);

        SaveDocument(path, original, document);
    }

    public static void WriteGameSpeed(string path, double speed)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string? original = File.Exists(path) ? File.ReadAllText(path) : null;
        var document = new TomlTextDocument(original ?? "# Fable II configuration\n");
        string? version = document.GetValue("general", "config_version");
        if (version is not null && version != "1")
            throw new FormatException("Unsupported fable2_config.toml version; not modified.");
        document.SetValue("general", "config_version", "1");
        document.SetValue("gameplay", "game_speed", GameSpeedSettings.Serialize(speed));
        SaveDocument(path, original, document);
    }

    private static void SaveDocument(string path, string? original, TomlTextDocument document)
    {
        string backupPath = path + ".launcher-backup";
        if (original is not null && !File.Exists(backupPath)) File.Copy(path, backupPath);
        string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, document.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    /// <summary>Parse "Key:Button,Key:Button,..." into a list of bindings.</summary>
    public static List<KeybindEntry> ParseKeybinds(string map)
    {
        var entries = new List<KeybindEntry>();
        if (string.IsNullOrWhiteSpace(map)) return entries;
        foreach (string part in map.Split(','))
        {
            string trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            int colon = trimmed.IndexOf(':');
            if (colon < 0) continue;
            entries.Add(new KeybindEntry
            {
                Key = trimmed.Substring(0, colon).Trim(),
                Button = trimmed.Substring(colon + 1).Trim(),
            });
        }
        return entries;
    }

    /// <summary>Build "Key:Button,Key:Button,..." from a list of bindings.</summary>
    public static string BuildKeybinds(IEnumerable<KeybindEntry> entries)
    {
        var parts = new List<string>();
        foreach (KeybindEntry entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) && string.IsNullOrWhiteSpace(entry.Button)) continue;
            parts.Add($"{entry.Key.Trim()}:{entry.Button.Trim()}");
        }
        return string.Join(",", parts);
    }

    // ---- value (de)serialization helpers ----

    private static bool ParseBool(string value, bool fallback)
    {
        value = value.Trim();
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ? true
             : value.Equals("false", StringComparison.OrdinalIgnoreCase) ? false
             : fallback;
    }

    private static int ParseInt(string value, int fallback)
    {
        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int parsed) ? parsed : fallback;
    }

    private static string Quote(string value) => TomlTextDocument.Quote(value);
    private static string Unquote(string value) => TomlTextDocument.Unquote(value);
}
