using System.IO;
using System.Text;

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
        var document = new TomlTextDocument(File.ReadAllText(path));
        foreach (var entry in document.Assignments)
            if (entry.Section == "") values[entry.Key] = entry.Value;
        return values;
    }

    public static void WriteValues(string path, IReadOnlyDictionary<string, string> settings,
        IReadOnlyList<string> keyOrder)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var document = new TomlTextDocument(File.Exists(path)
            ? File.ReadAllText(path) : "# Fable II Recomp Launcher configuration\n");
        foreach (string key in keyOrder)
            if (settings.TryGetValue(key, out string? value)) document.SetValue("", key, value);
        string backupPath = path + ".launcher-backup";
        if (File.Exists(path) && !File.Exists(backupPath)) File.Copy(path, backupPath);

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
}
