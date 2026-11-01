using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fable2Launcher;

/// <summary>
/// Builds circular keyboard/mouse prompts on the UI (STA) thread, when the user
/// launches the game. GameFace still receives its original Xbox guest actions.
/// Never generate every frame; the texture banks are prepared before launch.
/// </summary>
public static class KeyboardMousePromptArtwork
{
    public const string BadgeResourceName = @"Art\GUI\Frames\frames_elements.tex";
    public const int BadgeWidth = 512;
    public const int AtlasWidth = 256;

    // ABYX texture coordinates are A,B,Y,X (not A,B,X,Y).
    public static readonly string[] AtlasTargets = { "A", "B", "Y", "X" };
    private static readonly string[] SupportedGuestButtons =
        { "A","B","X","Y","LB","RB","LT","RT","Pause","Select", "Up","Down","Left","Right","L3","R3" };

    public static Dictionary<string, string> ResolveBindings(string keyboardGamepadMap)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var strengths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Fable2ConfigFile.ParseKeybinds(keyboardGamepadMap))
        {
            if (!Fable2ConfigFile.ButtonNames.Contains(entry.Button, StringComparer.OrdinalIgnoreCase) ||
                !Fable2ConfigFile.KeyNames.Contains(entry.Key, StringComparer.OrdinalIgnoreCase))
                continue;
            string guest = entry.Button.Trim();
            string key = entry.Key.Trim();
            int strength = Strength(guest, key);
            if (!strengths.TryGetValue(guest, out int old) || strength > old)
            {
                result[guest] = key;
                strengths[guest] = strength;
            }
        }
        return result;
    }

    private static int Strength(string guest, string key)
    {
        // Prefer action keys over modifiers for A (default has Shift and E).
        if (guest.Equals("A", StringComparison.OrdinalIgnoreCase) &&
            key.Equals("E", StringComparison.OrdinalIgnoreCase)) return 400;
        if (guest.Equals("X", StringComparison.OrdinalIgnoreCase) &&
            key.Equals("LMB", StringComparison.OrdinalIgnoreCase)) return 400;
        if (guest.Equals("Y", StringComparison.OrdinalIgnoreCase) &&
            key.Equals("RMB", StringComparison.OrdinalIgnoreCase)) return 400;
        if (key is "Shift" or "Control" or "Alt") return 5;
        if (key is "LMB" or "RMB" or "MMB" or "XMB1" or "XMB2") return 100;
        if (key.Length == 1) return 80;
        return 60;
    }

    public static string DisplayKey(string key)
    {
        return key.ToUpperInvariant() switch
        {
            "LMB" => "LMB", "RMB" => "RMB", "MMB" => "MMB",
            "XMB1" => "M4", "XMB2" => "M5",
            "ESCAPE" => "ESC", "RETURN" => "ENTER", "SPACE" => "SPACE",
            "CONTROL" => "CTRL", "BACKSPACE" => "BKSP",
            "PAGEUP" => "PGUP", "PAGEDOWN" => "PGDN",
            "NUMPADENTER" => "NUM↵", "NUMPADPLUS" => "NUM+",
            "NUMPADMINUS" => "NUM−", "NUMPADSTAR" => "NUM*",
            "NUMPADSLASH" => "NUM/", "CAPSLOCK" => "CAPS",
            "PRINTSCREEN" => "PRT", "NUMLOCK" => "NUML",
            "SCROLLLOCK" => "SCRL", "BACKTICK" => "`",
            "COMMA" => ",", "PERIOD" => ".", "SEMICOLON" => ";",
            "SLASH" => "/", "BACKSLASH" => "\\", "LBRACKET" => "[",
            "RBRACKET" => "]", "QUOTE" => "'", "MINUS" => "−", "PLUS" => "+",
            "UP" => "↑", "DOWN" => "↓", "LEFT" => "←", "RIGHT" => "→",
            _ when key.StartsWith("Numpad", StringComparison.OrdinalIgnoreCase) => "N" + key[6..].ToUpperInvariant(),
            _ => key.ToUpperInvariant()
        };
    }

    public static string Label(IReadOnlyDictionary<string, string> map, string guest) =>
        map.TryGetValue(guest, out string? key) ? DisplayKey(key) : "—";

    // The approved Fable-style mouse ornaments are embedded in the EXE. Never
    // read them from the installation folder (single-file publish compatibility).
    public static string? MouseIconAssetFor(string? label) => label?.Trim().ToUpperInvariant() switch
    {
        "LMB" => "LMB",  // left mouse button
        "MMB" => "MMB",  // middle mouse button
        "RMB" => "RMB",  // right mouse button
        _ => null
    };

    private static readonly Dictionary<string, BitmapSource> MouseIcons =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object MouseIconsGate = new();

    private static BitmapSource LoadMouseIcon(string button)
    {
        lock (MouseIconsGate)
        {
            if (MouseIcons.TryGetValue(button, out BitmapSource? existing))
                return existing;
            // Component-qualified pack URI also works for the linked test assembly.
            string assembly = typeof(KeyboardMousePromptArtwork).Assembly.GetName().Name!;
            var uri = new Uri($"pack://application:,,,/{assembly};component/Resources/MouseButtons/{button}.png");
            var info = Application.GetResourceStream(uri) ??
                throw new FileNotFoundException($"The {button} mouse icon is missing from launcher resources: {uri}");
            using var stream = info.Stream;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            if (image.PixelWidth < 64 || image.PixelHeight < 64)
                throw new InvalidDataException($"Invalid mouse icon resolution: {button}");
            image.Freeze();
            MouseIcons[button] = image;
            return image;
        }
    }

    private static void DrawMouseIcon(DrawingContext dc, string asset, int size)
    {
        // No extra circular plate/rim: the PNG already contains the gold ring
        // and its natural antialiased alpha edge.
        dc.DrawImage(LoadMouseIcon(asset), new Rect(0, 0, size, size));
    }

    /// <summary>Outputs Xbox ARGB logical texels, not PNG pixels or BGRA.</summary>
    public static byte[] RenderCircularKey(string label, int size)
    {
        if (size < 16 || size > 512) throw new ArgumentOutOfRangeException(nameof(size));
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            string normalized = string.IsNullOrWhiteSpace(label) ? "—" : label.Trim();
            string? mouseAsset = MouseIconAssetFor(normalized);
            if (mouseAsset is not null)
            {
                DrawMouseIcon(dc, mouseAsset, size);
            }
            else
            {
                Point center = new(size / 2.0, size / 2.0);
                double outer = size * .465;
                double inner = size * .400;
                // Anti-aliased transparent corners; circular dark plate and thin rim.
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xD9, 0xDF, 0xEB)), null,
                    center, outer, outer);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x1A, 0x2B, 0x3E)), null,
                    center, inner, inner);
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(0x63, 0xBA, 0xD5)),
                    Math.Max(1.0, size * .025)), center, inner - size * .014, inner - size * .014);
    
                var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
                    FontWeights.Bold, FontStretches.Normal);
                double font = Math.Max(8, size * .35);
                FormattedText text;
                do
                {
                    text = new FormattedText(normalized, CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, typeface, font, Brushes.White, 1.0);
                    if ((text.WidthIncludingTrailingWhitespace < size * .68 && text.Height < size * .50) || font <= 8)
                        break;
                    font -= 1;
                } while (true);
                dc.DrawText(text, new Point((size - text.WidthIncludingTrailingWhitespace) / 2,
                    (size - text.Height) / 2 - size * .015));
            }
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        byte[] bgra = new byte[size * size * 4];
        bitmap.CopyPixels(bgra, size * 4, 0);
        return BgraToArgb(bgra);
    }

    private static byte[] BgraToArgb(byte[] bgra)
    {
        byte[] argb = new byte[bgra.Length];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte a = bgra[i + 3];
            argb[i] = a;
            // Bitmap is premultiplied BGRA, while the game uses straight ARGB.
            argb[i + 1] = a == 0 ? (byte)0 : (byte)Math.Min(255, bgra[i + 2] * 255 / a);
            argb[i + 2] = a == 0 ? (byte)0 : (byte)Math.Min(255, bgra[i + 1] * 255 / a);
            argb[i + 3] = a == 0 ? (byte)0 : (byte)Math.Min(255, bgra[i] * 255 / a);
        }
        return argb;
    }

    private static byte[] StraightBgraToArgb(byte[] bgra)
    {
        byte[] argb = new byte[bgra.Length];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            argb[i] = bgra[i + 3];
            argb[i + 1] = bgra[i + 2];
            argb[i + 2] = bgra[i + 1];
            argb[i + 3] = bgra[i];
        }
        return argb;
    }

    public static byte[] CreateAtlas(IReadOnlyDictionary<string, string> bindings)
    {
        byte[] atlas = DualShockAtlasArtwork.GetLogicalArgb();
        byte[] originalBottom = atlas.AsSpan(128 * 256 * 4).ToArray();
        for (int row = 0; row < 2; row++)
            for (int col = 0; col < AtlasTargets.Length; col++)
            {
                byte[] icon = RenderCircularKey(Label(bindings, AtlasTargets[col]), 64);
                for (int y = 0; y < 64; y++)
                    Buffer.BlockCopy(icon, y * 64 * 4, atlas,
                        ((row * 64 + y) * 256 + col * 64) * 4, 64 * 4);
            }
        // HOLD/UI masks are not the button grid: never change them.
        if (!atlas.AsSpan(128 * 256 * 4).SequenceEqual(originalBottom))
            throw new InvalidDataException("HOLD animation pixels were changed.");
        return atlas;
    }

    public static byte[] CreateMenuBadge(IReadOnlyDictionary<string, string> bindings)
    {
        var uri = new Uri("pack://application:,,,/Resources/frames_elements_original.png", UriKind.Absolute);
        var info = Application.GetResourceStream(uri) ??
            throw new FileNotFoundException("Original frames_elements bitmap resource was not packaged.");
        using var stream = info.Stream;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        if (image.PixelWidth != BadgeWidth || image.PixelHeight != BadgeWidth)
            throw new InvalidDataException("Invalid MenuHighlight badge image dimensions.");
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] bgra = new byte[BadgeWidth * BadgeWidth * 4];
        converted.CopyPixels(bgra, BadgeWidth * 4, 0);
        // PNG decoding yields straight (not premultiplied) BGRA; the original
        // non-button frame textures must retain their exact RGB and alpha.
        byte[] argb = StraightBgraToArgb(bgra);
        // Original GameFace badges have their own round ornament/rim.
        // Replacing the whole rectangle with another 54/81px round button
        // produced a second, offset circle in the frontend (reported as "N E").
        // Erase ONLY the painted letter/inner face; keep the original border,
        // alpha, positioning and all neighbouring Frame elements byte-identical.
        ReplaceBadgeFace(argb, 37.5, 393.0, 21.5, Label(bindings, "A"));
        ReplaceBadgeFace(argb, 67.5, 216.0, 21.5, Label(bindings, "B"));
        return argb;
    }

    private static void ReplaceBadgeFace(byte[] atlas, double cx, double cy, double radius, string label)
    {
        const int side = 48;
        // Preserve the original green/red rim and all nearby materials. Only
        // the fully opaque central disc is covered with a dark-blue gradient.
        int left = Math.Max(0, (int)Math.Floor(cx - radius - 1));
        int top = Math.Max(0, (int)Math.Floor(cy - radius - 1));
        int right = Math.Min(BadgeWidth - 1, (int)Math.Ceiling(cx + radius + 1));
        int bottom = Math.Min(BadgeWidth - 1, (int)Math.Ceiling(cy + radius + 1));
        for (int y = top; y <= bottom; y++)
        for (int x = left; x <= right; x++)
        {
            double dx = (x + .5) - cx, dy = (y + .5) - cy;
            double coverage = Math.Clamp(radius + .5 - Math.Sqrt(dx * dx + dy * dy), 0, 1);
            int i = (y * BadgeWidth + x) * 4;
            if (coverage < .001 || atlas[i] < 128) continue;
            double highlight = Math.Clamp((cy - y) / radius, -.8, .8);
            int r = (int)(24 + highlight * 7), g = (int)(42 + highlight * 9), b = (int)(60 + highlight * 11);
            atlas[i + 1] = BlendByte(atlas[i + 1], r, coverage);
            atlas[i + 2] = BlendByte(atlas[i + 2], g, coverage);
            atlas[i + 3] = BlendByte(atlas[i + 3], b, coverage);
        }

        // Draw just the caption on transparency; there is deliberately NO
        // extra surrounding circle, so the existing menu layout cannot show
        // overlapping rings when sampling this sprite at multiple sizes.
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            string? mouseAsset = MouseIconAssetFor(label);
            if (mouseAsset is not null)
            {
                DrawMouseIcon(dc, mouseAsset, side);
            }
            else
            {
                string value = string.IsNullOrWhiteSpace(label) ? "—" : label.Trim();
                var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
                    FontWeights.Bold, FontStretches.Normal);
                double font = 24.0;
                FormattedText glyph;
                do
                {
                    glyph = new FormattedText(value, CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, typeface, font, Brushes.White, 1.0);
                    if ((glyph.WidthIncludingTrailingWhitespace <= side * .72 && glyph.Height <= side * .60) || font <= 8)
                        break;
                    font -= 1;
                } while (true);
                dc.DrawText(glyph, new Point((side - glyph.WidthIncludingTrailingWhitespace) / 2,
                    (side - glyph.Height) / 2 - .7));
            }
        }
        var bitmap = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        byte[] bgra = new byte[side * side * 4];
        bitmap.CopyPixels(bgra, side * 4, 0);
        byte[] glyphArgb = BgraToArgb(bgra);
        int ox = (int)Math.Round(cx - side / 2.0), oy = (int)Math.Round(cy - side / 2.0);
        double clipRadius = radius - (MouseIconAssetFor(label) is null ? 3.0 : 0.6);
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
        {
            int xx = ox + x, yy = oy + y;
            if (xx < 0 || xx >= BadgeWidth || yy < 0 || yy >= BadgeWidth) continue;
            double dx = xx + .5 - cx, dy = yy + .5 - cy;
            if (dx * dx + dy * dy >= clipRadius * clipRadius) continue;
            int source = (y * side + x) * 4, dest = (yy * BadgeWidth + xx) * 4;
            double coverage = glyphArgb[source] / 255.0;
            if (coverage <= 0 || atlas[dest] < 128) continue;
            atlas[dest + 1] = BlendByte(atlas[dest + 1], glyphArgb[source + 1], coverage);
            atlas[dest + 2] = BlendByte(atlas[dest + 2], glyphArgb[source + 2], coverage);
            atlas[dest + 3] = BlendByte(atlas[dest + 3], glyphArgb[source + 3], coverage);
        }
    }

    private static byte BlendByte(byte original, int replacement, double opacity) =>
        (byte)Math.Clamp((int)Math.Round(original * (1 - opacity) + replacement * opacity), 0, 255);
}
