using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace Fable2Launcher;

public sealed record AchievementInfo(int Id, int Points, string Name, string Description);

/// <summary>
/// Fable II achievement catalog (embedded, taken from the title's XDBF data)
/// plus the unlock state the ReXGlue runtime writes to
/// saves/achievements/4D5307F1.toml next to fable_2.exe.
/// </summary>
public static class Achievements
{
    public const string TitleId = "4D5307F1";

    private static IReadOnlyList<AchievementInfo>? _catalog;

    public static IReadOnlyList<AchievementInfo> Catalog => _catalog ??= LoadCatalog();

    private static IReadOnlyList<AchievementInfo> LoadCatalog()
    {
        try
        {
            var info = Application.GetResourceStream(
                new Uri("pack://application:,,,/Resources/V2/achievements.json", UriKind.Absolute));
            if (info is null) return Array.Empty<AchievementInfo>();
            using Stream stream = info.Stream;
            using JsonDocument document = JsonDocument.Parse(stream);
            var list = new List<AchievementInfo>();
            foreach (JsonElement item in document.RootElement.GetProperty("achievements").EnumerateArray())
                list.Add(new AchievementInfo(
                    item.GetProperty("id").GetInt32(),
                    item.GetProperty("points").GetInt32(),
                    item.GetProperty("name").GetString() ?? "",
                    item.GetProperty("description").GetString() ?? ""));
            return list;
        }
        catch (Exception error)
        {
            LauncherStartupDiagnostics.Report("Achievement catalog", error);
            return Array.Empty<AchievementInfo>();
        }
    }

    public static string StatePath(string executableDirectory) =>
        Path.Combine(executableDirectory, "saves", "achievements", TitleId + ".toml");

    private static readonly Regex Section = new(@"^\[\s*unlocked\.(\d+)\s*\]$", RegexOptions.Compiled);
    private static readonly Regex FileTime = new(@"^filetime\s*=\s*(\d+)$", RegexOptions.Compiled);

    /// <summary>Achievement id → local unlock time. Missing file = nothing unlocked.</summary>
    public static Dictionary<int, DateTime?> ReadUnlocked(string executableDirectory)
    {
        var result = new Dictionary<int, DateTime?>();
        string path = StatePath(executableDirectory);
        try
        {
            if (!File.Exists(path)) return result;
            int? current = null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                Match section = Section.Match(line);
                if (section.Success)
                {
                    current = int.Parse(section.Groups[1].Value, CultureInfo.InvariantCulture);
                    result.TryAdd(current.Value, null);   // a section alone means unlocked
                    continue;
                }
                if (line.StartsWith('[')) { current = null; continue; }
                Match time = FileTime.Match(line);
                if (current is int id && time.Success &&
                    long.TryParse(time.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long fileTime) &&
                    fileTime > 0)
                {
                    try { result[id] = DateTime.FromFileTimeUtc(fileTime).ToLocalTime(); }
                    catch (ArgumentOutOfRangeException) { }
                }
            }
        }
        catch (Exception error)
        {
            LauncherStartupDiagnostics.Report("Achievement state " + path, error);
        }
        return result;
    }
}
