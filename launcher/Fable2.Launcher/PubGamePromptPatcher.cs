using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fable2Launcher;

/// <summary>Called on the launcher's STA UI thread, immediately before starting the game.</summary>
internal static class PubGamePromptPatcher
{
    internal static string Apply(string gameRoot, ControllerPromptMode mode, string keyMap, bool mouseLook)
    {
        var bindings = KeyboardMousePromptArtwork.ResolveBindings(keyMap);
        var cache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        byte[] Artwork(string guest)
        {
            if (!cache.TryGetValue(guest, out byte[]? pixels))
            {
                pixels = Render(guest, mode, bindings, mouseLook);
                cache.Add(guest, pixels);
            }
            return pixels;
        }
        string modeName = mode switch
        {
            ControllerPromptMode.PlayStation => "playstation",
            ControllerPromptMode.KeyboardMouse => "keyboardmouse",
            _ => "xbox360"
        };
        return PubGameFileSet.Apply(gameRoot, modeName, Artwork);
    }

    private static byte[] Render(string guest, ControllerPromptMode mode,
        IReadOnlyDictionary<string, string> bindings, bool mouseLook)
    {
        const int size = PubGameSwfPatch.IconSize;
        if (mode == ControllerPromptMode.KeyboardMouse && guest is not ("LS" or "RS"))
        {
            // Identical mapping and approved embedded LMB/MMB/RMB PNGs as the main game.
            return KeyboardMousePromptArtwork.RenderCircularKey(KeyboardMousePromptArtwork.Label(bindings, guest), size);
        }
        if (mode == ControllerPromptMode.PlayStation && guest is "LT" or "RT" or "LB" or "RB")
        {
            string label = guest switch { "LT" => "L2", "RT" => "R2", "LB" => "L1", _ => "R1" };
            return KeyboardMousePromptArtwork.RenderCircularKey(label, size);
        }
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            Point center = new(64, 64);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(217, 223, 235)), null, center, 59.5, 59.5);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(26, 43, 62)), null, center, 52, 52);
            if (mode == ControllerPromptMode.PlayStation && guest is "A" or "B" or "X" or "Y")
            {
                Color color = guest switch
                {
                    "A" => Color.FromRgb(174, 184, 229), "B" => Color.FromRgb(242, 76, 40),
                    "X" => Color.FromRgb(222, 137, 190), _ => Color.FromRgb(73, 188, 173)
                };
                var pen = new Pen(new SolidColorBrush(color), 6)
                    { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                if (guest == "A")
                {
                    dc.DrawLine(pen, new Point(41, 41), new Point(87, 87));
                    dc.DrawLine(pen, new Point(41, 87), new Point(87, 41));
                }
                else if (guest == "B") dc.DrawEllipse(null, pen, center, 28, 28);
                else if (guest == "X") dc.DrawRectangle(null, pen, new Rect(38, 38, 52, 52));
                else
                {
                    dc.DrawLine(pen, new Point(64, 34), new Point(94, 87));
                    dc.DrawLine(pen, new Point(94, 87), new Point(34, 87));
                    dc.DrawLine(pen, new Point(34, 87), new Point(64, 34));
                }
            }
            else if (guest == "LS" && mode == ControllerPromptMode.KeyboardMouse)
            {
                // LS is stick MOVEMENT, not the L3 click. Honour the four real bindings.
                Caption(dc, KeyboardMousePromptArtwork.Label(bindings, "StickUp"), 64, 33, 32, 38);
                Caption(dc, KeyboardMousePromptArtwork.Label(bindings, "StickLeft"), 32, 66, 32, 38);
                Caption(dc, KeyboardMousePromptArtwork.Label(bindings, "StickRight"), 96, 66, 32, 38);
                Caption(dc, KeyboardMousePromptArtwork.Label(bindings, "StickDown"), 64, 96, 32, 38);
            }
            else if (guest == "RS" && mode == ControllerPromptMode.KeyboardMouse)
            {
                // Right-stick axes are mouse-look in this recomp, not Mouse3/R3.
                if (!mouseLook) Caption(dc, "--", 64, 64, 32, 80);
                else
                {
                    var pen = new Pen(Brushes.WhiteSmoke, 3.5);
                    dc.DrawRoundedRectangle(null, pen, new Rect(45, 34, 38, 61), 18, 18);
                    dc.DrawLine(pen, new Point(64, 35), new Point(64, 58));
                    dc.DrawLine(pen, new Point(46, 59), new Point(82, 59));
                    dc.DrawLine(pen, new Point(21, 66), new Point(34, 66));
                    dc.DrawLine(pen, new Point(21, 66), new Point(27, 60));
                    dc.DrawLine(pen, new Point(21, 66), new Point(27, 72));
                    dc.DrawLine(pen, new Point(94, 66), new Point(107, 66));
                    dc.DrawLine(pen, new Point(107, 66), new Point(101, 60));
                    dc.DrawLine(pen, new Point(107, 66), new Point(101, 72));
                }
            }
            else
            {
                // PlayStation L/R stick, movement rather than a click binding.
                Caption(dc, guest == "LS" ? "L" : "R", 64, 60, 41, 70);
                var pen = new Pen(Brushes.WhiteSmoke, 2.5);
                dc.DrawLine(pen, new Point(43, 94), new Point(85, 94));
                dc.DrawLine(pen, new Point(43, 94), new Point(50, 88));
                dc.DrawLine(pen, new Point(85, 94), new Point(78, 88));
            }
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var bgra = new byte[size * size * 4]; bitmap.CopyPixels(bgra, size * 4, 0);
        var argb = new byte[bgra.Length];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int alpha = bgra[i + 3]; argb[i] = (byte)alpha;
            argb[i + 1] = alpha == 0 ? (byte)0 : (byte)Math.Min(255, (bgra[i + 2] * 255 + alpha / 2) / alpha);
            argb[i + 2] = alpha == 0 ? (byte)0 : (byte)Math.Min(255, (bgra[i + 1] * 255 + alpha / 2) / alpha);
            argb[i + 3] = alpha == 0 ? (byte)0 : (byte)Math.Min(255, (bgra[i] * 255 + alpha / 2) / alpha);
        }
        return argb;
    }

    private static void Caption(DrawingContext dc, string label, double x, double y, double size, double width)
    {
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, size, Brushes.WhiteSmoke, 1.0);
        while (text.WidthIncludingTrailingWhitespace > width && size > 8)
        {
            size -= 1;
            text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, size, Brushes.WhiteSmoke, 1.0);
        }
        dc.DrawText(text, new Point(x - text.WidthIncludingTrailingWhitespace / 2, y - text.Height / 2));
    }
}
