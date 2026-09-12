using System.Linq;
using HarmonyLib;
using StardewModdingAPI;
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
    /// So the prefix below skips the rotation outright. The shoulder
    /// buttons themselves aren't left idle — see <see cref="ModEntry.OnButtonPressed"/>,
    /// which repurposes them as a ±12 index jump instead (no item
    /// rotation, so alignment holds).
    /// </summary>
    /// <remarks>
    /// 2026-09-12: a real-device test of the first version of this file
    /// (Harmony prefix only, nothing else) showed rows still active — the
    /// shoulder button still rotated the hotbar. Unlike every other patch
    /// in this codebase, this one was never checked against the decompiled
    /// source (this sandbox has no access to the private <c>vendor</c>
    /// reference assemblies — see the README's "CI builds" section), so
    /// <c>nameof(Farmer.shiftToolbar)</c> — while it compiles, since
    /// <c>nameof</c> only proves the *name* exists, not that this is the
    /// overload/arity the game actually calls for a controller shoulder
    /// press — is a genuine guess, and apparently a wrong one.
    ///
    /// Rather than keep guessing at the exact method, <see cref="ModEntry.OnButtonPressed"/>
    /// now ALSO suppresses <c>SButton.LeftShoulder</c>/<c>RightShoulder</c>
    /// directly (the same technique already used for the trigger buttons —
    /// see that method's own doc comment) and repurposes them as a ±12
    /// cursor jump. That's a second, independent line of defense that
    /// doesn't depend on knowing which method the game calls: if the
    /// button itself never reaches the game, whatever it would have called
    /// doesn't get to run either. The Harmony prefix below is kept as a
    /// third line of defense (e.g. for a keyboard's Tab key, which isn't a
    /// gamepad button and so isn't covered by suppression) — see
    /// <see cref="CheckPatched"/> for how a future test can tell whether it
    /// found its target at all.
    /// </remarks>
    internal static class InventoryNavigationPatches
    {
        /// <summary>Set by <see cref="ModEntry.Entry"/> before patching, so <see cref="CheckPatched"/> can log.</summary>
        public static IMonitor? Monitor;

        /// <summary>Skips <see cref="Farmer.shiftToolbar"/> entirely — see the class doc comment. Returning false means the original method never runs, so <c>Farmer.Items</c> is never rotated and none of vanilla's side effects (the "shwip" sound, <c>Toolbar.shifted</c>'s slide animation, the stowed-item reset) fire either; all of those only exist to sell the rotation that no longer happens.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Farmer), nameof(Farmer.shiftToolbar), new[] { typeof(bool) })]
        private static bool Farmer_ShiftToolbar_Prefix()
        {
            return false;
        }

        /// <summary>Logs whether the prefix above actually found and patched <see cref="Farmer.shiftToolbar"/>. Called once from <see cref="ModEntry.Entry"/> right after <c>harmony.PatchAll</c>. This is the diagnostic the class doc comment's 2026-09-12 remark refers to: if a future game version renames or re-shapes the method, this logs a warning naming exactly what's missing instead of the row-rotation bug silently coming back with nothing in the log to explain it.</summary>
        public static void CheckPatched(Harmony harmony)
        {
            var method = AccessTools.Method(typeof(Farmer), nameof(Farmer.shiftToolbar), new[] { typeof(bool) });
            if (method is null)
            {
                Monitor?.Log(
                    "InventoryNavigationPatches: Farmer.shiftToolbar(bool) not found — the Harmony prefix has nothing to patch, so shoulder-button suppression in ModEntry.OnButtonPressed is now the only thing stopping row rotation (fine for gamepad play, but a keyboard's Tab key would still shift rows).",
                    LogLevel.Warn
                );
                return;
            }

            bool patched = harmony.GetPatchedMethods().Contains(method);
            Monitor?.Log(
                patched
                    ? "InventoryNavigationPatches: Farmer.shiftToolbar(bool) patched OK."
                    : "InventoryNavigationPatches: Farmer.shiftToolbar(bool) found but Harmony didn't record it as patched — check for a PatchAll scanning issue.",
                patched ? LogLevel.Trace : LogLevel.Warn
            );
        }
    }
}
