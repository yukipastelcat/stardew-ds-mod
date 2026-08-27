using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace StardewDS
{
    /// <summary>
    /// Crops the backpack grid's own slot background frame, the darkened
    /// overlay drawn over a slot beyond the player's current backpack
    /// capacity, and the highlighted-slot frame used for the currently
    /// selected/equipped item — all straight from
    /// <see cref="Game1.menuTexture"/>.
    ///
    /// Unlike the other *Cache classes in this mod, these don't use a
    /// hand-copied pixel Rectangle: they call
    /// <see cref="Game1.getSourceRectForStandardTileSheet"/> with the same
    /// tile indices the decompiled game itself uses for these exact slot
    /// states — tile 10 for the normal slot and tile 57 for the locked
    /// overlay (`StardewValley.Menus.InventoryMenu.draw`, drawn there at
    /// `tint * 0.5f` alpha — reproduced here by the app compositing this
    /// PNG at 50% opacity instead), and tile 56 for the highlighted slot —
    /// the same tile the vanilla hotbar swaps in, in place of tile 10, for
    /// whichever slot is currently selected (`StardewValley.Menus.Toolbar.draw`:
    /// tile is 56 when `Game1.player.CurrentToolIndex == j`, else 10 — a
    /// replacement frame, not an overlay drawn on top of the normal one).
    /// Using the tile-index helper (rather than a hand-copied Rectangle)
    /// means the crop stays correct even if the sheet's pixel layout
    /// doesn't match what a decompile skim alone would suggest.
    /// </summary>
    internal static class InventorySlotIconCache
    {
        private const int SlotFrameTile = 10;
        private const int LockedOverlayTile = 57;
        private const int SelectedFrameTile = 56;

        private static byte[]? _slotFrame;
        private static byte[]? _lockedOverlay;
        private static byte[]? _selectedFrame;

        /// <summary>Cached PNG for the normal slot background, or null if not rendered yet.</summary>
        public static byte[]? TryGetSlotFrame() => _slotFrame;

        /// <summary>Cached PNG for the locked-slot overlay (composite at ~50% opacity over the slot frame), or null if not rendered yet.</summary>
        public static byte[]? TryGetLockedOverlay() => _lockedOverlay;

        /// <summary>Cached PNG for the highlighted slot frame — the real vanilla hotbar's selected-slot background (see the class doc comment). Use this in place of (not on top of) the normal slot frame for the currently selected slot. Null if not rendered yet.</summary>
        public static byte[]? TryGetSelectedFrame() => _selectedFrame;

        /// <summary>Crops and caches all three PNGs if not already cached — cheap no-op once warmed (this part of the sheet never changes). Main-thread only (touches the graphics device).</summary>
        public static void EnsureCached(GraphicsDevice device)
        {
            _slotFrame ??= Crop(device, Game1.menuTexture, Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, SlotFrameTile));
            _lockedOverlay ??= Crop(device, Game1.menuTexture, Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, LockedOverlayTile));
            _selectedFrame ??= Crop(device, Game1.menuTexture, Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, SelectedFrameTile));
        }

        private static byte[] Crop(GraphicsDevice device, Texture2D source, Rectangle sourceRect)
        {
            var pixels = new Color[sourceRect.Width * sourceRect.Height];
            source.GetData(0, sourceRect, pixels, 0, pixels.Length);

            using Texture2D cropped = new(device, sourceRect.Width, sourceRect.Height);
            cropped.SetData(pixels);

            using MemoryStream ms = new();
            cropped.SaveAsPng(ms, sourceRect.Width, sourceRect.Height);
            return ms.ToArray();
        }
    }
}
