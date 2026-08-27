using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace StardewDS
{
    /// <summary>
    /// Crops the game's own 9-slice menu window-border texture — the
    /// same ornate wood-carved border every vanilla dialogue/menu box
    /// uses. Read from the real decompiled
    /// <c>StardewValley.Menus.IClickableMenu.drawTextureBox</c> and its
    /// default call before writing this: <see cref="Game1.menuTexture"/>
    /// at <c>Rectangle(0, 256, 60, 60)</c> — a 3x3 grid of 20x20 tiles
    /// (<c>cornerSize = sourceRect.Width / 3</c> in the real method),
    /// i.e. corners, edges, and a stretchy center.
    ///
    /// Served whole as a single 60x60 PNG; the app applies the 9-slice
    /// stretch itself via Flutter's <c>Image.centerSlice</c>
    /// (<c>Rect.fromLTWH(20, 20, 20, 20)</c>), which does the same job as
    /// <c>drawTextureBox</c>'s tiling — no custom painting needed on
    /// either side.
    /// </summary>
    internal static class WindowBorderCache
    {
        private static byte[]? _cached;

        /// <summary>The cached border PNG, or null if not rendered yet. Safe to call from any thread.</summary>
        public static byte[]? TryGet() => _cached;

        /// <summary>Crops and caches the border once — cheap no-op after that (it never changes). Main-thread only (touches the graphics device).</summary>
        public static void EnsureCached(GraphicsDevice device)
        {
            if (_cached is not null)
                return;

            Rectangle sourceRect = new(0, 256, 60, 60);
            var pixels = new Color[sourceRect.Width * sourceRect.Height];
            Game1.menuTexture.GetData(0, sourceRect, pixels, 0, pixels.Length);

            using Texture2D cropped = new(device, sourceRect.Width, sourceRect.Height);
            cropped.SetData(pixels);

            using MemoryStream ms = new();
            cropped.SaveAsPng(ms, sourceRect.Width, sourceRect.Height);
            _cached = ms.ToArray();
        }
    }
}
