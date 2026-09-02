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
    /// vanilla game itself draws, plus the Animals screen's own
    /// 5-heart friendship meter, its real hand-cursor "needs petting"
    /// badge, and the two large scrollbar arrows its animal table
    /// reuses from the game's own scrollable-menu chrome.
    ///
    /// The source rects below were read out of the actual decompiled
    /// <c>StardewValley.Menus.GameMenu.draw</c> (tab icons),
    /// <c>StardewValley.Menus.InventoryPage</c>'s constructor (organize
    /// button), <c>StardewValley.Menus.SkillsPage.draw</c> (skill icons
    /// + pip segments), <c>StardewValley.Menus.DayTimeMoneyBox</c> (the
    /// quest-log/journal button + its pulse badge), and
    /// <c>StardewValley.Menus.AnimalPage</c> before writing this (same
    /// verify-before-guessing approach as <see cref="PortraitRenderer"/>):
    /// each tab icon is a 16x16 cell at y=368 on the Cursors sheet, x
    /// offset by `sheetIndex * 16`, where inventory=0, skills=1,
    /// social=2, map=3, crafting=4.
    ///
    /// <c>AnimalPage</c> is worth calling out specially: vanilla 1.6
    /// added a real "Animals" page to its own <c>GameMenu</c>, which
    /// turns out to be almost exactly what this app's own Animals
    /// screen is trying to reproduce — a scrollable list of
    /// portrait+name+friendship-hearts+petting-status rows with a
    /// scrollbar down the side. Once found (well after this feature's
    /// first few rounds — see the project README's Animals risk-area
    /// note for the fuller history), it settled several rects here that
    /// earlier attempts had gotten wrong by inference or by borrowing
    /// from an unrelated real menu instead of this directly-analogous
    /// one:
    /// - `heart-filled`/`heart-empty`
    ///   (`Rectangle(211, 428, 7, 6)`/`Rectangle(218, 428, 7, 6)`) —
    ///   already correct (previously cross-checked against the real,
    ///   published `AnimalSocialMenu` mod rather than a decompile); now
    ///   also confirmed directly against `AnimalPage.drawNPCSlot`'s own
    ///   heart-drawing loop, which reads this exact same pair.
    /// - `hand-cursor` (`Rectangle(32, 0, 10, 10)`) — CORRECTED from an
    ///   earlier `(32, 0, 16, 16)` guess (sourced from a community
    ///   Cursors reference, not a decompile): `AnimalPage.drawNPCSlot`
    ///   draws this same icon, at these smaller dimensions, as its own
    ///   "needs petting" hand — the earlier guess over-cropped 6px into
    ///   the neighboring cursor frames on both axes.
    /// - `scroll-arrow-up`/`scroll-arrow-down`
    ///   (`Rectangle(421, 459, 11, 12)`/`Rectangle(421, 472, 11, 12)`) —
    ///   CORRECTED from an earlier `(76, 72, 40, 44)`/`(13, 76, 40, 44)`
    ///   pair sourced from a community wiki table with no specific menu
    ///   behind it. `AnimalPage`'s own constructor builds its up/down
    ///   scroll buttons from exactly these rects — the real Animals-menu
    ///   scrollbar this app's table is modeled after, not a borrowed
    ///   reference from an unrelated menu (e.g. Collections).
    /// - `petting-status-unpet`/`petting-status-pet` — CORRECTED from a
    ///   wrong guess entirely (a previous round used
    ///   <c>StardewValley.Menus.OptionsCheckbox</c>'s real checkbox
    ///   sprite off <see cref="Game1.mouseCursors"/> for this, on the
    ///   assumption the per-row "already pet today" indicator is a
    ///   generic checkbox). `AnimalPage.drawNPCSlot` reveals it's a
    ///   dedicated, purpose-built icon instead — see this class's field
    ///   doc comment on <see cref="SourceRects16"/> for the rect and
    ///   the real 3-state enum (not-pet/auto-pet/hand-pet) it's part of.
    /// - `table-divider-h`/`table-divider-v` — CORRECTED from tile
    ///   indices 6/5 to 25/26: `AnimalPage.draw()` calls
    ///   `drawHorizontalPartition`/`drawVerticalPartition` with
    ///   `small: true` for its own row/column dividers, and the `small`
    ///   branch of each method (read directly off the decompiled
    ///   `IClickableMenu.cs`) uses different tile indices than the
    ///   non-small branch this used before. See
    ///   <see cref="MenuTileIndices"/>'s doc comment for the rest of
    ///   this citation (asset name, helper signature).
    ///
