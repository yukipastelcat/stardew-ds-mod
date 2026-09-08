using System.Collections.Concurrent;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Objects;

namespace StardewDS
{
    /// <summary>
    /// Crops a single-frame icon PNG for an item straight out of the
    /// game's own loaded spritesheet, and caches it by <see cref="CacheKeyFor"/>
    /// (usually just the qualified item id, e.g. "(O)24") for
    /// <see cref="CompanionServer"/>'s `GET /sprite` to serve.
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

        /// <summary>Returns the cached PNG bytes for <paramref name="cacheKey"/> (see <see cref="CacheKeyFor"/>), or null if it hasn't been cropped yet (or doesn't exist). Safe to call from any thread.</summary>
        public static byte[]? TryGet(string cacheKey) =>
            Cache.TryGetValue(cacheKey, out byte[]? bytes) ? bytes : null;

        /// <summary>
        /// The cache/URL key for <paramref name="item"/>: its qualified item
        /// id for a plain item, or that id plus a packed-color suffix for a
        /// <see cref="ColoredObject"/> (e.g. Pickles/Jelly/Wine/Juice/Roe/
        /// Aged Roe/Caviar/Dried Fruit/Dried Mushrooms). Every flavor of one
        /// of these shares a single qualified id ("(O)344" is every Jelly
        /// regardless of which fruit made it) with the actual color carried
        /// per-instance on the object itself, not in its shared item data —
        /// keying the cache by qualified id alone would cache one flavor's
        /// icon and then serve it for every other flavor of the same
        /// preserve/fish product (issue #9). Null if <paramref name="item"/>
        /// is null.
        /// </summary>
        public static string? CacheKeyFor(Item? item)
        {
            if (item is null)
                return null;

            return item is ColoredObject coloredObj
                ? $"{item.QualifiedItemId}@{coloredObj.color.Value.PackedValue:x8}"
                : item.QualifiedItemId;
        }

        /// <summary>
        /// Crops and caches the icon for <paramref name="item"/> if it isn't
        /// already cached (see <see cref="CacheKeyFor"/> for the cache key).
        /// A no-op otherwise, so it's cheap to call once per inventory/
        /// equipment slot every tick — main-thread only.
        /// </summary>
        public static void EnsureCached(Item? item)
        {
            string? cacheKey = CacheKeyFor(item);
            if (cacheKey is null || Cache.ContainsKey(cacheKey))
                return;

            ParsedItemData? data = ItemRegistry.GetData(item!.QualifiedItemId);
            if (data is null)
                return;

            Texture2D sourceTexture = data.GetTexture();

            // "Smoked Fish" is the one ColoredObject that doesn't use the
            // plain base + tinted-overlay composite below — vanilla's own
            // ColoredObject.drawInMenu special-cases it to draw the actual
            // fish's own sprite instead of a flat tint. Out of scope for
            // issue #9 (Pickles/Jelly/Caviar/Aged Roe/Roe/Dried Fruit all
            // use the normal two-layer composite), so it falls back to the
            // plain crop below rather than risk a garbled composite from
            // applying the wrong technique to it.
            byte[] png = item is ColoredObject coloredObj && item.ItemId != "SmokedFish"
                ? RenderColoredObject(data, sourceTexture, coloredObj)
                : CropPlain(data, sourceTexture);

            Cache[cacheKey] = png;
        }

        private static byte[] CropPlain(ParsedItemData data, Texture2D sourceTexture)
        {
            Rectangle sourceRect = data.GetSourceRect();

            var pixels = new Color[sourceRect.Width * sourceRect.Height];
            sourceTexture.GetData(0, sourceRect, pixels, 0, pixels.Length);

            using Texture2D cropped = new(Game1.graphics.GraphicsDevice, sourceRect.Width, sourceRect.Height);
            cropped.SetData(pixels);

            using MemoryStream ms = new();
            cropped.SaveAsPng(ms, sourceRect.Width, sourceRect.Height);
            return ms.ToArray();
        }

        /// <summary>
        /// Reproduces the real vanilla <c>ColoredObject.drawInMenu</c>
        /// two-layer composite (verified against decompiled 1.6 source
        /// before writing this, same off-screen-render-target technique
        /// <see cref="PortraitRenderer"/> uses): the plain base sprite
        /// (spriteIndex 0) drawn untinted, then a second sprite — the
        /// *same* spriteIndex-0 rect if <see cref="ColoredObject.ColorSameIndexAsParentSheetIndex"/>,
        /// else the adjacent spriteIndex-1 tile — drawn on top tinted with
        /// the object's actual <see cref="ColoredObject.color"/>. (When
        /// <c>ColorSameIndexAsParentSheetIndex</c> is set, vanilla skips the
        /// untinted base draw entirely and only draws the single tinted
        /// layer, since there'd be nothing distinguishable under it.)
        /// Without this second pass every colored preserve/fish product
        /// crops as its plain, uncolored base sprite (issue #9).
        /// </summary>
        private static byte[] RenderColoredObject(ParsedItemData data, Texture2D sourceTexture, ColoredObject coloredObj)
        {
            Rectangle baseRect = data.GetSourceRect();
            bool sameIndex = coloredObj.ColorSameIndexAsParentSheetIndex;
            Rectangle colorRect = sameIndex ? baseRect : data.GetSourceRect(1);

            GraphicsDevice device = Game1.graphics.GraphicsDevice;
            using RenderTarget2D target = new(device, baseRect.Width, baseRect.Height);

            device.SetRenderTarget(target);
            device.Clear(Color.Transparent);

            using (SpriteBatch spriteBatch = new(device))
            {
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                if (!sameIndex)
                    spriteBatch.Draw(sourceTexture, Vector2.Zero, baseRect, Color.White);
                spriteBatch.Draw(sourceTexture, Vector2.Zero, colorRect, coloredObj.color.Value);
                spriteBatch.End();
            }

            device.SetRenderTarget(null);

            var pixels = new Color[baseRect.Width * baseRect.Height];
            target.GetData(pixels);

            using Texture2D flat = new(device, baseRect.Width, baseRect.Height);
            flat.SetData(pixels);

            using MemoryStream ms = new();
            flat.SaveAsPng(ms, baseRect.Width, baseRect.Height);
            return ms.ToArray();
        }
    }
}
