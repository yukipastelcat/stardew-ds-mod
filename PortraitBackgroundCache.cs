using System.Collections.Concurrent;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace StardewDS
{
    /// <summary>
    /// Serves the actual background image the vanilla inventory menu
    /// draws behind the player's portrait — <see cref="Game1.daybg"/> /
    /// <see cref="Game1.nightbg"/>, which the game itself swaps at 7pm
    /// (<c>Game1.timeOfDay >= 1900</c> — see the exact draw call in the
    /// real decompiled <c>StardewValley.Menus.InventoryPage.draw</c>,
    /// read before writing this, same as <see cref="PortraitRenderer"/>).
    ///
    /// These are small standalone textures, not a crop from a shared
    /// sheet, so the *whole* texture is captured — <c>Texture2D.GetData</c>
    /// with no source rect reads however big the asset actually is, so
    /// there's no dimension to guess at here.
    /// </summary>
    internal static class PortraitBackgroundCache
    {
        private static readonly ConcurrentDictionary<string, byte[]> Cache = new();

        /// <summary>Returns the cached day or night background PNG, or null if not cached yet. Safe to call from any thread.</summary>
        public static byte[]? TryGet(bool night) =>
            Cache.TryGetValue(night ? "night" : "day", out byte[]? bytes) ? bytes : null;

        /// <summary>Crops and caches the requested variant if it isn't cached yet — cheap no-op once both variants are warmed (this never changes). Main-thread only (touches the graphics device).</summary>
        public static void EnsureCached(bool night, GraphicsDevice device)
        {
            string key = night ? "night" : "day";
            if (Cache.ContainsKey(key))
                return;

            Texture2D source = night ? Game1.nightbg : Game1.daybg;
            var pixels = new Color[source.Width * source.Height];
            source.GetData(pixels);

            using Texture2D flat = new(device, source.Width, source.Height);
            flat.SetData(pixels);

            using MemoryStream ms = new();
            flat.SaveAsPng(ms, source.Width, source.Height);
            Cache[key] = ms.ToArray();
        }
    }
}
