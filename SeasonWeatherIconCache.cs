using System.Collections.Concurrent;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace StardewDS
{
    /// <summary>
    /// Crops the season and weather icons the vanilla clock HUD itself
    /// draws — see the actual decompiled
    /// <c>StardewValley.Menus.DayTimeMoneyBox.draw</c> (fetched from
    /// github.com/veywrn/StardewValley and read before writing this, same
    /// verify-first approach as <see cref="PortraitRenderer"/> and
    /// <see cref="UiIconCache"/>) — straight out of the game's own
    /// <see cref="Game1.mouseCursors"/> spritesheet:
    ///
    /// <list type="bullet">
    /// <item>Season: <c>Rectangle(406, 441 + seasonNumber * 8, 12, 8)</c>,
    /// where seasonNumber comes from <c>Utility.getSeasonNumber</c>
    /// (spring=0, summer=1, fall=2, winter=3).</item>
    /// <item>Weather: <c>Rectangle(317 + 12 * weatherIcon, 421, 12, 8)</c>,
    /// where weatherIcon is read directly from the game's own
    /// <see cref="Game1.weatherIcon"/> field — deliberately NOT
    /// re-derived from the isRaining/isSnowing/etc. flags this mod
    /// already tracks for the <c>Weather</c> string, since the real
    /// <c>Game1.updateWeatherIcon()</c> logic turned out to be more
    /// involved than those flags alone (e.g. it special-cases festival
    /// days and weddings) — reading the game's own already-computed
    /// value sidesteps re-implementing that.</item>
    /// </list>
    ///
    /// Both are tiny (12x8) source crops, cached forever once cropped —
    /// there are only 4 seasons and a handful of weather codes. Main
    /// thread only (touches the graphics device).
    /// </summary>
    internal static class SeasonWeatherIconCache
    {
        private static readonly ConcurrentDictionary<string, byte[]> Cache = new();

        public static byte[]? TryGetSeason(int seasonNumber) =>
            Cache.TryGetValue($"season{seasonNumber}", out byte[]? bytes) ? bytes : null;

        public static byte[]? TryGetWeather(int weatherIcon) =>
            Cache.TryGetValue($"weather{weatherIcon}", out byte[]? bytes) ? bytes : null;

        public static void EnsureSeasonCached(int seasonNumber, GraphicsDevice device)
        {
            string key = $"season{seasonNumber}";
            if (Cache.ContainsKey(key))
                return;
            Cache[key] = Crop(new Rectangle(406, 441 + seasonNumber * 8, 12, 8), device);
        }

        public static void EnsureWeatherCached(int weatherIcon, GraphicsDevice device)
        {
            string key = $"weather{weatherIcon}";
            if (Cache.ContainsKey(key))
                return;
            Cache[key] = Crop(new Rectangle(317 + 12 * weatherIcon, 421, 12, 8), device);
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