/// The Animals nav tab's own icon (`animals-tab`) is, unlike every
/// other entry above, cropped from a *third* texture —
/// `Game1.mouseCursors_1_6` (asset `"LooseSprites\Cursors_1_6"`), not
/// `Game1.mouseCursors` — because it's the real vanilla `GameMenu`
/// "animals" tab icon 1.6 itself added, and 1.6 put its new tab
/// icons on a new sheet rather than editing the original Cursors
/// layout. Verified by reading the decompiled `GameMenu.cs` directly
/// (both its tab list, which really does include an "animals"
/// `ClickableComponent` as of 1.6, and its `draw` method's per-tab
/// icon switch) after an earlier attempt used a raw "Animals/White
/// Chicken" creature-sprite crop instead, on the mistaken assumption
/// vanilla has no real tab for this (true in 1.5.6, not in 1.6).
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

            // Animals screen — the 5-heart friendship meter. See this
            // class's own doc comment for where this rect pair came from.
            ["heart-filled"] = new Rectangle(211, 428, 7, 6),
            ["heart-empty"] = new Rectangle(218, 428, 7, 6),

            // Animals screen — the real vanilla "picking up an item"
            // hand cursor, repurposed as the per-row "needs petting"
            // badge, and the two large scrollbar arrows its table
            // reuses from vanilla's own scrollable-menu chrome. See
            // this class's own doc comment for where these rects came
            // from.
            // Rectangle(32, 0, 10, 10) — CORRECTED after finding the
            // real StardewValley.Menus.AnimalPage (vanilla 1.6's own
            // Animals menu page, which this whole feature turns out to
            // parallel closely) draws this exact same icon for its own
            // "needs petting" hand: an earlier guess used (32,0,16,16)
            // instead, over-cropping 6px into the neighboring cursor
            // frames on both axes.
            ["hand-cursor"] = new Rectangle(32, 0, 10, 10),
            // CORRECTED the same way — an earlier pass sourced these
            // from a community wiki table (stardewmodding.wiki.gg)
            // rather than any specific menu's own scrollbar, flagged
            // low-confidence at the time. AnimalPage constructs its own
            // up/down scroll buttons from these exact rects
            // (`new ClickableTextureComponent(..., Game1.mouseCursors,
            // new Rectangle(421, 459, 11, 12), 4f)` / `(421, 472, 11,
            // 12)`) — the real Animals-menu scrollbar this app's own
            // table is modeled after, not a borrowed reference from an
            // unrelated menu.
            ["scroll-arrow-up"] = new Rectangle(421, 459, 11, 12),
            ["scroll-arrow-down"] = new Rectangle(421, 472, 11, 12),

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

        // Animals screen — the table's internal grid divider lines.
        // Unlike every rect above, these crop Game1.menuTexture (a
        // different sheet than Cursors), at the tile *indices* vanilla's
        // own IClickableMenu.drawHorizontalPartition/drawVerticalPartition
        // pass to Game1.getSourceRectForStandardTileSheet.
        //
        // CORRECTED after finding the real StardewValley.Menus.AnimalPage
        // (vanilla 1.6's own Animals menu — see UiIconCache's/
        // AnimalIconCache's other doc comments for what else that class
        // settled) — its own draw() calls
        // `drawHorizontalPartition(b, y, small: true)` /
        // `drawVerticalPartition(b, x, small: true, ...)` for exactly this
        // (its own row/column dividers), and the `small: true` branch of
        // each method (read directly off the decompiled IClickableMenu.cs)
        // uses different tile indices than the non-small branch this used
        // before — 25 (horizontal) and 26 (vertical), not 6 and 5, which
        // are non-small tiles this code was never actually going to draw.
        private static readonly Dictionary<string, int> MenuTileIndices = new()
        {
            ["table-divider-h"] = 25,
            ["table-divider-v"] = 26,
        };

        // The Animals nav tab's own icon. CORRECTED after the user
        // pointed out it should come from the same real place
        // Backpack/Map/Skills' icons do — it turns out it does: vanilla
        // 1.6 actually added a real "animals" tab to GameMenu itself
        // (confirmed by reading the decompiled GameMenu.cs's own tab
        // list and its draw method's per-tab icon switch), it's just
        // drawn from a newer, second sheet — Game1.mouseCursors_1_6
        // (asset "LooseSprites\Cursors_1_6", added in 1.6 for the tabs
        // that version introduced) — rather than the original Cursors
        // sheet every entry in SourceRects above reads, which is why an
        // earlier attempt here used a raw "Animals/White Chicken" crop
        // instead of finding this. Same one-tile, no-compositing
        // technique as every other tab icon in this file.
        //
        // The Animals table's own per-row petting-status glyph also lives
        // here now — CORRECTED after it turned out the real vanilla
        // AnimalPage doesn't use OptionsCheckbox's checkbox for this at
        // all (a previous, wrong guess): its own drawNPCSlot draws
        // `Game1.mouseCursors_1_6`, `Rectangle(273 + WasPetYet * 9, 253,
        // 9, 9)`, a real 3-state icon (0 = not pet, 1 = auto-pet, 2 =
        // hand-pet) this app's own `AnimalDto.WasPet` bool only needs two
        // states of (this app doesn't currently distinguish an
        // auto-petter from the player's own hand — see AnimalDto's doc
        // comment on the mod side if that's ever added).
        private static readonly Dictionary<string, Rectangle> SourceRects16 = new()
        {
            ["animals-tab"] = new Rectangle(257, 246, 16, 16),
            ["petting-status-unpet"] = new Rectangle(273, 253, 9, 9),
            ["petting-status-pet"] = new Rectangle(291, 253, 9, 9),
        };

        private static readonly ConcurrentDictionary<string, byte[]> Cache = new();

        /// <summary>Returns the cached PNG bytes for the icon named <paramref name="name"/> ("backpack", "skills", "map", "crafting", "organize", "quality-silver"/"quality-gold"/"quality-iridium", "skill-farming"/"skill-mining"/"skill-foraging"/"skill-fishing"/"skill-combat", "pip-empty"/"pip-filled"/"pip-empty-wide"/"pip-filled-wide", "heart-filled"/"heart-empty", "hand-cursor", "scroll-arrow-up"/"scroll-arrow-down", "journal"/"journal-pulse", "watering-can-gauge", "vitals-energy-cap-top"/"vitals-energy-body"/"vitals-energy-cap-bottom"/"vitals-health-cap-top"/"vitals-health-body"/"vitals-health-cap-bottom"/"vitals-exhausted"/"vitals-droplet", "petting-status-unpet"/"petting-status-pet", "table-divider-h"/"table-divider-v", "animals-tab"), or null if unknown or not cached yet. Safe to call from any thread.</summary>
        public static byte[]? TryGet(string name) =>
            Cache.TryGetValue(name, out byte[]? bytes) ? bytes : null;

        /// <summary>Crops and caches every icon in <see cref="SourceRects"/> and <see cref="MenuTileIndices"/> that isn't cached yet — cheap no-op once warmed (these never change, unlike item sprites or the portrait). Main-thread only (touches the graphics device).</summary>
        public static void EnsureCached(GraphicsDevice device)
        {
            foreach (KeyValuePair<string, Rectangle> entry in SourceRects)
            {
                if (Cache.ContainsKey(entry.Key))
                    continue;

                Crop(entry.Key, Game1.mouseCursors, entry.Value, device);
            }

            foreach (KeyValuePair<string, int> entry in MenuTileIndices)
            {
                if (Cache.ContainsKey(entry.Key))
                    continue;

                // The real vanilla helper computes the tile's pixel
                // Rectangle from Game1.menuTexture's own actual width —
                // see this class's doc comment for why this is called
                // directly rather than a hardcoded rect.
                Rectangle sourceRect = Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, entry.Value);
                Crop(entry.Key, Game1.menuTexture, sourceRect, device);
            }

            foreach (KeyValuePair<string, Rectangle> entry in SourceRects16)
            {
                if (Cache.ContainsKey(entry.Key))
                    continue;

                Crop(entry.Key, Game1.mouseCursors_1_6, entry.Value, device);
            }
        }

        private static void Crop(string cacheKey, Texture2D sourceTexture, Rectangle sourceRect, GraphicsDevice device)
        {
            var pixels = new Color[sourceRect.Width * sourceRect.Height];
            sourceTexture.GetData(0, sourceRect, pixels, 0, pixels.Length);

            using Texture2D cropped = new(device, sourceRect.Width, sourceRect.Height);
            cropped.SetData(pixels);

            using MemoryStream ms = new();
            cropped.SaveAsPng(ms, sourceRect.Width, sourceRect.Height);
            Cache[cacheKey] = ms.ToArray();
        }
    }
}
