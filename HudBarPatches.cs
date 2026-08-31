using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;

namespace StardewDS
{
    /// <summary>
    /// Suppresses just the vanilla health and energy (stamina) bars from
    /// the in-game HUD, so the companion app can be the only thing drawing
    /// them (next to its clock — see
    /// <c>companion/lib/widgets/vitals_bars.dart</c>), the same split the
    /// mod already does for the toolbar and clock/day box (see
    /// <see cref="HudPatches"/>).
    ///
    /// Unlike the toolbar and clock — separate <c>IClickableMenu</c>s that
    /// <see cref="HudPatches"/> / <c>ModEntry.OnUpdateTicked</c> can drop
    /// from <c>Game1.onScreenMenus</c> wholesale — the two bars are drawn
    /// *inline* inside <c>Game1.drawHUD</c> itself, right before its
    /// <c>onScreenMenus</c> loop. There's no per-widget flag for them
    /// (<c>Game1.displayHUD = false</c> would hide the entire HUD —
    /// buff icons, level-up bars, toast notifications and all — which is
    /// why <see cref="HudPatches"/>'s doc comment records that being
    /// abandoned), so this is an IL transpiler that snips out exactly the
    /// bar-drawing region of <c>drawHUD</c> and leaves everything after it
    /// (the <c>onScreenMenus</c> loop, the profession-17 forage sparkles)
    /// untouched.
    ///
    /// The excised region, verified against the decompiled SV 1.6
    /// <c>Game1.drawHUD</c> (github.com/Dannode36/StardewValleyDecompiled —
    /// same verify-first approach as <see cref="SeasonWeatherIconCache"/>
    /// and <see cref="UiIconCache"/>), runs from:
    /// <code>
    ///   float modifier = 0.625f;                       // first `ldc.r4 0.625`
    ///   Vector2 topOfBar = new Vector2(...);
    ///   ... draw stamina frame + fill, exhausted face ...
    ///   if (currentLocation is MineShaft || ... || player.health &lt; player.maxHealth) {
    ///       ... draw health frame + fill ...
    ///   } else { showingHealth = false; }
    /// </code>
    /// up to (not including):
    /// <code>
    ///   foreach (IClickableMenu menu in onScreenMenus) { ... }   // first `ldsfld Game1::onScreenMenus`
    /// </code>
    /// and is replaced with just <c>showingHealth = showingHealthBar = false;</c>
    /// so the per-second blood-droplet spawn that vanilla gates on
    /// <c>showingHealthBar</c> (<c>Game1.UpdateControlInput</c>-adjacent,
    /// at <c>player.health &lt;= 10</c>) never fires on the real HUD — the
    /// companion runs its own droplet layer instead. (The stamina "sweat"
    /// droplets aren't gated on those flags, so <c>ModEntry.OnUpdateTicked</c>
    /// strips those from <c>Game1.uiOverlayTempSprites</c> separately.)
    ///
    /// If the anchors aren't found (a future game version reshapes
    /// <c>drawHUD</c>), this logs a warning and returns the method
    /// unchanged — the bars stay visible rather than the transpiler
    /// throwing and taking the whole HUD down with it. Same defensive
    /// stance <see cref="HudPatches"/>'s remarks describe.
    /// </summary>
    [HarmonyPatch]
    internal static class HudBarPatches
    {
        /// <summary>Set once from <see cref="ModEntry.Entry"/> so the transpiler can report a failed match. Null-safe — a missing monitor just means no log line.</summary>
        internal static IMonitor? Monitor;

        [HarmonyPatch(typeof(Game1), "drawHUD")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> DrawHud_RemoveBars(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();

            int start = code.FindIndex(ci =>
                ci.opcode == OpCodes.Ldc_R4 && ci.operand is float f && f == 0.625f);

            int end = start < 0 ? -1 : code.FindIndex(start + 1, ci =>
                ci.opcode == OpCodes.Ldsfld && ci.operand is FieldInfo fi && fi.Name == "onScreenMenus");

            var showingHealth = AccessTools.Field(typeof(Game1), nameof(Game1.showingHealth));
            var showingHealthBar = AccessTools.Field(typeof(Game1), nameof(Game1.showingHealthBar));

            if (start < 0 || end < 0 || end <= start || showingHealth is null || showingHealthBar is null)
            {
                Monitor?.Log(
                    "Could not find the health/energy bar region in Game1.drawHUD to remove "
                    + $"(modifier anchor idx={start}, onScreenMenus anchor idx={end}, "
                    + $"showingHealth field {(showingHealth is null ? "missing" : "ok")}) — the vanilla bars "
                    + "will stay visible in-game. The game's drawHUD may have changed shape; the app-side "
                    + "bars in the companion still work, they'll just be duplicated on the main screen.",
                    LogLevel.Warn);
                return code;
            }

            // Everything the removed instructions carried (branch-target
            // labels for the `if (exhausted)` / health-bar `if` that lived
            // entirely inside this region, plus the fall-through label on
            // the first one, if any) is re-hosted on the first replacement
            // instruction so nothing dangles. Nothing outside the region
            // branches into it (the method's own early-out `return` jumps
            // past everything to `ret`), so this is safe.
            var carriedLabels = code.Skip(start).Take(end - start).SelectMany(ci => ci.labels).ToList();

            var replacement = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldc_I4_0),
                new CodeInstruction(OpCodes.Stsfld, showingHealth),
                new CodeInstruction(OpCodes.Ldc_I4_0),
                new CodeInstruction(OpCodes.Stsfld, showingHealthBar),
            };
            replacement[0].labels.AddRange(carriedLabels);

            code.RemoveRange(start, end - start);
            code.InsertRange(start, replacement);

            Monitor?.Log("Removed the vanilla health/energy bars from Game1.drawHUD.", LogLevel.Trace);
            return code;
        }
    }
}
