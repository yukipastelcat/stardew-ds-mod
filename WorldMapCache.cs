using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace StardewDS
{
    /// <summary>
    /// Serves the real vanilla world map background — cropped from the
    /// same texture <c>StardewValley.Menus.MapPage</c> draws behind the
    /// player's position marker when they open the in-game map
    /// (<c>Game1.content.Load&lt;Texture2D&gt;("LooseSprites\\map")</c>,
    /// confirmed against decompiled <c>MapPage</c>'s constructor before
    /// writing this, same verify-first approach as every other cache in
    /// this mod).
    ///
    /// <b>Correction, confirmed by a real screenshot</b>: the full
    /// texture is not just the valley — it's a much larger spritesheet
    /// packing in every <c>Data/WorldMap</c> map area (alternate farm
    /// layouts, the quarry, Ginger Island, the volcano dungeon, etc.),
    /// stacked below the base overworld view. Serving the whole thing
    /// (the first version of this file) showed all of that stitched
    /// together as one tall, garbled image instead of just the
    /// overworld. The fix, taken straight from the decompiled
    /// <c>MapPage.draw</c> call this doc comment already cited: vanilla
    /// only ever draws <see cref="SourceRect"/> — <c>Rectangle(0, 0,
    /// 300, 180)</c> — from this texture for the base map, so that's
    /// the only part cropped and cached here.
    ///
    /// This deliberately still doesn't add the per-farm-type overlay
    /// <c>MapPage.draw</c> layers on top for cases 1-6 of
    /// <see cref="Game1.whichFarm"/> (standard/riverland/forest/hilltop/
    /// wilderness/four-corners/beach) — those overlays are drawn at this
    /// same <see cref="SourceRect"/> region (composited over the base,
    /// not a separate area of the sheet), so the base map alone still
    /// reads correctly without them; revisit only if the farm-type
    /// variant specifically turns out to matter.
    ///
    /// <see cref="Width"/>/<see cref="Height"/> are <see cref="SourceRect"/>'s
    /// own dimensions (300x180) — <see cref="GameStateSnapshot"/> divides
    /// the player's raw map-pixel marker position (already in this same
    /// 0-300x0-180 coordinate space, per <c>Data/WorldMap</c>'s
    /// <c>MapPixelArea</c>) by these to report a 0-1 fraction.
    /// </summary>
    internal static class WorldMapCache
    {
        /// <summary>The exact region vanilla's own <c>MapPage.draw</c> draws for the base overworld map — verified against decompiled source, not guessed.</summary>
        private static readonly Rectangle SourceRect = new(0, 0, 300, 180);

        private static byte[]? _cached;

        public static int Width { get; private set; }
        public static int Height { get; private set; }

        /// <summary>The cached map PNG, or null if not rendered yet. Safe to call from any thread.</summary>
        public static byte[]? TryGet() => _cached;

        /// <summary>Loads and caches the map texture once — cheap no-op after that (it never changes within a session). Main-thread only (touches the content manager + graphics device).</summary>
        public static void EnsureCached(GraphicsDevice device)
        {
            if (_cached is not null)
                return;

            Texture2D source = Game1.content.Load<Texture2D>("LooseSprites\\map");

            var pixels = new Color[SourceRect.Width * SourceRect.Height];
            source.GetData(0, SourceRect, pixels, 0, pixels.Length);

            using Texture2D cropped = new(device, SourceRect.Width, SourceRect.Height);
            cropped.SetData(pixels);

            using MemoryStream ms = new();
            cropped.SaveAsPng(ms, SourceRect.Width, SourceRect.Height);
            _cached = ms.ToArray();
            Width = SourceRect.Width;
            Height = SourceRect.Height;
        }
    }
}
