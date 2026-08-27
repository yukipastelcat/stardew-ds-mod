using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using StardewValley.Menus;

namespace StardewDS
{
    /// <summary>
    /// Harmony patches that suppress the vanilla toolbar/hotbar so the
    /// companion app's Backpack tab is the only place item selection
    /// happens, instead of the two disagreeing with each other.
    ///
    /// This only targets the toolbar. The clock and health/energy meters
    /// don't have their own draw methods to patch this precisely — they're
    /// drawn inline inside Game1's main HUD draw call — so those are hidden
    /// from <c>ModEntry</c> instead, via the coarser <c>Game1.displayHUD</c>
    /// flag (which also covers the toolbar; this patch is redundant with
    /// that but kept as a fallback in case a future game version stops
    /// gating the toolbar on that flag).
    /// </summary>
    internal static class HudPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Toolbar), nameof(Toolbar.draw), new[] { typeof(SpriteBatch) })]
        private static bool Toolbar_Draw_Prefix()
        {
            return false; // skip the original method entirely — don't draw the toolbar
        }
    }
}
