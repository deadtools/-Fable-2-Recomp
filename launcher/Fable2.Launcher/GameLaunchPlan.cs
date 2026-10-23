using System.IO;
using System.Text.Json;
using Fable2Launcher.Constants;

namespace Fable2Launcher;

public sealed record GameLaunchPlan(string Executable, string WorkingDirectory, string GameRoot);

public static class GameLaunchPlanner
{
    public static GameLaunchPlan Create(string executableDirectory, string gameRoot, GameCompatibility compatibility)
    {
        if (!compatibility.Supported || compatibility.Profile is null)
            throw new InvalidOperationException(compatibility.Message);
        string executable = Path.Combine(executableDirectory, "fable_2.exe");
        if (!File.Exists(executable))
            throw new InvalidOperationException("Файл fable_2.exe не найден рядом с лаунчером или в выбранной папке.");

        // Legacy executables only support USA/EU. A generated descriptor permits
        // the German or dual-GOTY executable; never silently try an old US build.
        string descriptor = Path.Combine(executableDirectory, "fable2_build.json");
        string[] profiles = [GameVersions.DefaultProfile];
        if (File.Exists(descriptor))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(descriptor));
            profiles = document.RootElement.GetProperty("profiles").EnumerateArray()
                .Select(value => value.GetString() ?? "").ToArray();
        }
        if (!profiles.Contains(compatibility.Profile, StringComparer.Ordinal))
            throw new InvalidOperationException($"Этот fable_2.exe не поддерживает профиль {compatibility.Profile}. Установите сборку Recomp с поддержкой нескольких GOTY-версий.");
        return new(Path.GetFullPath(executable), Path.GetFullPath(executableDirectory), Path.GetFullPath(gameRoot));
    }
}
