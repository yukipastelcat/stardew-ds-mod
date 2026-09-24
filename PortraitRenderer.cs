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

        /// <summary>The three distinct standing-walk frames of the farmer sprite sheet (columns 0-2). `SkillsPage` cycles its portrait through <c>{0, 1, 0, 2}</c> (<c>playerPanelFrames</c>), so the app needs just these three.</summary>
        public const int FrameCount = 3;

        /// <summary>The eye states vanilla's blink cycle (<c>Farmer.update</c>) moves <c>currentEyes</c> through: 0 = open, 1 = half closed, 4 = closed. Rendered explicitly rather than read from the live farmer, whose state at capture time is whatever the blink timer (or being exhausted) last left it at — the app runs its own blink from these.</summary>
        public static readonly int[] EyeStates = { 0, 1, 4 };

        private static readonly byte[]?[,] _cached = new byte[FrameCount, 3][];
        private static int _ticksSinceRefresh = RefreshEveryTicks; // forces a render on the very first call.

        /// <summary>The most recently rendered portrait PNG for walk frame <paramref name="frame"/> (0-2, out-of-range values clamp) with eye state <paramref name="eyes"/> (0 open, 1 half closed, 4 closed; anything else is treated as open), or null if none has been rendered yet. Frame 0 is the still standing pose. Safe to call from any thread.</summary>
        public static byte[]? TryGet(int frame = 0, int eyes = 0) =>
            _cached[System.Math.Clamp(frame, 0, FrameCount - 1), System.Math.Max(0, System.Array.IndexOf(EyeStates, eyes))];

        /// <summary>Re-renders the portrait frames if they're due; a cheap no-op otherwise. Main-thread only — call from the same place as <see cref="GameStateSnapshot.Capture"/>.</summary>
        public static void Refresh(Farmer player, GraphicsDevice device)
        {
            _ticksSinceRefresh++;
            if (_ticksSinceRefresh < RefreshEveryTicks)
                return;
            _ticksSinceRefresh = 0;

            int savedEyes = player.currentEyes;
            try
            {
                for (int frame = 0; frame < FrameCount; frame++)
                {
                    for (int e = 0; e < EyeStates.Length; e++)
                    {
                        player.currentEyes = EyeStates[e];
                        _cached[frame, e] = Render(player, device, frame);
                    }
                }
            }
            finally
            {
                player.currentEyes = savedEyes;
            }
        }

        private static byte[] Render(Farmer player, GraphicsDevice device, int frame)
        {
            bool bathing = player.bathingClothes.Value;
            int spriteFrame = bathing ? 108 : frame;
            Rectangle source = new(frame * 16, bathing ? 576 : 0, 16, 32);

            using RenderTarget2D target = new(device, Width, Height);

            device.SetRenderTarget(target);
            device.Clear(Color.Transparent);

            using SpriteBatch spriteBatch = new(device);
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);

            // Same call SkillsPage.draw / InventoryPage make for
            // Game1.player, just with a small fixed margin instead of the
            // menu's on-screen position — the composited draw can spill
            // slightly past the base 16x32 source rect (hat brims, hair),
            // hence the padding around it in our 80x144 canvas rather than
            // a tight 64x128 (16x32 at this method's own internal 4x
            // multiplier).
            player.FarmerRenderer.draw(
                spriteBatch,
                new FarmerSprite.AnimationFrame(spriteFrame, 0, secondaryArm: false, flip: false),
                spriteFrame,
                source,
                new Vector2(8, 8),
                Vector2.Zero,
                0.8f,
                2,
                Color.White,
                0f,
                1f,
                player
            );

            // SkillsPage.draw darkens the farmer with a second, 30% dark-blue
            // pass after 7pm (over the night background).
            if (Game1.timeOfDay >= 1900)
            {
                player.FarmerRenderer.draw(
                    spriteBatch,
                    new FarmerSprite.AnimationFrame(frame, 0, secondaryArm: false, flip: false),
                    frame,
                    new Rectangle(frame * 16, 0, 16, 32),
                    new Vector2(8, 8),
                    Vector2.Zero,
                    0.8f,
                    2,
                    Color.DarkBlue * 0.3f,
                    0f,
                    1f,
                    player
                );
            }

            spriteBatch.End();
            device.SetRenderTarget(null);

            var pixels = new Color[Width * Height];
            target.GetData(pixels);

            using Texture2D flat = new(device, Width, Height);
            flat.SetData(pixels);

            using MemoryStream ms = new();
            flat.SaveAsPng(ms, Width, Height);
            return ms.ToArray();
        }
    }
}
