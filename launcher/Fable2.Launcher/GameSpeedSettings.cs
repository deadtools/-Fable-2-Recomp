using System;
using System.IO;
using System.Globalization;
using System.Text;

namespace Fable2Launcher;

public static class GameSpeedSettings
{
    public const double Default = 1.0;
    public const string CapabilityMarker = "FABLE2_GAME_SPEED_V18_GUEST_CLOCK";
    public static double Normalize(double value) => !double.IsFinite(value) ? Default :
        Math.Round(Math.Clamp(value, 0.1, 2.0) * 10.0, MidpointRounding.AwayFromZero) / 10.0;
    public static double Parse(string? text) => double.TryParse(text?.Trim(),
        NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? Normalize(value) : Default;
    public static string Serialize(double value) => Normalize(value).ToString("0.0", CultureInfo.InvariantCulture);
    public static int ToStep(double value) => (int)Math.Round(Normalize(value) * 10.0);
    public static double FromStep(double step) => Normalize(step / 10.0);

    // Inspect the actual EXE. A JSON capability flag could falsely bless an old binary.
    public static bool SupportsRuntime(string executable)
    {
        try
        {
            using var input = new FileStream(executable, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return ContainsMarker(input);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    internal static bool ContainsMarker(Stream input)
    {
        byte[] marker = Encoding.ASCII.GetBytes(CapabilityMarker);
        byte[] buffer = new byte[64 * 1024 + marker.Length - 1];
        int carry = 0;
        int read;
        while ((read = input.Read(buffer, carry, buffer.Length - carry)) > 0)
        {
            int length = carry + read;
            if (buffer.AsSpan(0, length).IndexOf(marker.AsSpan()) >= 0) return true;
            carry = Math.Min(marker.Length - 1, length);
            Buffer.BlockCopy(buffer, length - carry, buffer, 0, carry);
        }
        return false;
    }
}
