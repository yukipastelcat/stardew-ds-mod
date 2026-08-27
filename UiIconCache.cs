using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace StardewDS
{
    /// <summary>
    /// Crops the small fixed set of UI icons the companion app uses —
    /// the bottom nav's backpack/skills/map/crafting tab icons, plus the
    /// backpack screen's organize button — straight out of the game's
    /// own `Cursors` spritesheet (<see cref="Game1.mouseCursors"/>), the
    /// exact same icons the vanilla game itself draws.
    ///
    /// The source rects below were read out of the actual decompiled
    /// <c>StardewValley.Menus.GameMenu.draw</c> (tab icons) and
    /// <c>StardewValley.Menus.InventoryPage</c>'s constructor (organize
    /// button) before writing this (same verify-before-guessing approach
    /// as <see cref="PortraitRenderer"/>): each tab icon is a 16x16 cell
    /// at y=368 on the Cursors sheet, x offset by `sheetIndex * 16`,
    /// where inventory=0, skills=1, social=2, map=3, crafting=4
    /// (skills added later, verified the same way against the decompiled
    /// GameMenu tab list before writing it, per this project's
    /// verify-before-guessing convention).
    /// </summary>
    internal static class UiIconCache
    {
        private static readonly Dictionary<string, Rectangle> SourceRects = new()
        {
            ["backpack"] = new Rectangle(0, 368, 16, 16), // GameMenu's "inventory" tab icon
            ["skills"] = new Rectangle(16, 368, 16, 16), // GameMenu's "skills" tab icon
            ["map"] = new Rectangle(48, 368, 16, 16), // GameMenu's "map" tab icon
            ["crafting"] = new Rectangle(64, 368, 16, 16), // GameMenu's "crafting" tab icon

            // InventoryPage's organizeButton — a ClickableTextureComponent
            // built from `new Rectangle(162, 440, 16, 16)` on this same
            // Cursors sheet (verified against the decompiled
            // InventoryPage constructor before writing, same as every
            // other rect in this file).
            ["organize"] = new Rectangle(162, 440, 16, 16),
        };

        private static readonly ConcurrentDictionary<string, byte[]> Cache = new();

        /// <summary>Returns the cached PNG bytes for the icon named <paramref name="name"/> ("backpack", "skills", "map", or "crafting"), or null if unknown or not cached yet. Safe to call from any thread.</summary>
        public static byte[]? TryGet(string name) =>
            Cache.TryGetValue(name, out byte[]? bytes) ? bytes : null;

        /// <summary>Crops and caches every icon in <see cref="SourceRects"/> that isn't cached yet — cheap no-op once warmed (these never change, unlike item sprites or the portrait). Main-thread only (touches the graphics device).</summary>
        public static void EnsureCached(GraphicsDevice device)
        {
            foreach (KeyValuePair<string, Rectangle> entry in SourceRects)
            {
                if (Cache.ContainsKey(entry.Key))
                    continue;

                Rectangle sourceRect = entry.Value;
                var pixels = new Color[sourceRect.Width * sourceRect.Height];
                Game1.mouseCursors.GetData(0, sourceRect, pixels, 0, pixels.Length);

                using Texture2D cropped = new(device, sourceRect.Width, sourceRect.Height);
                cropped.SetData(pixels);

                using MemoryStream ms = new();
                cropped.SaveAsPng(ms, sourceRect.Width, sourceRect.Height);
                Cache[entry.Key] = ms.ToArray();
            }
        }
    }
}
