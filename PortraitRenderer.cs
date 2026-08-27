using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace StardewDS
{
    /// <summary>
    /// Renders the player's actual farmer sprite — the same composited
    /// body+shirt+pants+hair+hat+accessories draw the vanilla inventory
    /// menu uses for its own portrait box — to an off-screen texture and
    /// caches it as PNG bytes for <see cref="CompanionServer"/>'s
    /// `GET /portrait`.
    ///
    /// The draw call below is adapted directly from the actual decompiled
    /// game source (StardewValley.Menus.InventoryPage's portrait draw),
    /// not guessed — verified against Stardew Valley's real source before
    /// writing this, specifically because getting this one wrong would've
    /// meant a much harder-to-diagnose blank/garbled image instead of a
    /// clean compile error. Only the render target size/position differ
    /// (aimed at our own off-screen canvas instead of the menu's on-screen
    /// coordinates); everything else — source rect, scale, facing
    /// direction — matches the game's own call exactly, so this should
    /// produce a pixel-identical crop to what you see in the in-game menu.
    ///
    /// Re-rendered periodically rather than on every tick — see
    /// <see cref="Refresh"/> — since creating a render target and
    /// PNG-encoding it 60 times a second would be wasteful for something
    /// that only changes when the player re-dresses or gets a haircut.
    /// Touches the graphics device, so — like <see cref="SpriteCache"/> —
    /// must only be called from the main game thread.
    /// </summary>
    internal static class PortraitRenderer
    {
        private const int Width = 80;
        private const int Height = 144;
        private const int RefreshEveryTicks = 30; // ~0.5s at 60 ticks/sec — frequent enough to catch a wardrobe/haircut change without re-rendering on every single tick.

        private static byte[]? _cached;
        private static int _ticksSinceRefresh = RefreshEveryTicks; // forces a render on the very first call.

        /// <summary>The most recently rendered portrait PNG, or null if none has been rendered yet. Safe to call from any thread.</summary>
        public static byte[]? TryGet() => _cached;

        /// <summary>Re-renders the portrait if it's due; a cheap no-op otherwise. Main-thread only — call from the same place as <see cref="GameStateSnapshot.Capture"/>.</summary>
        public static void Refresh(Farmer player, GraphicsDevice device)
        {
            _ticksSinceRefresh++;
            if (_ticksSinceRefresh < RefreshEveryTicks)
                return;
            _ticksSinceRefresh = 0;

            using RenderTarget2D target = new(device, Width, Height);

            device.SetRenderTarget(target);
            device.Clear(Color.Transparent);

            using SpriteBatch spriteBatch = new(device);
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);

            // Same call InventoryPage makes for Game1.player, just with a
            // small fixed margin instead of the menu's on-screen position
            // — the composited draw can spill slightly past the base 16x32
            // source rect (hat brims, hair), hence the padding around it
            // in our 80x144 canvas rather than a tight 64x128 (16x32 at
            // this method's own internal 4x multiplier).
            player.FarmerRenderer.draw(
                spriteBatch,
                new FarmerSprite.AnimationFrame(0, player.bathingClothes.Value ? 108 : 0, secondaryArm: false, flip: false),
                player.bathingClothes.Value ? 108 : 0,
                new Rectangle(0, player.bathingClothes.Value ? 576 : 0, 16, 32),
                new Vector2(8, 8),
                Vector2.Zero,
                0.8f,
                2,
                Color.White,
                0f,
                1f,
                player
            );

            spriteBatch.End();
            device.SetRenderTarget(null);

            var pixels = new Color[Width * Height];
            target.GetData(pixels);

            using Texture2D flat = new(device, Width, Height);
            flat.SetData(pixels);

            using MemoryStream ms = new();
            flat.SaveAsPng(ms, Width, Height);
            _cached = ms.ToArray();
        }
    }
}
