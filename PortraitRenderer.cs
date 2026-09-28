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
    /// Re-rendered only when the player's appearance actually changes (see
    /// <see cref="AppearanceSignature"/>), not on a fixed timer: an earlier
    /// version re-rendered all nine frame/eye combinations every 30 ticks,
    /// each one a render-target draw, a GPU read-back and a PNG encode on
    /// the main thread — a visible hitch twice a second. Now a change marks
    /// the set dirty, one combination is rendered per tick (spreading the
    /// GPU read-backs out), and the PNG encoding runs on the thread pool
    /// (<see cref="PngEncoder"/>). Touches the graphics device, so — like
    /// <see cref="SpriteCache"/> — must only be called from the main game
    /// thread.
    /// </summary>
    internal static class PortraitRenderer
    {
        private const int Width = 80;
        private const int Height = 144;

        /// <summary>How often, in ticks, to recompute <see cref="AppearanceSignature"/>. Cheap, but no need to do it every tick.</summary>
        private const int CheckEveryTicks = 30;

        /// <summary>Safety net: re-render anyway after this many ticks (~60s), in case something the signature doesn't cover changed the sprite.</summary>
        private const int ForceRefreshTicks = 3600;

        /// <summary>The three distinct standing-walk frames of the farmer sprite sheet (columns 0-2). `SkillsPage` cycles its portrait through <c>{0, 1, 0, 2}</c> (<c>playerPanelFrames</c>), so the app needs just these three.</summary>
        public const int FrameCount = 3;

        /// <summary>The eye states vanilla's blink cycle (<c>Farmer.update</c>) moves <c>currentEyes</c> through: 0 = open, 1 = half closed, 4 = closed. Rendered explicitly rather than read from the live farmer, whose state at capture time is whatever the blink timer (or being exhausted) last left it at — the app runs its own blink from these.</summary>
        public static readonly int[] EyeStates = { 0, 1, 4 };

        private static readonly byte[]?[,] _cached = new byte[FrameCount, 3][];
        private static int _ticksSinceCheck = CheckEveryTicks; // forces a check on the very first call.
        private static int _ticksSinceRender;
        private static int? _signature;

        /// <summary>Index (frame * EyeStates.Length + eye) of the next combination to render, or -1 when everything is up to date.</summary>
        private static int _nextToRender = -1;

        private static RenderTarget2D? _target;
        private static SpriteBatch? _spriteBatch;

        /// <summary>PNG-encode tasks of the render pass in progress, so <see cref="Version"/> is only bumped once every frame of it is servable.</summary>
        private static readonly System.Collections.Generic.List<System.Threading.Tasks.Task> _pendingEncodes = new();

        private static int _version;

        /// <summary>Bumped each time a complete re-render (all frames and eye states) has finished encoding — 0 until the first one. Reported in the snapshot so the app can put it in the <c>/portrait</c> URL: the app caches images by URL, so without this it would keep showing the old outfit/haircut forever. Safe to read from any thread.</summary>
        public static int Version => System.Threading.Volatile.Read(ref _version);

        /// <summary>The most recently rendered portrait PNG for walk frame <paramref name="frame"/> (0-2, out-of-range values clamp) with eye state <paramref name="eyes"/> (0 open, 1 half closed, 4 closed; anything else is treated as open), or null if none has been rendered yet. Frame 0 is the still standing pose. Safe to call from any thread.</summary>
        public static byte[]? TryGet(int frame = 0, int eyes = 0) =>
            _cached[System.Math.Clamp(frame, 0, FrameCount - 1), System.Math.Max(0, System.Array.IndexOf(EyeStates, eyes))];

        /// <summary>A hash of everything that changes how the farmer sprite is drawn — hair, skin, clothes and their dye colors, hat, boots, accessory, eye color, bathing clothes, and whether it's night (SkillsPage's dark-blue overlay). Shared with <see cref="MiniPortraitRenderer"/>.</summary>
        public static int AppearanceSignature(Farmer player)
        {
            var hash = new System.HashCode();
            hash.Add(player.IsMale);
            hash.Add(player.hair.Value);
            hash.Add(player.hairstyleColor.Value.PackedValue);
            hash.Add(player.skin.Value);
            hash.Add(player.accessory.Value);
            hash.Add(player.facialHair.Value);
            hash.Add(player.newEyeColor.Value.PackedValue);
            hash.Add(player.shirt.Value);
            hash.Add(player.pants.Value);
            hash.Add(player.pantsColor.Value.PackedValue);
            hash.Add(player.shoes.Value);
            hash.Add(player.shirtItem.Value?.QualifiedItemId);
            hash.Add(player.shirtItem.Value?.clothesColor.Value.PackedValue);
            hash.Add(player.pantsItem.Value?.QualifiedItemId);
            hash.Add(player.pantsItem.Value?.clothesColor.Value.PackedValue);
            hash.Add(player.hat.Value?.QualifiedItemId);
            hash.Add(player.boots.Value?.QualifiedItemId);
            hash.Add(player.bathingClothes.Value);
            hash.Add(Game1.timeOfDay >= 1900);
            return hash.ToHashCode();
        }

        /// <summary>Checks whether the portrait needs re-rendering and, if so, renders one frame/eye combination per call until the set is complete. A cheap no-op otherwise. Main-thread only — call from the same place as <see cref="GameStateSnapshot.Capture"/>.</summary>
        public static void Refresh(Farmer player, GraphicsDevice device)
        {
            _ticksSinceRender++;
            if (++_ticksSinceCheck >= CheckEveryTicks)
            {
                _ticksSinceCheck = 0;
                int signature = AppearanceSignature(player);
                if (signature != _signature || _ticksSinceRender >= ForceRefreshTicks)
                {
                    _signature = signature;
                    _nextToRender = 0;
                    _pendingEncodes.Clear();
                }
            }

            if (_nextToRender < 0)
                return;

            int frame = _nextToRender / EyeStates.Length;
            int e = _nextToRender % EyeStates.Length;
            _nextToRender = _nextToRender + 1 >= FrameCount * EyeStates.Length ? -1 : _nextToRender + 1;
            _ticksSinceRender = 0;

            int savedEyes = player.currentEyes;
            Color[] pixels;
            try
            {
                player.currentEyes = EyeStates[e];
                pixels = Render(player, device, frame);
            }
            finally
            {
                player.currentEyes = savedEyes;
            }

            _pendingEncodes.Add(System.Threading.Tasks.Task.Run(() => _cached[frame, e] = PngEncoder.Encode(pixels, Width, Height)));

            // Last combination of this pass rendered: publish a new version
            // once all of its encodes have landed.
            if (_nextToRender < 0)
            {
                System.Threading.Tasks.Task.WhenAll(_pendingEncodes.ToArray())
                    .ContinueWith(_ => System.Threading.Interlocked.Increment(ref _version));
                _pendingEncodes.Clear();
            }
        }

        private static Color[] Render(Farmer player, GraphicsDevice device, int frame)
        {
            bool bathing = player.bathingClothes.Value;
            int spriteFrame = bathing ? 108 : frame;
            Rectangle source = new(frame * 16, bathing ? 576 : 0, 16, 32);

            if (_target is null || _target.IsDisposed)
                _target = new RenderTarget2D(device, Width, Height);
            if (_spriteBatch is null || _spriteBatch.IsDisposed)
                _spriteBatch = new SpriteBatch(device);
            RenderTarget2D target = _target;
            SpriteBatch spriteBatch = _spriteBatch;

            device.SetRenderTarget(target);
            device.Clear(Color.Transparent);

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
            return pixels;
        }
    }
}
