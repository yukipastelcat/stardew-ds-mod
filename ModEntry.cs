using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace StardewDS
{
    /// <summary>The mod entry point.</summary>
    public class ModEntry : Mod
    {
        /// <summary>
        /// Port the companion server listens on. Must match the app's
        /// default in lib/services/game_connection_service.dart.
        /// </summary>
        private const int Port = 8082;

        private CompanionServer? _server;
        private readonly object _pendingLock = new();
        private int? _pendingSelectIndex;
        private (int From, int To)? _pendingMove;
        private bool _pendingOrganize;
        private bool _pendingOpenJournal;

        /// <summary>The authoritative slot index once trigger/shoulder navigation has taken over — forced back onto <see cref="Farmer.CurrentToolIndex"/> EVERY tick, indefinitely, not just for a few ticks after the last press (see <see cref="SyncAuthoritativeToolIndex"/>'s remarks for why a bounded window turned out not to work). <see langword="null"/> only before the very first tick this session has a chance to adopt a baseline. Not guarded by <see cref="_pendingLock"/> — unlike the fields above, this one is only ever touched from SMAPI's own main-thread events, never from the companion server's background thread.</summary>
        private int? _desiredToolIndex;

        /// <summary><see cref="Game1.ticks"/> value at the last trigger/shoulder press <see cref="OnButtonPressed"/> actually accepted, or <see langword="null"/> before the first one. See <see cref="DebounceTicks"/> for what this guards against, and <see cref="OnButtonPressed"/>'s remarks for why this needs to be nullable rather than a sentinel <c>int</c> value.</summary>
        private int? _lastAcceptedPressTick;

        /// <summary>Minimum gap, in game ticks, between two accepted trigger/shoulder presses. 4 ticks (~65ms at 60 ticks/sec) is comfortably below any human's fastest deliberate repeat presses — a generous floor against real hardware bounce, not a fix for any specific observed bug (real-device logs show no evidence of genuine double-firing on either button type; see <see cref="OnButtonPressed"/>'s remarks for what those earlier double-jumps actually were).</summary>
        private const int DebounceTicks = 4;

        // ---- Debug-only counters, added 2026-09-12 for real-device
        // investigation (see DebugLogToolIndexChanges). None of these affect behavior —
        // they exist purely so a SMAPI log can be lined up against a
        // screenshot named by "how many controller presses so far": count
        // physical presses on the device while watching the log, and the
        // press number here should match. A mismatch is itself a finding
        // (see each counter's own doc comment for what it isolates).

        /// <summary>Every raw <c>SButton.LeftTrigger</c>/<c>RightTrigger</c>/<c>LeftShoulder</c>/<c>RightShoulder</c> <see cref="OnButtonPressed"/> event received, counted before the <see cref="DebounceTicks"/> filter runs. If this climbs faster than the player is physically pressing the button, SMAPI itself is delivering more than one event per physical press.</summary>
        private int _debugRawEventCount;

        /// <summary>Every raw event that survived the <see cref="DebounceTicks"/> filter and actually called <see cref="RequestCycle"/>. If THIS climbs slower than physical presses (falls behind <see cref="_debugRawEventCount"/> matching 1:1 with real presses), the debounce window is eating genuine presses — a real bug the debounce itself could introduce.</summary>
        private int _debugAcceptedPressCount;

        /// <summary>Last <c>Farmer.CurrentToolIndex</c> value logged by the per-tick change watcher in <see cref="OnUpdateTicked"/>, so that watcher logs only on an actual change instead of once per tick. <see langword="null"/> means nothing logged yet this session.</summary>
        private int? _debugLastLoggedToolIndex;

        /*********
        ** Public methods
        *********/
        /// <summary>The mod entry point, called after the mod is first loaded.</summary>
        /// <param name="helper">Provides simplified APIs for writing mods.</param>
        public override void Entry(IModHelper helper)
        {
            HudBarPatches.Monitor = this.Monitor;
            InventoryNavigationPatches.Monitor = this.Monitor;

            Harmony harmony = new(this.ModManifest.UniqueID);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            InventoryNavigationPatches.Apply(harmony);

            this._server = new CompanionServer(this.Monitor, Port, this.OnSelectRequested, this.OnMoveRequested, this.OnOrganizeRequested, this.OnOpenJournalRequested);

            helper.Events.Input.ButtonPressed += this.OnButtonPressed;
            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Raised after the game is launched, right before the first update tick.</summary>
        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            this.Monitor.Log(
                $"StardewDS loaded. Companion server starting on port {Port} — point the app at this PC's IP address on the same network.",
                LogLevel.Info
            );
            this._server?.Start();
        }

        /// <summary>Raised once per game tick -- force-removes the toolbar/clock from <c>Game1.onScreenMenus</c> as a backstop to the Harmony draw() prefixes in <see cref="HudPatches"/>, strips the vanilla stamina "sweat" droplet particles from <c>Game1.uiOverlayTempSprites</c> now that the bar they sit next to is hidden (see <see cref="HudBarPatches"/>), keeps <c>CurrentToolIndex</c> pinned to whatever trigger/shoulder navigation or the app last selected (see <see cref="SyncAuthoritativeToolIndex"/>), applies any pending item-selection/move/organize request from the app, and republishes the current state snapshot for the companion server to serve. Does not touch <c>Game1.options.hardwareCursor</c>, which is left entirely to the player.</summary>
        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (Context.IsWorldReady)
            {
                // The toolbar and clock/day/money box are meant to be
                // hidden via Harmony patches on their own draw methods (see
                // HudPatches.cs) instead of the blanket Game1.displayHUD
                // flag this used to set to false every tick. That flag also
                // happened to gate the health/energy (stamina) bars —
                // confirmed against the decompiled Game1.drawHUD, which
                // draws them inline right alongside the toolbar/clock (via
                // onScreenMenus), with no way to hide just some of what
                // that one method draws — so it was hiding those too, even
                // though nothing in the app duplicates them. Leaving
                // displayHUD at its default `true` lets them draw normally
                // again.
                //
                // 2026-08-27: screenshot evidence showed the toolbar and
                // clock still drawing in-game (SV 1.6.15) even though
                // Harmony reported both prefix patches applied with zero
                // errors — the exact draw(SpriteBatch) overload being
                // patched isn't the code path this game version actually
                // uses to render them. Rather than chase the right overload,
                // the onScreenMenus-filtering loop below is the real
                // fix: it can't draw what isn't in the list, regardless of
                // which draw() would've been called.

                // The mod deliberately does NOT touch
                // Game1.options.hardwareCursor -- that is the player's own
                // game setting (Options > Windows menu) and not this mod's
                // business either way. Earlier rounds tried flipping it to
                // true, then explicitly back to false, while troubleshooting
                // a missing-cursor-during-gameplay bug; neither direction
                // actually fixed that bug, and per feedback 2026-08-27 the
                // mod should not be changing this setting at all. Whatever
                // cursor issue remains is a separate, still-open question
                // (see project memory hud_cursor_debug.md) -- not something
                // to chase by toggling this option.

                // Belt-and-suspenders HUD hiding -- see comment above this
                // block and HudPatches.cs's own doc comment for why the
                // Harmony draw() prefixes alone weren't enough.
                // Game1.onScreenMenus is typed IList<IClickableMenu>, not
                // List<IClickableMenu> -- no RemoveAll available, so this
                // walks backwards and removes matches by index instead
                // (safe against re-indexing while iterating, unlike a
                // forward loop that RemoveAt's).
                for (int i = Game1.onScreenMenus.Count - 1; i >= 0; i--)
                {
                    if (Game1.onScreenMenus[i] is Toolbar or DayTimeMoneyBox)
                        Game1.onScreenMenus.RemoveAt(i);
                }

                // The vanilla health/energy bars themselves are removed by
                // an IL transpiler on Game1.drawHUD (see HudBarPatches.cs).
                // The blood / sweat droplet particles that vanilla spawns
                // *next to* those bars are a separate matter: they go into
                // Game1.uiOverlayTempSprites from the game's update loop
                // (not drawHUD), and the red blood ones are gated on
                // Game1.showingHealthBar — which the transpiler now keeps
                // permanently false — so those never spawn. The sky-blue
                // "sweat" droplets on the stamina side are NOT gated on
                // anything the transpiler touches, so they'd still appear
                // on the real HUD, orphaned next to a bar that's no longer
                // there. Strip them here (the app draws its own droplet
                // layer — see companion/lib/widgets/vitals_bars.dart). The
                // 5x6 source crop Rectangle(366, 412, 5, 6) on
                // Game1.mouseCursors is the exact one vanilla uses for both
                // droplet colors (verified against the decompiled
                // Game1.cs) and isn't used for anything else in the HUD.
                for (int i = Game1.uiOverlayTempSprites.Count - 1; i >= 0; i--)
                {
                    TemporaryAnimatedSprite sprite = Game1.uiOverlayTempSprites[i];
                    if (sprite.texture == Game1.mouseCursors && sprite.sourceRect == new Rectangle(366, 412, 5, 6))
                        Game1.uiOverlayTempSprites.RemoveAt(i);
                }

                // Order matters: an app tap should always win over
                // whatever trigger/shoulder navigation was last protecting
                // (see SyncAuthoritativeToolIndex's doc comment), so apply
                // the app's request first and let the sync adopt it as the
                // new baseline afterward rather than fighting to restore
                // the old one.
                bool appSelected = this.ApplyPendingSelection();
                this.SyncAuthoritativeToolIndex(appSelected);
                this.ApplyPendingMove();
                this.ApplyPendingOrganize();
                this.ApplyPendingOpenJournal();

                // Debug-only (2026-09-12 investigation) — see the method's
                // own doc comment. Runs last so it reports whatever the
                // index actually ended up at after every other step above.
                this.DebugLogToolIndexChanges();
            }

            this._server?.UpdateSnapshot(GameStateSnapshot.Capture());
        }

        /// <summary>Raised after the player returns to the title screen — clears the authoritative tool index baseline (see <see cref="SyncAuthoritativeToolIndex"/>, which will adopt whatever's current the next time a save loads, rather than trying to force a value left over from the last save) and clears the published snapshot so the app correctly reports "not connected" instead of showing stale data.</summary>
        private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
        {
            this._desiredToolIndex = null;
            this._server?.UpdateSnapshot(null);
        }


        /// <summary>Raised when any button is pressed — takes over all four of the trigger and shoulder buttons (L2/R2 and L1/R1 on the handheld this mod targets) so they step the selected slot through the player's entire backpack instead of the vanilla 12-slot hotbar.</summary>
        /// <remarks>
        /// This has gone through several real-device rounds (all
        /// 2026-09-12), each one correcting the previous round's
        /// misdiagnosis:
        ///
        /// 1. Immediately setting <c>CurrentToolIndex</c> from this handler
        ///    showed every trigger press moving the selection by *two*
        ///    slots. The working theory then was that
        ///    <c>IInputHelper.Suppress</c> was "unreliable" for analog
        ///    triggers, letting vanilla's own step land on top of ours.
        /// 2. Deferring the write to a short reassert window (forcing the
        ///    corrected value back for a few ticks, rather than setting it
        ///    once) mostly fixed it, but a capture showed the occasional
        ///    press still skipping a slot — attributed at the time to
        ///    SMAPI double-firing the press event itself, "fixed" with a
        ///    debounce.
        /// 3. A log from a session with debug logging (added specifically
        ///    to investigate further) turned out to show every single
        ///    trigger press being silently rejected by a bug in that
        ///    debounce (an <c>int.MinValue</c> sentinel, subtracted from
        ///    causing an overflow) — meaning <see cref="RequestCycle"/>
        ///    never ran that whole session for triggers, and yet
        ///    <c>CurrentToolIndex</c> still advanced by exactly one per
        ///    press. That's direct proof <c>Suppress</c> has zero effect on
        ///    these two buttons on this platform: vanilla's own handling
        ///    was 100% in control throughout every one of the three
        ///    rounds, and steps by exactly one, always, deterministically.
        ///    Reasonable conclusion at the time: stop fighting it entirely,
        ///    remove all trigger handling from this method.
        /// 4. That "stop fighting it" version's own log showed vanilla's
        ///    step is `(current + delta) % 12` — genuinely correct within
        ///    the old twelve-slot hotbar, but still capped there
        ///    (`CurrentToolIndex changed 11 -> 0`) even with row rotation
        ///    disabled, since that arithmetic has nothing to do with
        ///    <see cref="Farmer.shiftToolbar"/>. Fixed by bringing triggers
        ///    back through a reassert mechanism (as in round 2) — but that
        ///    one still used a SHORT, few-tick window.
        /// 5. A real-device log of THAT version showed the wrap was still
        ///    coming back, on a delay: the reassert window closed a few
        ///    ticks after each press (as designed), the corrected value
        ///    sat unprotected in between presses (which were 11-29 ticks
        ///    apart in that log — human-paced, far outside a 3-tick
        ///    window), and the moment the NEXT press arrived, vanilla read
        ///    whatever was sitting there (say, 12) and applied its bare
        ///    `%12` regardless of magnitude — `(12 + 1) % 12` is `1`, not
        ///    `13`. A bounded window can't work against that: vanilla's
        ///    step isn't on a clock this mod can outlast, it fires on
        ///    whatever press comes next, arbitrarily long after the last
        ///    correction. So the fix is to never stop correcting — see
        ///    <see cref="SyncAuthoritativeToolIndex"/>, called every tick
        ///    indefinitely rather than for a few ticks after a press.
        ///
        /// So triggers go through <see cref="RequestCycle"/> /
        /// <see cref="SyncAuthoritativeToolIndex"/> — the same permanent
        /// mechanism shoulders use — just without ever calling
        /// <c>Suppress</c> on them (confirmed pointless by round 3, and
        /// removed rather than left in as inert dead weight). It works
        /// BECAUSE vanilla's own write is now fully understood: it writes
        /// its own (12-capped) result to <c>CurrentToolIndex</c> whenever
        /// it next processes a trigger press, no matter how long after our
        /// last correction that is, and <see cref="SyncAuthoritativeToolIndex"/>
        /// — called every single tick, forever, not just after our own
        /// presses — overwrites that with the correct full-range value
        /// before the next frame renders.
        ///
        /// Shoulders remain unaffected by any of this: their own vanilla
        /// action (<see cref="Farmer.shiftToolbar"/>) is confirmed actually
        /// disabled (see that method's own Harmony patch), so there's no
        /// vanilla write competing with ours there at all — suppression
        /// (still applied to the shoulder buttons specifically, as
        /// defense-in-depth alongside the Harmony patch) and the shared
        /// permanent-sync mechanism are the entire story for those.
        ///
        /// Only during normal gameplay (<see cref="Context.IsPlayerFree"/>).
        /// In menus these buttons page between inventory/crafting tabs,
        /// which this must not eat.
        /// </remarks>
        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsPlayerFree)
                return;

            int delta = e.Button switch
            {
                SButton.RightTrigger => 1,
                SButton.LeftTrigger => -1,
                SButton.RightShoulder => 12,
                SButton.LeftShoulder => -12,
                _ => 0
            };
            if (delta == 0)
                return;

            // Suppression only matters for the shoulder buttons now: round
            // 3 (see remarks above) proved it has zero effect on the
            // triggers on this platform, so calling it for them would just
            // be misleading dead code — not harmful, but not honest either
            // about what's actually happening.
            if (e.Button is SButton.LeftShoulder or SButton.RightShoulder)
                this.Helper.Input.Suppress(e.Button);

            this._debugRawEventCount++;
            // _lastAcceptedPressTick is null until the first accepted
            // press — treat that as "always accept" rather than
            // subtracting against an int sentinel: Game1.ticks -
            // int.MinValue overflows (its magnitude exceeds int.MaxValue),
            // wrapping to a huge negative number that's always less than
            // DebounceTicks. A 2026-09-12 test with exactly that sentinel
            // bug showed every press for a whole session silently rejected
            // as a result — see this method's own remarks for the full
            // story that log told once cross-checked against
            // DebugLogToolIndexChanges.
            int? gap = this._lastAcceptedPressTick is int last ? Game1.ticks - last : null;
            if (gap is int g && g < DebounceTicks)
            {
                this.Monitor.Log(
                    $"[Nav] raw event #{this._debugRawEventCount} {e.Button} at tick {Game1.ticks} — REJECTED by debounce (gap {g} < {DebounceTicks} ticks since last accepted press #{this._debugAcceptedPressCount}).",
                    LogLevel.Debug
                );
                return;
            }
            this._lastAcceptedPressTick = Game1.ticks;

            this._debugAcceptedPressCount++;
            this.Monitor.Log(
                $"[Nav] raw event #{this._debugRawEventCount} {e.Button} at tick {Game1.ticks} — ACCEPTED as press #{this._debugAcceptedPressCount} (gap {gap?.ToString() ?? "n/a (first press)"} ticks), CurrentToolIndex before={Game1.player?.CurrentToolIndex.ToString() ?? "null"}.",
                LogLevel.Debug
            );

            // Shoulders only: vanilla's own trigger handling still runs
            // (unsuppressed — see the remarks above) and almost certainly
            // plays its own tool-switch sound as part of that, same as it
            // always did; playing ours too would double it up on every
            // trigger press. Shoulders have no vanilla action left at all
            // (Farmer.shiftToolbar is patched out), so nothing else plays
            // a sound for them unless we do.
            bool playSwapSound = e.Button is SButton.LeftShoulder or SButton.RightShoulder;
            this.RequestCycle(delta, playSwapSound);
        }

        /// <summary>Records a request to step the selected slot <paramref name="delta"/> places through the player's unlocked backpack, wrapping at both ends (and skipping a trailing run of empty slots on a forward step — see the remarks below), for <see cref="SyncAuthoritativeToolIndex"/> to actually apply (and keep re-applying forever — see that method). The bound is <c>MaxItems</c> — the same number the app locks its grid at (see <c>GameStateSnapshot.Capture</c>'s <c>BackpackSize</c>) — so this can only ever land on a slot the app is already drawing as unlocked.</summary>
        /// <remarks>
        /// This does NOT set <see cref="Farmer.CurrentToolIndex"/> directly
        /// — an early version of this file did, immediately, from inside
        /// <see cref="OnButtonPressed"/>, and a real-device test of that
        /// version showed the selection moving by two slots per press
        /// instead of one.
        ///
        /// Deferring the write to <see cref="SyncAuthoritativeToolIndex"/>
        /// is what makes this safe regardless of what else touches
        /// <c>CurrentToolIndex</c> in between: for the triggers, that's
        /// vanilla's own (unsuppressed — see <see cref="OnButtonPressed"/>'s
        /// remarks) 12-slot-capped step, which still runs and still writes
        /// its own (wrong, capped) result to the field; the sync simply
        /// overwrites it with this method's full-range result. A real
        /// device test (2026-09-12) of an EARLIER version of this pair —
        /// which only re-applied the corrected value for a few ticks after
        /// each press, then let the field go unprotected — showed exactly
        /// why that wasn't enough: vanilla's `%12` step doesn't run on a
        /// timer, it runs on the NEXT press, however long after our
        /// correction that is (11-29 ticks apart in that log, versus a
        /// 3-tick reassert window), and it wraps whatever's sitting in the
        /// field with no regard for its magnitude — `(12 + 1) % 12` really
        /// is `1`, not `13`. So the field has to be permanently protected,
        /// not just briefly after each press — see
        /// <see cref="SyncAuthoritativeToolIndex"/>.
        ///
        /// The *intent* is recorded here, computed from whatever
        /// <see cref="_desiredToolIndex"/> already holds (so several quick
        /// presses stack correctly instead of each reading a possibly
        /// vanilla-clobbered live <c>CurrentToolIndex</c> — which, for
        /// triggers, could hold vanilla's just-written capped value rather
        /// than ours if read directly).
        ///
        /// Forward steps (<paramref name="delta"/> &gt; 0) additionally
        /// skip a trailing run of empty slots: if <c>next</c> and every
        /// slot after it are unoccupied, there's nothing useful left to
        /// step through between here and the end of the backpack, so this
        /// jumps straight back to slot 0 instead of making the player
        /// click through each empty slot individually — see
        /// <see cref="IsBackpackTailEmpty"/>.
        /// </remarks>
        private void RequestCycle(int delta, bool playSwapSound)
        {
            Farmer? player = Game1.player;
            if (player is null)
                return;

            int capacity = player.MaxItems;
            if (capacity <= 0)
                return;

            int baseIndex = this._desiredToolIndex ?? player.CurrentToolIndex;
            int next = ((baseIndex + delta) % capacity + capacity) % capacity;

            if (delta > 0 && IsBackpackTailEmpty(player, next, capacity))
            {
                this.Monitor.Log(
                    $"[Nav] press #{this._debugAcceptedPressCount} RequestCycle: slots {next}..{capacity - 1} are all empty, skipping to slot 0 instead.",
                    LogLevel.Debug
                );
                next = 0;
            }

            this.Monitor.Log(
                $"[Nav] press #{this._debugAcceptedPressCount} RequestCycle: baseIndex={baseIndex} (from {(this._desiredToolIndex is int ? "authoritative" : "live CurrentToolIndex")}) delta={delta} capacity={capacity} -> next={next}.",
                LogLevel.Debug
            );

            this._desiredToolIndex = next;

            // See OnButtonPressed's call site for why this is conditional
            // now — vanilla already plays its own tool-switch sound for
            // triggers, since it still processes them itself.
            if (playSwapSound)
                Game1.playSound("toolSwap");
        }

        /// <summary>Whether every slot from <paramref name="from"/> (inclusive) to the end of the backpack is unoccupied — used by <see cref="RequestCycle"/> to skip a trailing run of empty slots on a forward step. A slot at or beyond <c>player.Items.Count</c> counts as empty too (see <c>ApplyPendingMove</c>'s own doc comment for why <c>Items.Count</c> only covers slots that have actually held an item, not the player's full unlocked capacity).</summary>
        private static bool IsBackpackTailEmpty(Farmer player, int from, int capacity)
        {
            for (int i = from; i < capacity; i++)
            {
                if (i < player.Items.Count && player.Items[i] is not null)
                    return false;
            }
            return true;
        }

        /// <summary>Keeps <see cref="Farmer.CurrentToolIndex"/> permanently pinned to <see cref="_desiredToolIndex"/> — called every tick, indefinitely, not just for a few ticks after a trigger/shoulder press. See <see cref="RequestCycle"/>'s doc comment for the real-device evidence that a bounded window doesn't work: vanilla's `%12` trigger step reacts to whatever's in the field on whatever press next comes along, no matter how long that is after this mod's last correction, so the only reliable fix is to never stop correcting.</summary>
        /// <remarks>
        /// Adopts the live <c>CurrentToolIndex</c> as the new authoritative
        /// baseline — rather than fighting to restore an old one — in two
        /// cases: <paramref name="appSelected"/> is true (a fresh
        /// app-originated tap just landed this tick, via
        /// <see cref="ApplyPendingSelection"/>, applied first in
        /// <see cref="OnUpdateTicked"/>, so that new selection should win
        /// and become the thing this method protects from here on), or
        /// <see cref="_desiredToolIndex"/> is still <see langword="null"/>
        /// (nothing has established a baseline yet — the very first tick
        /// this runs each session, or right after
        /// <see cref="OnReturnedToTitle"/> cleared it).
        ///
        /// Real limitation, not a currently-observed bug: this can't tell
        /// vanilla's own unwanted trigger-driven write apart from any
        /// OTHER way <c>CurrentToolIndex</c> might legitimately change
        /// outside of <see cref="ApplyPendingSelection"/> — a keyboard
        /// number-key press or mouse-wheel scroll on desktop, say — so any
        /// of those would also get silently overridden back to whatever
        /// this mod last set. Acceptable for this mod's actual target (an
        /// Android touchscreen handheld, gamepad-only), where vanilla's
        /// `%12` trigger step is the only other thing that ever touches
        /// this field.
        /// </remarks>
        private void SyncAuthoritativeToolIndex(bool appSelected)
        {
            if (Game1.player is not Farmer player)
                return;

            if (appSelected || this._desiredToolIndex is not int index || index < 0 || index >= player.MaxItems)
            {
                this._desiredToolIndex = player.CurrentToolIndex;
                return;
            }

            // Logged BEFORE overwriting: a mismatch here means vanilla's
            // own (unsuppressed) trigger handling wrote its own,
            // 12-capped result since the last time this ran — exactly the
            // drift this whole mechanism exists to correct, every single
            // tick, forever. A match means this tick is a pure no-op.
            if (player.CurrentToolIndex != index)
            {
                this.Monitor.Log(
                    $"[Nav] press #{this._debugAcceptedPressCount} sync CORRECTED drift at tick {Game1.ticks}: CurrentToolIndex was {player.CurrentToolIndex}, forcing back to {index}.",
                    LogLevel.Warn
                );
                player.CurrentToolIndex = index;
            }
        }

        /// <summary>Debug-only: logs <c>Farmer.CurrentToolIndex</c> whenever it changes from one tick to the next, tagged with the accepted-press counter so a SMAPI log can be read alongside a screenshot named by physical press count (see the counters' own doc comments). Called once per tick from <see cref="OnUpdateTicked"/>, after every other navigation step has had a chance to touch the index.</summary>
        private void DebugLogToolIndexChanges()
        {
            int? current = Game1.player?.CurrentToolIndex;
            if (current == this._debugLastLoggedToolIndex)
                return;

            this.Monitor.Log(
                $"[Nav] CurrentToolIndex changed {this._debugLastLoggedToolIndex?.ToString() ?? "(none)"} -> {current?.ToString() ?? "(none)"} at tick {Game1.ticks} (after press #{this._debugAcceptedPressCount}).",
                LogLevel.Debug
            );
            this._debugLastLoggedToolIndex = current;
        }


        /// <summary>Called from the companion server's background thread when the app requests an item be selected. Queues the request instead of applying it here — Stardew Valley's game state isn't safe to mutate off the main thread — for <see cref="OnUpdateTicked"/> to apply.</summary>
        private void OnSelectRequested(int index)
        {
            lock (this._pendingLock)
            {
                this._pendingSelectIndex = index;
            }
        }

        /// <summary>Called from the companion server's background thread when the app drags an item from one backpack slot to another. Queues the request (last one wins if several arrive before the next tick) for <see cref="OnUpdateTicked"/> to apply on the main thread.</summary>
        private void OnMoveRequested(int from, int to)
        {
            lock (this._pendingLock)
            {
                this._pendingMove = (from, to);
            }
        }

        /// <summary>Called from the companion server's background thread when the app taps the organize button. Queues the request for <see cref="OnUpdateTicked"/> to apply on the main thread.</summary>
        private void OnOrganizeRequested()
        {
            lock (this._pendingLock)
            {
                this._pendingOrganize = true;
            }
        }

        /// <summary>Called from the companion server's background thread when the app taps the new Journal button. Queues the request for <see cref="OnUpdateTicked"/> to apply on the main thread.</summary>
        private void OnOpenJournalRequested()
        {
            lock (this._pendingLock)
            {
                this._pendingOpenJournal = true;
            }
        }

        /// <summary>Applies (on the main thread) the most recent pending selection request from the app, if any. Returns whether one was actually applied, so <see cref="OnUpdateTicked"/> knows to let <see cref="SyncAuthoritativeToolIndex"/> adopt this fresher choice as its new baseline rather than fighting to restore whatever trigger/shoulder navigation was protecting before.</summary>
        private bool ApplyPendingSelection()
        {
            int? index;
            lock (this._pendingLock)
            {
                index = this._pendingSelectIndex;
                this._pendingSelectIndex = null;
            }

            if (index is int i && Game1.player is not null && i >= 0 && i < Game1.player.MaxItems)
            {
                Game1.player.CurrentToolIndex = i;
                return true;
            }

            return false;
        }

        /// <summary>Applies (on the main thread) the most recent pending move request from the app, if any — swaps whatever is in the two slots. Both indices must be within the player's current (unlocked) backpack capacity; out-of-range requests (e.g. a stale drag onto a slot that got locked) are silently dropped rather than applied partially.</summary>
        private void ApplyPendingMove()
        {
            (int From, int To)? move;
            lock (this._pendingLock)
            {
                move = this._pendingMove;
                this._pendingMove = null;
            }

            if (move is not (int from, int to))
                return;

            Farmer? player = Game1.player;
            if (player is null || from == to)
                return;
            if (from < 0 || from >= player.MaxItems || to < 0 || to >= player.MaxItems)
                return;

            // player.Items.Count only covers slots that have actually held
            // an item at some point — confirmed against the decompiled
            // Netcode.NetList<T,TField>: Count tracks real elements (not
            // capacity), and its indexer setter throws ArgumentOutOfRange
            // for index >= Count rather than auto-growing. A slot beyond
            // Count is still a legitimate empty *unlocked* slot in the
            // app's UI (see GameStateSnapshot.Capture's own `i <
            // player.Items.Count ? ... : null` guard), so pad with nulls
            // up to whichever index this move needs instead of silently
            // dropping the request — the previous version returned early
            // here, which is why dragging onto most empty slots did
            // nothing.
            // Items is typed IList<Item> (non-nullable Item), but the
            // rest of this codebase already treats it as holding real
            // nulls for empty slots (see GameStateSnapshot.Capture's
            // `Item? item = ... player.Items[i]` read) — that's genuinely
            // how the game itself uses this list, the null-forgiving `!`
            // here just matches what's already true at runtime.
            while (player.Items.Count <= from || player.Items.Count <= to)
                player.Items.Add(null!);

            (player.Items[from], player.Items[to]) = (player.Items[to], player.Items[from]);
        }

        /// <summary>Applies (on the main thread) a pending organize request from the app, if any — calls the game's own organize-button logic so the result matches exactly what pressing it in-game would do.</summary>
        private void ApplyPendingOrganize()
        {
            bool organize;
            lock (this._pendingLock)
            {
                organize = this._pendingOrganize;
                this._pendingOrganize = false;
            }

            if (!organize || Game1.player is null)
                return;

            ItemGrabMenu.organizeItemsInList(Game1.player.Items);
        }

        /// <summary>Applies (on the main thread) a pending "open journal" request from the app, if any — opens the real vanilla <see cref="QuestLog"/> menu, the same menu class the game's own journal key/quest-log button opens. Guarded the same way the real quest-log button's own click handler is (verified against the decompiled <c>DayTimeMoneyBox.receiveLeftClick</c>) — player able to move, no dialogue/event/farm-event in progress — plus not stomping an already-open menu, since a remote tap arriving mid-cutscene or while some other menu is already up shouldn't force one open.</summary>
        private void ApplyPendingOpenJournal()
        {
            bool openJournal;
            lock (this._pendingLock)
            {
                openJournal = this._pendingOpenJournal;
                this._pendingOpenJournal = false;
            }

            if (!openJournal || Game1.player is null)
                return;

            if (Game1.activeClickableMenu is not null || !Game1.player.CanMove || Game1.dialogueUp || Game1.eventUp || Game1.farmEvent is not null)
                return;

            Game1.activeClickableMenu = new QuestLog();
        }
    }
}
