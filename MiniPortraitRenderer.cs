using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace StardewDS
{
    /// <summary>
    /// Renders the player's real small "mini portrait" — the exact
    /// head-and-hair-only icon vanilla itself draws on the GameMenu's
    /// Skills tab and as the player's marker on the world map
    /// (<see cref="StardewValley.FarmerRenderer.drawMiniPortrat"/>) — to
    /// an off-screen texture and caches it as PNG bytes for
    /// <see cref="CompanionServer"/>'s `GET /mini-portrait`.
    ///
    /// This is a genuinely different, much simpler render than
    /// <see cref="PortraitRenderer"/>'s full standing body: verified
    /// against the real decompiled `FarmerRenderer.drawMiniPortrat`
    /// (Dannode36/StardewValleyDecompiled — the up-to-date 1.6 mirror,
    /// not the older one this project used early on and which turned
    /// out to be pre-1.6) before writing this. The method draws exactly
    /// two layers — the base body/head texture cropped to
    /// <c>Rectangle(0, 0, 16, IsMale ? 15 : 16)</c> (facing down, i.e.
    /// just the head — this crop is far shorter than the full 32px-tall
    /// standing frame <see cref="PortraitRenderer"/> uses, which is
    /// what makes this an actual head shot instead of a full body), then
    /// the current hairstyle on top — and deliberately does **not**
    /// draw a shirt, pants, hat, or accessory, matching real vanilla:
    /// the Skills tab icon and map marker show your bare head+hair even
    /// if you're wearing a hat.
    ///
    /// Called at scale 3f for the GameMenu tab and scale 4f for the map
    /// marker in real vanilla (`GameMenu.draw`/`MapPage.drawMiniPortraits`
    /// respectively) — this renders once at a single fixed scale/canvas
    /// size shared by both companion-app call sites (the Skills nav tab
    /// overlay and the Map screen's player marker) rather than exposing
    /// two separately-scaled renders, since the app displays both at a
    /// similar small size anyway.
    /// </summary>
    internal static class MiniPortraitRenderer
    {
        private const int Width = 72;
        private const int Height = 72;

        // Matches real vanilla's own MapPage usage (`drawMiniPortrat(...,
        // scale: 4f, ...)`) — the larger of vanilla's two real call sites,
        // giving the crispest render for the two call sites in this app.
        private const float Scale = 4f;

        // Position within the off-screen canvas: real vanilla source crop
        // is 16 wide x up to 16 tall (male: 15, female: 16), so at Scale=4f
        // the character renders at up to 64x64 — an (4,4) top-left offset
        // centers that within the 72x72 canvas with a small margin on
        // every side for the hairstyle's own draw, which can extend a few
        // pixels past the base body crop for tall/wide hairstyles.
        private const float PositionOffset = 4f;

        /// <summary>How often, in ticks, to recompute <see cref="PortraitRenderer.AppearanceSignature"/>. Re-renders only when it changes — see <see cref="PortraitRenderer"/>'s doc comment for why a fixed re-render timer caused hitches.</summary>
        private const int CheckEveryTicks = 30;

        private static byte[]? _cached;
        private static int _ticksSinceCheck = CheckEveryTicks; // forces a check on the very first call.
        private static int? _signature;
        private static RenderTarget2D? _target;
        private static SpriteBatch? _spriteBatch;

        private static int _version;

        /// <summary>Bumped each time a re-render has finished encoding — 0 until the first one. Reported in the snapshot so the app can put it in the <c>/mini-portrait</c> URL (see <see cref="PortraitRenderer.Version"/>). Safe to read from any thread.</summary>
        public static int Version => System.Threading.Volatile.Read(ref _version);

        /// <summary>The most recently rendered mini-portrait PNG, or null if none has been rendered yet. Safe to call from any thread.</summary>
        public static byte[]? TryGet() => _cached;

        /// <summary>Re-renders the mini portrait if the player's appearance changed; a cheap no-op otherwise. Main-thread only — call from the same place as <see cref="GameStateSnapshot.Capture"/> (alongside <see cref="PortraitRenderer.Refresh"/>). PNG encoding runs on the thread pool.</summary>
        public static void Refresh(Farmer player, GraphicsDevice device)
        {
            if (++_ticksSinceCheck < CheckEveryTicks)
                return;
            _ticksSinceCheck = 0;

            int signature = PortraitRenderer.AppearanceSignature(player);
            if (signature == _signature)
                return;
            _signature = signature;

            if (_target is null || _target.IsDisposed)
                _target = new RenderTarget2D(device, Width, Height);
            if (_spriteBatch is null || _spriteBatch.IsDisposed)
                _spriteBatch = new SpriteBatch(device);
            RenderTarget2D target = _target;
            SpriteBatch spriteBatch = _spriteBatch;

            device.SetRenderTarget(target);
            device.Clear(Color.Transparent);

            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);

            // The exact real vanilla call (facingDirection is forced to 2
            // — facing down — inside drawMiniPortrat itself regardless of
            // what's passed, per the decompiled source, but passed as 2
            // here anyway to match the real call sites' own arguments).
            player.FarmerRenderer.drawMiniPortrat(
                spriteBatch,
                new Vector2(PositionOffset, PositionOffset),
                0f,
                Scale,
                2,
                player
            );

            spriteBatch.End();
            device.SetRenderTarget(null);

            var pixels = new Color[Width * Height];
            target.GetData(pixels);
            System.Threading.Tasks.Task.Run(() =>
            {
                _cached = PngEncoder.Encode(pixels, Width, Height);
                System.Threading.Interlocked.Increment(ref _version);
            });
        }
    }
}
