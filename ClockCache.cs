using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace StardewDS
{
    /// <summary>
    /// Crops the two sprites the vanilla clock/day box
    /// (<c>StardewValley.Menus.DayTimeMoneyBox</c>) draws itself — read
    /// from the real decompiled <c>DayTimeMoneyBox.draw</c> before
    /// writing this (same source that already gave the season/weather
    /// badge rects in <see cref="SeasonWeatherIconCache"/>):
    ///
    /// <list type="bullet">
    /// <item>The wood-and-parchment box backdrop —
    /// <c>Rectangle(333, 431, 71, 43)</c> on <see cref="Game1.mouseCursors"/>.</item>
    /// <item>A single sundial-style needle — <c>Rectangle(324, 477, 7, 19)</c>,
    /// pivoting near its base (origin (3, 17) in the real draw call) and
    /// sweeping a half circle (rotation <c>PI</c> to <c>2*PI</c>) from 6am
    /// to roughly 2am.</item>
    /// </list>
    ///
    /// Notably there is no 12-hour analog clock face anywhere in the
    /// vanilla game — just this box, plain digital time text, and the one
    /// needle. An earlier version of the companion app's clock invented a
    /// full analog dial with hour/minute hands; this replaces that with
    /// the app rendering the real sprites the same way the game does.
    /// </summary>
    internal static class ClockCache
    {
        private static byte[]? _box;
        private static byte[]? _needle;

        /// <summary>The cached box/needle PNGs, or null if not rendered yet. Safe to call from any thread.</summary>
        public static byte[]? TryGetBox() => _box;
        public static byte[]? TryGetNeedle() => _needle;

        /// <summary>Crops and caches both sprites once — cheap no-op after that (neither ever changes). Main-thread only (touches the graphics device).</summary>
        public static void EnsureCached(GraphicsDevice device)
        {
            _box ??= Crop(new Rectangle(333, 431, 71, 43), device);
            _needle ??= Crop(new Rectangle(324, 477, 7, 19), device);
        }

        private static byte[] Crop(Rectangle sourceRect, GraphicsDevice device)
        {
            var pixels = new Color[sourceRect.Width * sourceRect.Height];
            Game1.mouseCursors.GetData(0, sourceRect, pixels, 0, pixels.Length);

            using Texture2D cropped = new(device, sourceRect.Width, sourceRect.Height);
            cropped.SetData(pixels);

            using MemoryStream ms = new();
            cropped.SaveAsPng(ms, sourceRect.Width, sourceRect.Height);
            return ms.ToArray();
        }
    }
}
