using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using StardewValley.Menus;

namespace StardewDS
{
    /// <summary>
    /// Harmony patches that suppress specific pieces of the vanilla HUD that
    /// the companion app duplicates, so the two don't disagree with each
    /// other:
    /// - The toolbar/hotbar (item selection happens in the app's Backpack
    ///   tab instead).
    /// - The day/time/money box, which also draws the clock, season icon
    ///   and weather icon (the app renders these too — see
    ///   ClockCache/SeasonWeatherIconCache).
    ///
    /// The health and energy (stamina) bars are drawn inline inside
    /// Game1.drawHUD itself rather than by a separate menu class, so they
    /// can't be patched out the same way — and per the mod's current
    /// design they're no longer hidden at all (Game1.displayHUD is left at
    /// its default `true`, so drawHUD runs and draws them normally).
    /// Previously this mod set Game1.displayHUD = false every tick, which
    /// coarsely hid the toolbar, clock, AND the health/energy bars
    /// together (Stardew doesn't expose a way to hide just some of what
    /// drawHUD draws) — these two per-widget patches replace that, so the
    /// bars can stay visible while the toolbar and clock stay hidden.
    /// </summary>
    internal static class HudPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Toolbar), nameof(Toolbar.draw), new[] { typeof(SpriteBatch) })]
        private static bool Toolbar_Draw_Prefix()
        {
            return false; // skip the original method entirely — don't draw the toolbar
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DayTimeMoneyBox), nameof(DayTimeMoneyBox.draw), new[] { typeof(SpriteBatch) })]
        private static bool DayTimeMoneyBox_Draw_Prefix()
        {
            return false; // skip the original method entirely — don't draw the clock/day/money/season/weather box
        }
    }
}
