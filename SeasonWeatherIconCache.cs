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
    /// <item>Weather: <c>Rectangle(317 + 12 * weatherIcon, 421, 12, 8)</c>
    /// out of <see cref="Game1.mouseCursors"/> for every normal weather
    /// code, except green rain (<c>weatherIcon == 999</c>), which the
    /// vanilla <c>DayTimeMoneyBox.draw</c> special-cases to
    /// <c>Rectangle(243, 293, 12, 8)</c> out of
    /// <see cref="Game1.mouseCursors_1_6"/> instead — <c>317 + 12 * 999</c>
    /// is far outside the spritesheet, so without that branch the crop
    /// throws every tick. weatherIcon is read directly from the game's
    /// own <see cref="Game1.weatherIcon"/> field — deliberately NOT
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
            Cache[key] = Crop(Game1.mouseCursors, new Rectangle(406, 441 + seasonNumber * 8, 12, 8), device);
        }

        /// <summary>The game's own sentinel <see cref="Game1.weatherIcon"/> value for green rain.</summary>
        private const int GreenRainWeatherIcon = 999;

        public static void EnsureWeatherCached(int weatherIcon, GraphicsDevice device)
        {
            string key = $"weather{weatherIcon}";
            if (Cache.ContainsKey(key))
                return;
            Cache[key] = weatherIcon == GreenRainWeatherIcon
                ? Crop(Game1.mouseCursors_1_6, new Rectangle(243, 293, 12, 8), device)
                : Crop(Game1.mouseCursors, new Rectangle(317 + 12 * weatherIcon, 421, 12, 8), device);
        }

        private static byte[] Crop(Texture2D texture, Rectangle sourceRect, GraphicsDevice device)
        {
            var pixels = new Color[sourceRect.Width * sourceRect.Height];
            texture.GetData(0, sourceRect, pixels, 0, pixels.Length);

            using Texture2D cropped = new(device, sourceRect.Width, sourceRect.Height);
            cropped.SetData(pixels);

            using MemoryStream ms = new();
            cropped.SaveAsPng(ms, sourceRect.Width, sourceRect.Height);
            return ms.ToArray();
        }
    }
}
