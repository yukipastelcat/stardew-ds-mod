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
    /// the bottom nav's backpack/skills/map/crafting tab icons, the
    /// backpack screen's organize and journal buttons, the three
    /// item-quality star badges (silver/gold/iridium), the five skill
    /// icons and skill-bar "pip" segments the Skills screen draws, the
    /// quest-log button's own "new activity" pulse badge, the
    /// watering can's own water-level gauge frame, and the health/energy
    /// (stamina) bar pieces + their status decorations (tired face,
    /// blood/sweat droplet) — straight
    /// out of the game's own `Cursors` spritesheet
    /// (<see cref="Game1.mouseCursors"/>), the exact same icons the
    /// vanilla game itself draws.
    ///
    /// The source rects below were read out of the actual decompiled
    /// <c>StardewValley.Menus.GameMenu.draw</c> (tab icons),
    /// <c>StardewValley.Menus.InventoryPage</c>'s constructor (organize
    /// button), <c>StardewValley.Menus.SkillsPage.draw</c> (skill icons
    /// + pip segments), and <c>StardewValley.Menus.DayTimeMoneyBox</c>
    /// (the quest-log/journal button + its pulse badge) before writing
    /// this (same verify-before-guessing approach as
    /// <see cref="PortraitRenderer"/>): each tab icon is a 16x16 cell
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

            // Item quality star badges — the small icon vanilla's own
            // Object.drawInMenu draws over an item's sprite for
            // silver/gold/iridium quality. Verified against the
            // decompiled Object class (same rects cited when a 1.5.4
            // bug report about ColoredObject's outdated copy of this
            // logic was fixed): silver/gold follow an 8px-per-step
            // pattern starting at (338, 400); iridium (added after that
            // pattern was established) doesn't fit it and lives at a
            // separate (346, 392) instead. Quality 3 has no star (never
            // used by the game); quality 0 (normal) draws no badge at
            // all, so it has no entry here.
            ["quality-silver"] = new Rectangle(338, 400, 8, 8),
            ["quality-gold"] = new Rectangle(346, 400, 8, 8),
            ["quality-iridium"] = new Rectangle(346, 392, 8, 8),

            // Skills screen — five skill icons, verified against the
            // decompiled SkillsPage.draw's per-skill `iconSource` switch
            // (all 10x10 cells at y=428 on Cursors). Luck has no icon
            // here since the Skills screen doesn't show a luck row (it
            // stays hidden until the player finds the Special Charm, same
            // as vanilla) — only the five skills the app's Skills screen
            // actually draws are cropped.
            ["skill-farming"] = new Rectangle(10, 428, 10, 10),
            ["skill-mining"] = new Rectangle(30, 428, 10, 10),
            ["skill-foraging"] = new Rectangle(60, 428, 10, 10),
            ["skill-fishing"] = new Rectangle(20, 428, 10, 10),
            ["skill-combat"] = new Rectangle(120, 428, 10, 10),

            // Skills screen — the ten level-progress "pip" segments per
            // skill row. Vanilla draws a narrower 8x9 pip for positions
            // 1-4/6-9 and a wider 14x9 pip for the 5th/10th (the
            // level-5/level-10 profession-milestone markers), each in an
            // empty/filled pair — verified against SkillsPage.draw's own
            // `(i + 1) % 5 == 0` branch. The same filled-wide crop is
            // also what SkillsPage's constructor uses as the base sprite
            // for an actually-chosen profession badge, but this mod
            // doesn't crop that separately since the app's Skills screen
            // doesn't render profession badges (see skills_screen.dart's
            // doc comment for the deliberate scope cut).
            ["pip-empty"] = new Rectangle(129, 338, 8, 9),
            ["pip-filled"] = new Rectangle(137, 338, 8, 9),
            ["pip-empty-wide"] = new Rectangle(145, 338, 14, 9),
            ["pip-filled-wide"] = new Rectangle(159, 338, 14, 9),

            // Backpack screen's new Journal button — the exact
            // `DayTimeMoneyBox.questButton` icon (opens the real
            // `QuestLog` menu in-game when clicked; see
            // CompanionServer's `POST /open-journal`), and the small "!"
            // badge that same widget pulses over the button while
            // `Farmer.hasNewQuestActivity()` is true — both verified
            // against the decompiled DayTimeMoneyBox source before
            // writing.
            ["journal"] = new Rectangle(383, 493, 11, 14),
            ["journal-pulse"] = new Rectangle(395, 497, 3, 8),

            // Backpack toolbar's funds cell — the exact sprites vanilla's
            // `DayTimeMoneyBox.drawMoneyBox` / `MoneyDial.draw` use
            // (verified against the decompiled 1.6 source):
            //   - the money-box backdrop (coin icon + digit well),
            //     `Rectangle(340, 472, 65, 17)` on `Game1.mouseCursors`,
            //     drawn at 4x. Its top edge is the visual join to the
            //     clock box above it in-game; the companion renders it
            //     standalone (see `funds_box.dart`).
            //   - the 0-9 digit glyphs, `Rectangle(286, 502 - d*8, 5, 8)`,
            //     drawn Maroon-tinted at 4x with a 24px advance. Served
            //     one per digit so the app can compose an arbitrary
            //     amount, same as the per-quality-star / per-skill-icon
            //     entries above.
            ["money-box"] = new Rectangle(340, 472, 65, 17),
            ["money-digit-0"] = new Rectangle(286, 502, 5, 8),
            ["money-digit-1"] = new Rectangle(286, 494, 5, 8),
            ["money-digit-2"] = new Rectangle(286, 486, 5, 8),
            ["money-digit-3"] = new Rectangle(286, 478, 5, 8),
            ["money-digit-4"] = new Rectangle(286, 470, 5, 8),
            ["money-digit-5"] = new Rectangle(286, 462, 5, 8),
            ["money-digit-6"] = new Rectangle(286, 454, 5, 8),
            ["money-digit-7"] = new Rectangle(286, 446, 5, 8),
            ["money-digit-8"] = new Rectangle(286, 438, 5, 8),
            ["money-digit-9"] = new Rectangle(286, 430, 5, 8),

            // Health / energy (stamina) bar pieces — the exact crops
            // vanilla `Game1.drawHUD` draws for the two bottom-right HUD
            // bars (verified against the decompiled `drawHUD`, SV 1.6:
            // stamina frame at x=256, health frame at x=268, each a
            // 3-piece vertical sprite — 16px top cap, 16px stretchable
            // middle, 16px bottom cap — drawn at 4x). The mod hides the
            // real in-game bars (see `HudBarPatches.cs`) and the companion
            // app redraws them next to its clock from these crops; the
            // colored fill itself isn't a sprite (it's `Game1.staminaRect`
            // tinted `Utility.getRedToGreenLerpColor`), reproduced app-side
            // as a plain rect the same way the watering-can gauge fill and
            // the weapon cooldown wipe are.
            ["vitals-energy-cap-top"] = new Rectangle(256, 408, 12, 16),
            ["vitals-energy-body"] = new Rectangle(256, 424, 12, 16),
            ["vitals-energy-cap-bottom"] = new Rectangle(256, 448, 12, 16),
            ["vitals-health-cap-top"] = new Rectangle(268, 408, 12, 16),
            ["vitals-health-body"] = new Rectangle(268, 424, 12, 16),
            ["vitals-health-cap-bottom"] = new Rectangle(268, 448, 12, 16),

            // The little "tired" face vanilla `drawHUD` draws above the
            // stamina bar while `Farmer.exhausted` is true —
            // `Rectangle(191, 406, 12, 11)` on the Cursors sheet.
            ["vitals-exhausted"] = new Rectangle(191, 406, 12, 11),

            // The 5x6 droplet vanilla spawns into `Game1.uiOverlayTempSprites`
            // near the bars — red blood drops when `health <= 10`
            // (`Game1.drawHUD`'s per-second check), sky-blue sweat drops
            // when stamina is low and a tool is used. Same source rect for
            // both, tinted per spawn. The app runs its own particle layer
            // from this crop; the mod strips the vanilla ones off the real
            // HUD (see `ModEntry.OnUpdateTicked`).
            ["vitals-droplet"] = new Rectangle(366, 412, 5, 6),

            // Watering can's own water-level gauge background/frame —
            // the exact crop vanilla's `WateringCan.drawInMenu` draws
            // via `spriteBatch.Draw(Game1.mouseCursors, location + new
            // Vector2(4f, 44f), new Rectangle(297, 420, 14, 5), ...)`
            // (verified against decompiled `WateringCan.cs` before
            // writing, per this project's convention). Only the
            // background/frame is a sprite — the fill itself is a
            // plain solid-color rect (`Game1.staminaRect`, DodgerBlue
            // at 70% opacity, or BlueViolet at full opacity for a
            // bottomless can), reproduced app-side as a `ColoredBox`
            // the same way the sword cooldown-wipe overlay is (see
            // `InventorySlotDto.CooldownFraction`) rather than as a
            // second cropped icon.
            ["watering-can-gauge"] = new Rectangle(297, 420, 14, 5),
        };

        private static readonly ConcurrentDictionary<string, byte[]> Cache = new();

        /// <summary>Returns the cached PNG bytes for the icon named <paramref name="name"/> ("backpack", "skills", "map", "crafting", "organize", "quality-silver"/"quality-gold"/"quality-iridium", "skill-farming"/"skill-mining"/"skill-foraging"/"skill-fishing"/"skill-combat", "pip-empty"/"pip-filled"/"pip-empty-wide"/"pip-filled-wide", "journal"/"journal-pulse", "money-box"/"money-digit-0".."money-digit-9", "watering-can-gauge", "vitals-energy-cap-top"/"vitals-energy-body"/"vitals-energy-cap-bottom"/"vitals-health-cap-top"/"vitals-health-body"/"vitals-health-cap-bottom"/"vitals-exhausted"/"vitals-droplet"), or null if unknown or not cached yet. Safe to call from any thread.</summary>
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
