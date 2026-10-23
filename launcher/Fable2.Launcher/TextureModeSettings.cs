using System.Diagnostics;
using System.IO;
using System.Text;

namespace Fable2Launcher;

public enum HeroDogTextureMode { GpuRealtime, CompatibilityReadback, DiagnosticOff }

public static class TextureModeSettings
{
    public const string ReadbackAddress = "0x12704000";

    public static HeroDogTextureMode Get(Fable2ConfigValues values) =>
        values.RealtimeTextureMorphing ? HeroDogTextureMode.GpuRealtime :
        values.HeroDogTextureReadback ? HeroDogTextureMode.CompatibilityReadback :
        HeroDogTextureMode.DiagnosticOff;

    public static void Apply(Fable2ConfigValues values, HeroDogTextureMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        values.RealtimeTextureMorphing = mode == HeroDogTextureMode.GpuRealtime;
        values.HeroDogTextureReadback = mode == HeroDogTextureMode.CompatibilityReadback;
    }

    // These are the requested settings, not proof of what a running GPU did.
    public static string DescribeRequest(string directory, Fable2ConfigValues values)
    {
        var text = new StringBuilder();
        string modeLabel = Get(values) switch
        {
            HeroDogTextureMode.GpuRealtime => "Морфинг на GPU в реальном времени",
            HeroDogTextureMode.CompatibilityReadback => "Совместимый Readback",
            _ => "Исправления отключены (диагностика)"
        };
        text.AppendLine($"Запрошенный режим: {modeLabel}");
        text.AppendLine($"realtime_texture_morphing={values.RealtimeTextureMorphing}, hero_dog_texture_readback={values.HeroDogTextureReadback}");
        if (values.RealtimeTextureMorphing && values.HeroDogTextureReadback)
            text.AppendLine("Оба старых флага включены. При сохранении останется только один; сейчас приоритет имеет GPU-режим.");
        if (Get(values) == HeroDogTextureMode.DiagnosticOff)
            text.AppendLine("ВНИМАНИЕ: исправление текстур не выбрано; герой и собака могут отображаться чёрными.");
        var sdk = LauncherConfigFile.ReadValues(Path.Combine(directory, "fable_2.toml"));
        foreach (string key in new[] { "readback_resolve_force_addresses", "readback_resolve" })
        {
            text.AppendLine($"Конфигурация SDK {key}: {(sdk.TryGetValue(key, out var v) ? v : "<не задано>")}");
            string env = "REX_" + key.ToUpperInvariant();
            text.AppendLine($"Переменная среды {env}: {Environment.GetEnvironmentVariable(env) ?? "<не задано>"}");
        }
        text.AppendLine("Это входные параметры конфигурации, а не измерение фактически применённого режима. Подтверждение во время работы появится в строках [texture-runtime] лога после пересборки игры.");
        return text.ToString();
    }

    // Explicit, opt-in session override, not a persistent edit of SDK settings.
    public static void AddReadbackOverride(ProcessStartInfo startInfo, Fable2ConfigValues values)
    {
        if (Get(values) != HeroDogTextureMode.CompatibilityReadback)
            throw new InvalidOperationException("Принудительный Readback требует режима «Совместимый Readback».");
        startInfo.ArgumentList.Add("--readback_resolve_force_addresses=" + ReadbackAddress);
    }
}
