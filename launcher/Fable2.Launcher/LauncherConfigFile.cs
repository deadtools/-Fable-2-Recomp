using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Fable2Launcher;

public static class LauncherConfigFile
{
    public static Dictionary<string, string> ReadLauncherValues(string directory)
    {
        var values = ReadValues(Path.Combine(directory, "fable_2.toml"));
        foreach ((string key, string value) in ReadValues(Path.Combine(directory, "launcher-settings.toml")))
            if (GraphicsSettings.ManagedKeys.Contains(key)) values[key] = value;
        return values;
    }

    public static Dictionary<string, string> ReadValues(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return values;
        string text = ReadConfigText(path, out _);
        text = RepairSdkGeneratedWindowsPaths(text, out _);
        var document = new TomlTextDocument(text);
        foreach (var entry in document.Assignments)
            if (entry.Section == "") values[entry.Key] = entry.Value;
        return values;
    }

    // Valid launcher/SDK TOML is UTF-8 text and cannot contain NUL bytes.
    // Older launcher builds could preserve a block of leading NUL bytes that
    // had already appeared in fable_2.toml / launcher-settings.toml. The SDK
    // then ignored the file and silently fell back to defaults (notably 1x
    // resolution scale). Read through that corruption, and rewrite clean text
    // on the next normal SaveConfiguration call.
    private static string ReadConfigText(string path, out bool hadNulBytes)
    {
        byte[] bytes = File.ReadAllBytes(path);
        hadNulBytes = Array.IndexOf(bytes, (byte)0) >= 0;
        if (bytes.Length >= 2 &&
            ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            throw new FormatException("UTF-16 TOML is not supported; configuration was not modified.");
        if (hadNulBytes) bytes = bytes.Where(value => value != 0).ToArray();
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new FormatException("Configuration is not valid UTF-8; it was not modified.", ex);
        }
    }

    public static void WriteValues(string path, IReadOnlyDictionary<string, string> settings,
        IReadOnlyList<string> keyOrder)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        bool hadNulBytes = false;
        string existingText = File.Exists(path)
            ? ReadConfigText(path, out hadNulBytes)
            : "# Fable II Recomp Launcher configuration\n";
        existingText = RepairSdkGeneratedWindowsPaths(existingText, out bool repairedSdkPath);
        var document = new TomlTextDocument(existingText);
        foreach (string key in keyOrder)
            if (settings.TryGetValue(key, out string? value)) document.SetValue("", key, value);
        string backupPath = path + ".launcher-backup";
        if (File.Exists(path) && !File.Exists(backupPath)) File.Copy(path, backupPath);
        if (hadNulBytes && File.Exists(path))
        {
            string recoveryBackup = path + ".nul-recovery-backup";
            if (!File.Exists(recoveryBackup)) File.Copy(path, recoveryBackup);
        }
        if (repairedSdkPath && File.Exists(path))
        {
            string recoveryBackup = path + ".sdk-path-recovery-backup";
            if (!File.Exists(recoveryBackup)) File.Copy(path, recoveryBackup);
        }

        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, document.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
    // ReXGlue currently writes game_data_root using a raw Windows path, for
    // example: game_data_root = "D:\Fable2\Game\". In TOML every backslash
    // inside a basic quoted string is an escape character, and the trailing
    // backslash escapes the closing quote. That makes the SDK-generated file
    // invalid and previously prevented the launcher from saving graphics
    // settings on the next run. Repair only this SDK-owned path assignment,
    // preserving all other text verbatim. Existing escaped paths are left
    // unchanged; only odd-length backslash runs are escaped.
    private static string RepairSdkGeneratedWindowsPaths(string text, out bool repaired)
    {
        repaired = false;
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        bool finalNewline = text.EndsWith("\n", StringComparison.Ordinal);
        string[] lines = Regex.Split(text, "\r\n|\n");
        int count = lines.Length;
        if (finalNewline && count > 0 && lines[count - 1].Length == 0) count--;

        var pattern = new Regex(@"^(\s*game_data_root\s*=\s*)""(.*)""(\s*(?:#.*)?)$");
        for (int i = 0; i < count; i++)
        {
            Match match = pattern.Match(lines[i]);
            if (!match.Success) continue;
            string inner = match.Groups[2].Value;
            if (!inner.Contains('\\')) continue;

            var escaped = new StringBuilder(inner.Length + 8);
            bool changed = false;
            for (int p = 0; p < inner.Length;)
            {
                if (inner[p] != '\\')
                {
                    escaped.Append(inner[p++]);
                    continue;
                }
                int start = p;
                while (p < inner.Length && inner[p] == '\\') p++;
                int run = p - start;
                escaped.Append('\\', run);
                if ((run & 1) != 0)
                {
                    escaped.Append('\\');
                    changed = true;
                }
            }
            if (!changed) continue;
            lines[i] = match.Groups[1].Value + "\"" + escaped + "\"" + match.Groups[3].Value;
            repaired = true;
        }

        if (!repaired) return text;
        string result = string.Join(newline, lines.Take(count));
        return finalNewline ? result + newline : result;
    }

}
