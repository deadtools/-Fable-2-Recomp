using System;
using System.IO;
using System.Text;

namespace Fable2Launcher;

public enum ControllerPromptMode { Xbox360, PlayStation, KeyboardMouse }

/// <summary>The prompt graphics mode does not change Xbox guest input semantics.</summary>
public static class ControllerPromptSettings
{
    public static string StateFilePath => Path.Combine(AppContext.BaseDirectory, "launcher-control-mode.txt");

    public static ControllerPromptMode Load()
    {
        try
        {
            if (File.Exists(StateFilePath))
            {
                string state = File.ReadAllText(StateFilePath).Trim();
                return state.ToLowerInvariant() switch
                {
                    "playstation" or "ps" => ControllerPromptMode.PlayStation,
                    "keyboardmouse" or "keyboard" or "kbm" => ControllerPromptMode.KeyboardMouse,
                    _ => ControllerPromptMode.Xbox360,
                };
            }
            // Migrate existing users' DualShock checkbox setting.
            string legacy = Path.Combine(AppContext.BaseDirectory, "launcher-dualshock.txt");
            if (File.Exists(legacy))
            {
                string value = File.ReadAllText(legacy).Trim();
                if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase))
                    return ControllerPromptMode.PlayStation;
            }
            return ControllerPromptMode.Xbox360;
        }
        catch (IOException) { return ControllerPromptMode.Xbox360; }
        catch (UnauthorizedAccessException) { return ControllerPromptMode.Xbox360; }
    }

    public static void Save(ControllerPromptMode mode)
    {
        string value = mode switch
        {
            ControllerPromptMode.PlayStation => "playstation",
            ControllerPromptMode.KeyboardMouse => "keyboardmouse",
            _ => "xbox360"
        };
        string temp = StateFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, value + Environment.NewLine, new UTF8Encoding(false));
            File.Move(temp, StateFilePath, true);
            // Keep the old file in sync in case a user restores an older launcher.
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launcher-dualshock.txt"),
                mode == ControllerPromptMode.PlayStation ? "true" : "false");
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
