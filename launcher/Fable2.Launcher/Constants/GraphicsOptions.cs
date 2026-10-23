namespace Fable2Launcher.Constants;

public sealed record FrameLimitOption(string Value, string Label);
public static class GraphicsOptions
{
    // Host caps, not claims that every scene is timing-safe above 60 FPS.
    public static readonly FrameLimitOption[] FrameLimits =
    [
        new("30", "Лимит 30 FPS"), new("60", "Лимит 60 FPS"),
        new("120", "Лимит 120 FPS — экспериментально"), new("144", "Лимит 144 FPS — экспериментально"),
        new("165", "Лимит 165 FPS — экспериментально"), new("240", "Лимит 240 FPS — экспериментально"),
        new("0", "Без ограничений — экспериментально")
    ];
}
