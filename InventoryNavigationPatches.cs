using System;
using System.Reflection;
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
    /// So <see cref="Apply"/> below skips the rotation outright. The
    /// shoulder buttons themselves aren't left idle — see
    /// <see cref="ModEntry.OnButtonPressed"/>, which repurposes them as a
    /// ±12 index jump instead (no item rotation, so alignment holds).
    /// </summary>
    /// <remarks>
    /// 2026-09-12, first real-device test: rows still active — the
    /// shoulder button still rotated the hotbar. The method target
    /// (<c>Farmer.shiftToolbar(bool)</c>, checked by <c>nameof</c>) was
    /// never verified against decompiled source (no <c>vendor</c> access in
    /// the sandbox that wrote it), so the working theory was a wrong
    /// overload/signature. Fixed by having <see cref="ModEntry.OnButtonPressed"/>
    /// ALSO suppress <c>SButton.LeftShoulder</c>/<c>RightShoulder</c>
    /// directly (repurposed as a ±12 cursor jump) as a second, independent
    /// line of defense that doesn't depend on knowing which method the game
    /// calls.
    ///
    /// Second real-device test, same day: the SMAPI log settled the
    /// question the first fix couldn't —
    /// <c>AccessTools.Method(typeof(Farmer), nameof(Farmer.shiftToolbar), new[] { typeof(bool) })</c>
    /// DOES find the method (so the signature was right all along), but
    /// <c>harmony.GetPatchedMethods()</c> didn't include it after
    /// <c>harmony.PatchAll(Assembly.GetExecutingAssembly())</c> — the
    /// attribute-based scan itself wasn't applying this particular patch,
    /// for reasons that log line can't explain any further (every other
    /// annotated patch in this mod, including two in this same file's
    /// sibling <see cref="HudPatches"/> using the identical
    /// `[HarmonyPrefix] + [HarmonyPatch(...)]`-on-one-method style, applied
    /// fine per the same log). Rather than keep guessing at PatchAll's
    /// internals with no way to compile-test the guess, <see cref="Apply"/>
    /// now patches this one method imperatively — <c>harmony.Patch(...)</c>
    /// called directly from <see cref="ModEntry.Entry"/>, wrapped in its own
    /// try/catch — instead of relying on attribute scanning to find it.
    /// This sidesteps the mystery entirely: either it patches (and logs
    /// success) or the catch block logs the real exception, which
    /// attribute-based scanning never surfaces at all.
    /// </remarks>
    internal static class InventoryNavigationPatches
    {
        /// <summary>Set by <see cref="ModEntry.Entry"/> before patching, so <see cref="Apply"/> can log.</summary>
        public static IMonitor? Monitor;

        /// <summary>Patches <see cref="Farmer.shiftToolbar"/> to a no-op — see the class doc comment for why this is applied imperatively rather than via <c>harmony.PatchAll</c>'s attribute scan. Called once from <see cref="ModEntry.Entry"/>, after <c>PatchAll</c> has applied every other patch in the mod.</summary>
        public static void Apply(Harmony harmony)
        {
            MethodBase? method = AccessTools.Method(typeof(Farmer), nameof(Farmer.shiftToolbar), new[] { typeof(bool) });
            if (method is null)
            {
                Monitor?.Log(
                    "InventoryNavigationPatches: Farmer.shiftToolbar(bool) not found — nothing to patch, so shoulder-button suppression in ModEntry.OnButtonPressed is now the only thing stopping row rotation (fine for gamepad play, but a keyboard's Tab key would still shift rows).",
                    LogLevel.Warn
                );
                return;
            }

            try
            {
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(InventoryNavigationPatches), nameof(Farmer_ShiftToolbar_Prefix)));
                Monitor?.Log("InventoryNavigationPatches: Farmer.shiftToolbar(bool) patched OK.", LogLevel.Trace);
            }
            catch (Exception ex)
            {
                Monitor?.Log(
                    $"InventoryNavigationPatches: failed to patch Farmer.shiftToolbar(bool) — {ex.GetType().Name}: {ex.Message}. Shoulder-button suppression in ModEntry.OnButtonPressed is still stopping row rotation from a gamepad; a keyboard's Tab key would not be covered.",
                    LogLevel.Error
                );
            }
        }

        /// <summary>Skips <see cref="Farmer.shiftToolbar"/> entirely — see the class doc comment. Returning false means the original method never runs, so <c>Farmer.Items</c> is never rotated and none of vanilla's side effects (the "shwip" sound, <c>Toolbar.shifted</c>'s slide animation, the stowed-item reset) fire either; all of those only exist to sell the rotation that no longer happens. Not attribute-patched — see <see cref="Apply"/>, which applies this method to <see cref="Farmer.shiftToolbar"/> imperatively.</summary>
        private static bool Farmer_ShiftToolbar_Prefix()
        {
            // Debug-only (2026-09-12 investigation): confirms at runtime,
            // not just at Apply()-time, that the patch is actually in the
            // call path — e.g. rules out the (extremely unlikely, but
            // cheap to rule out given Apply()'s own success log wasn't
            // enough to explain the first real-device failure) case of a
            // JIT-inlined call to the original method bypassing Harmony's
            // patched version.
            Monitor?.Log("[Nav] Farmer.shiftToolbar prefix invoked — blocking row rotation.", LogLevel.Debug);
            return false;
        }
    }
}
