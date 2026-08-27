using System.Collections.Concurrent;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.ItemTypeDefinitions;

namespace StardewDS
{
    /// <summary>
    /// Crops a single-frame icon PNG for an item straight out of the
    /// game's own loaded spritesheet, and caches it by qualified item id
    /// (e.g. "(O)24") for <see cref="CompanionServer"/>'s `GET /sprite` to
    /// serve.
    ///
    /// This is why the mod exists as a real feature rather than the app
    /// shipping its own art: these are the player's own licensed game's
    /// textures, read from the game's own loaded content each time — the
    /// app never bundles or downloads Stardew Valley assets itself.
    ///
    /// NOTE (unverified — see project README): written against the
    /// current SDV 1.6 `ItemRegistry`/`ParsedItemData` API, which is the
    /// documented modern replacement for indexing the old hardcoded
    /// spritesheets directly. Not compiled against the real game DLLs
    /// here, so <see cref="ParsedItemData.GetSourceRect"/>'s default
    /// overload and <c>Game1.graphics.GraphicsDevice</c> are the two
    /// pieces most likely to need a small adjustment if the build flags
    /// them.
    ///
    /// <see cref="EnsureCached"/> creates a <see cref="Texture2D"/>, which
    /// needs the graphics device — call it only from the main game thread
    /// (e.g. from an UpdateTicked handler, same as <see cref="GameStateSnapshot.Capture"/>
    /// which is what actually calls it). The cache dictionary itself is a
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> so the HTTP
    /// background thread can safely read from it via <see cref="TryGet"/>.
    /// </summary>
    internal static class SpriteCache
    {
        private static readonly ConcurrentDictionary<string, byte[]> Cache = new();

        /// <summary>Returns the cached PNG bytes for <paramref name="qualifiedItemId"/>, or null if it hasn't been cropped yet (or doesn't exist). Safe to call from any thread.</summary>
        public static byte[]? TryGet(string qualifiedItemId) =>
            Cache.TryGetValue(qualifiedItemId, out byte[]? bytes) ? bytes : null;

        /// <summary>
        /// Crops and caches the icon for <paramref name="qualifiedItemId"/>
        /// if it isn't already cached. A no-op otherwise, so it's cheap to
        /// call once per inventory/equipment slot every tick — main-thread
        /// only.
        /// </summary>
        public static void EnsureCached(string? qualifiedItemId)
        {
            if (string.IsNullOrEmpty(qualifiedItemId) || Cache.ContainsKey(qualifiedItemId))
                return;

            ParsedItemData? data = ItemRegistry.GetData(qualifiedItemId);
            if (data is null)
                return;

            Texture2D sourceTexture = data.GetTexture();
            Rectangle sourceRect = data.GetSourceRect();

            var pixels = new Color[sourceRect.Width * sourceRect.Height];
            sourceTexture.GetData(0, sourceRect, pixels, 0, pixels.Length);

            using Texture2D cropped = new(Game1.graphics.GraphicsDevice, sourceRect.Width, sourceRect.Height);
            cropped.SetData(pixels);

            using MemoryStream ms = new();
            cropped.SaveAsPng(ms, sourceRect.Width, sourceRect.Height);

            Cache[qualifiedItemId] = ms.ToArray();
        }
    }
}
