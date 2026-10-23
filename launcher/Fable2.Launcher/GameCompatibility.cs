using System.IO;
using System.Security.Cryptography;
using Fable2Launcher.Constants;

namespace Fable2Launcher;

public sealed record GameCompatibility(string Label, string Message, bool Supported, string? Profile = null);

public static class GameCompatibilityInspector
{
    public static GameCompatibility Inspect(string directory)
    {
        string xex = Path.Combine(directory, "default.xex");
        if (!File.Exists(xex))
            return new("Файлы игры не найдены", "В выбранной папке игры не найден default.xex.", false);
        using FileStream input = File.OpenRead(xex);
        string hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        return Classify(hash,
            path => File.Exists(Path.Combine(directory, path)),
            path => Directory.Exists(Path.Combine(directory, path)));
    }

    /// <summary>
    /// Hash-only gate for disc extraction: is this default.xex a build this
    /// recompile supports? Only the XEX hash is known before extraction, so
    /// content markers are not checked here — Inspect() verifies them once
    /// the files exist.
    /// </summary>
    public static (bool Ok, string Message) CheckHash(string hash)
    {
        foreach (GameVersion version in GameVersions.All)
        {
            if (!string.Equals(hash, version.Hash, StringComparison.OrdinalIgnoreCase)) continue;
            if (!version.Compatible)
                return (false, version.Reason);
            if (version.CodeGroup != GameVersions.CodeGroup)
                return (false, "Обнаружена " + version.Name + ", но группа гостевого кода этой версии не включена в данную сборку.");
            return (true, version.Name);
        }
        return (false, "Неизвестный SHA-256 файла default.xex: " + hash +
            ". Проект принимает только проверенные версии, поэтому извлечение не продолжено.");
    }

    // Pure, catalogue-driven classification; tests supply marker predicates.
    // Adding a validated locale does not require a new hash/locale if-branch.
    public static GameCompatibility Classify(string hash, Func<string, bool> hasFile,
        Func<string, bool> hasDirectory, IReadOnlyList<GameVersion>? versions = null)
    {
        foreach (GameVersion version in versions ?? GameVersions.All)
        {
            if (!string.Equals(hash, version.Hash, StringComparison.OrdinalIgnoreCase)) continue;
            if (!version.Compatible)
                return new("Обнаружена " + version.Name, version.Reason, false);
            if (version.CodeGroup != GameVersions.CodeGroup)
                return new("Обнаружена " + version.Name,
                    "Группа гостевого кода этой версии не включена в данную сборку.", false);
            bool hasRejectionRule = version.RejectFiles.Length + version.RejectDirectories.Length > 0;
            if (hasRejectionRule && version.RejectFiles.All(hasFile) &&
                version.RejectDirectories.All(hasDirectory))
                return new("Обнаружена несовместимая смесь файлов",
                    "Этот XEX используется с несовместимыми файлами retail/TU1. Используйте полный комплект файлов одной версии.", false);
            if (!version.RequiredFiles.All(hasFile))
                return new("Неполный комплект файлов " + version.Name,
                    "Отсутствуют необходимые данные или локализация. Выберите полный комплект файлов соответствующей версии.", false);
            return new("Обнаружена " + version.Name, version.Reason, true, version.Id);
        }
        return new("Неизвестная версия XEX", $"Неизвестный SHA-256 файла default.xex: {hash}", false);
    }
}
