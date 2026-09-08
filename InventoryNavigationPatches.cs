using HarmonyLib;
using StardewValley;

namespace StardewDS
{
    /// <summary>
    /// Disables vanilla's "toolbar rows" behaviour entirely, so the
    /// backpack the companion app draws and the cursor the game tracks can
    /// never disagree.
    ///
    /// Vanilla treats the backpack as N pages of 12 slots: the shift-toolbar
    /// button (right/left shoulder on a gamepad, the "R" button on the
    /// handheld this mod targets) calls
    /// <see cref="Farmer.shiftToolbar"/>, which does NOT move a cursor — it
    /// physically rotates <c>Farmer.Items</c> by 12 positions so a different
    /// twelve items occupy the hotbar. That made sense when the hotbar was
    /// the only twelve slots on screen; it's actively wrong here. The app
    /// shows all 36 slots at once and lets you tap any of them
    /// (<c>ModEntry.ApplyPendingSelection</c> sets <c>CurrentToolIndex</c>
    /// to any index inside <c>MaxItems</c>), so a rotation makes every item
    /// in the app's grid appear to teleport to a different slot while the
    /// selection stays put — the two views drift apart for no gain.
    ///
    /// So the prefix below skips the rotation outright. Nothing replaces it:
    /// with the whole backpack reachable by tapping it in the app, and with
    /// <c>ModEntry.OnButtonPressed</c> now cycling the trigger buttons
    /// across all <c>MaxItems</c> slots instead of wrapping inside the first
    /// twelve, there is no longer anything a "next 12 slots" action could
    /// usefully do. The shoulder buttons simply become inert during
    /// gameplay.
    ///
    /// This is a plain prefix rather than the defensive
    /// reflection-and-warn approach <see cref="HudBarPatches"/> uses,
    /// because <c>nameof(Farmer.shiftToolbar)</c> is checked at compile
    /// time against the real game assembly (see the README's "CI builds"
    /// section for how those get here): if a future game version drops or
    /// renames the method, the build fails loudly instead of the mod
    /// silently going back to rotating the player's inventory.
    /// </summary>
    internal static class InventoryNavigationPatches
    {
        /// <summary>Skips <see cref="Farmer.shiftToolbar"/> entirely — see the class doc comment. Returning false means the original method never runs, so <c>Farmer.Items</c> is never rotated and none of vanilla's side effects (the "shwip" sound, <c>Toolbar.shifted</c>'s slide animation, the stowed-item reset) fire either; all of those only exist to sell the rotation that no longer happens.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Farmer), nameof(Farmer.shiftToolbar), new[] { typeof(bool) })]
        private static bool Farmer_ShiftToolbar_Prefix()
        {
            return false;
        }
    }
}
