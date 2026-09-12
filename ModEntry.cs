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

        /// <summary>How many further <see cref="OnUpdateTicked"/> calls will keep re-applying <see cref="_desiredToolIndex"/> after a shoulder-button press. See <see cref="RequestCycle"/>'s doc comment for why one shot isn't enough. Not guarded by <see cref="_pendingLock"/> — unlike the fields above, this one and <see cref="_desiredToolIndex"/> are only ever touched from SMAPI's own main-thread events, never from the companion server's background thread.</summary>
        private int _reassertTicksRemaining;

        /// <summary>The slot index <see cref="RequestCycle"/> last computed from a shoulder-button press, re-applied every tick while <see cref="_reassertTicksRemaining"/> is still positive. <see langword="null"/> once that window has elapsed (or been cancelled by a fresher app-originated selection — see <see cref="OnUpdateTicked"/>). The trigger buttons don't go through this any more — see <see cref="OnButtonPressed"/>'s remarks.</summary>
        private int? _desiredToolIndex;

        /// <summary>How many ticks (~<c>1000/60</c>ms apiece) to keep re-applying <see cref="_desiredToolIndex"/> after each shoulder-button press. See <see cref="RequestCycle"/>'s doc comment for what this is guarding against; 3 is a handful of frames — long enough to win against a same-press vanilla reaction landing a tick or two late, short enough that it never fights a later, legitimate change (an app tap, a different button) for more than an eyeblink.</summary>
        private const int ReassertTicks = 3;

        /// <summary><see cref="Game1.ticks"/> value at the last shoulder-button press <see cref="OnButtonPressed"/> actually accepted, or <see langword="null"/> before the first one. See <see cref="DebounceTicks"/> for what this guards against, and <see cref="OnButtonPressed"/>'s remarks for why this needs to be nullable rather than a sentinel <c>int</c> value.</summary>
        private int? _lastAcceptedPressTick;

        /// <summary>Minimum gap, in game ticks, between two accepted shoulder-button presses. 4 ticks (~65ms at 60 ticks/sec) is comfortably below any human's fastest deliberate repeat presses. (Originally sized against suspected same-press analog-trigger jitter — since triggers no longer go through this debounce at all, per <see cref="OnButtonPressed"/>'s remarks, that reasoning no longer applies here, but the value is still a reasonable floor for a physical button.)</summary>
        private const int DebounceTicks = 4;

        // ---- Debug-only counters, added 2026-09-12 for real-device
        // investigation (see DebugLogToolIndexChanges). None of these affect behavior —
        // they exist purely so a SMAPI log can be lined up against a
        // screenshot named by "how many controller presses so far": count
        // physical presses on the device while watching the log, and the
        // press number here should match. A mismatch is itself a finding
        // (see each counter's own doc comment for what it isolates).

        /// <summary>Every raw <c>SButton.LeftTrigger</c>/<c>RightTrigger</c>/<c>LeftShoulder</c>/<c>RightShoulder</c> <see cref="OnButtonPressed"/> event received. Triggers are counted but otherwise untouched (see <see cref="OnButtonPressed"/>'s remarks); shoulders are counted before the <see cref="DebounceTicks"/> filter runs. If a shoulder count climbs faster than the player is physically pressing the button, SMAPI itself is delivering more than one event per physical press.</summary>
        private int _debugRawEventCount;

        /// <summary>Every raw shoulder-button event that survived the <see cref="DebounceTicks"/> filter and actually called <see cref="RequestCycle"/>. If THIS climbs slower than physical presses (falls behind <see cref="_debugRawEventCount"/>'s shoulder-only subset matching 1:1 with real presses), the debounce window is eating genuine presses — a real bug the debounce itself could introduce.</summary>
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

        /// <summary>Raised once per game tick -- force-removes the toolbar/clock from <c>Game1.onScreenMenus</c> as a backstop to the Harmony draw() prefixes in <see cref="HudPatches"/>, strips the vanilla stamina "sweat" droplet particles from <c>Game1.uiOverlayTempSprites</c> now that the bar they sit next to is hidden (see <see cref="HudBarPatches"/>), re-asserts any slot the shoulder buttons just cycled to (see <see cref="ReassertDesiredToolIndex"/>), applies any pending item-selection/move/organize request from the app, and republishes the current state snapshot for the companion server to serve. Does not touch <c>Game1.options.hardwareCursor</c>, which is left entirely to the player.</summary>
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

                // Order matters: an app tap should always win over a
                // shoulder-button cycle still in its reassert window (see
                // ReassertDesiredToolIndex's doc comment), so apply the
                // app's request first and only reassert afterward if the
                // app didn't just pick something itself this tick.
                bool appSelected = this.ApplyPendingSelection();
                this.ReassertDesiredToolIndex(cancelled: appSelected);
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

        /// <summary>Raised after the player returns to the title screen — drops any not-yet-re-asserted shoulder-button cycle (see <see cref="ReassertDesiredToolIndex"/>) and clears the published snapshot so the app correctly reports "not connected" instead of showing stale data.</summary>
        private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
        {
            this._desiredToolIndex = null;
            this._reassertTicksRemaining = 0;
            this._server?.UpdateSnapshot(null);
        }


        /// <summary>Raised when any button is pressed — takes over the two shoulder buttons (L1/R1 on the handheld this mod targets) so they step the selected slot 12 places through the player's entire backpack instead of rotating the hotbar. The trigger buttons (L2/R2) are deliberately NOT handled here — see the remarks below for why.</summary>
        /// <remarks>
        /// Earlier versions of this method also intercepted the triggers,
        /// suppressing them and reimplementing a ±1 step ourselves — twice
        /// revised across two real-device tests chasing a double-jump, then
        /// an occasional skip. A third test (2026-09-12), with debug
        /// logging that watches <c>Farmer.CurrentToolIndex</c> independent
        /// of anything this file does (see <see cref="DebugLogToolIndexChanges"/>),
        /// settled it: a bug in that version's debounce (subtracting
        /// against an <c>int.MinValue</c> sentinel, which overflows)
        /// caused every single trigger press that whole session to be
        /// rejected before <c>RequestCycle</c> ever ran — and
        /// <c>CurrentToolIndex</c> still advanced by exactly one per press
        /// anyway. That's direct proof <c>IInputHelper.Suppress</c> has no
        /// effect at all on these two buttons on this platform (not just
        /// "unreliable" — the base game's own handling was 100% in control
        /// the entire time), and that vanilla's own single-step trigger
        /// logic is already correct on its own. The double-jumps and skips
        /// in the earlier tests were therefore almost certainly this file's
        /// own step landing on top of vanilla's, not vanilla misbehaving —
        /// a problem that fighting vanilla harder can't fix, only stopping
        /// the fight does.
        ///
        /// So the triggers are left alone now: vanilla handles them,
        /// unsuppressed, and <see cref="DebugLogToolIndexChanges"/> watches
        /// the result every tick regardless of what caused it. If vanilla's
        /// own step turns out to still wrap at slot 11 (its old
        /// hotbar-only boundary) instead of continuing into the rest of
        /// the backpack now that <see cref="InventoryNavigationPatches"/>
        /// has disabled row rotation, that specific boundary needs its own
        /// fix — but not yet known whether it does, hence leaving triggers
        /// alone first to get a clean read rather than guessing at a
        /// correction blind.
        ///
        /// The shoulder buttons are a different situation: their own
        /// vanilla action (<see cref="Farmer.shiftToolbar"/>) is
        /// confirmed actually disabled (see that method's patch), so
        /// there's no vanilla behavior left to watch or fight — suppressing
        /// them and reimplementing the ±12 jump ourselves, as below, is
        /// the entire mechanism, not a layer on top of vanilla's own.
        ///
        /// Only during normal gameplay (<see cref="Context.IsPlayerFree"/>).
        /// In menus this button pages between inventory/crafting tabs,
        /// which this must not eat.
        /// </remarks>
        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsPlayerFree)
                return;

            // Debug-only (2026-09-12): still logged so a raw trigger event
            // count is available to compare against physical presses, even
            // though nothing here acts on it any more.
            if (e.Button is SButton.LeftTrigger or SButton.RightTrigger)
            {
                this._debugRawEventCount++;
                this.Monitor.Log(
                    $"[Nav] raw event #{this._debugRawEventCount} {e.Button} at tick {Game1.ticks} — left to vanilla (not suppressed, no RequestCycle); see DebugLogToolIndexChanges for the resulting CurrentToolIndex.",
                    LogLevel.Debug
                );
                return;
            }

            int delta = e.Button switch
            {
                SButton.RightShoulder => 12,
                SButton.LeftShoulder => -12,
                _ => 0
            };
            if (delta == 0)
                return;

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

            this.RequestCycle(delta);
        }

        /// <summary>Records a request to step the selected slot <paramref name="delta"/> places through the player's unlocked backpack, wrapping at both ends, for <see cref="ReassertDesiredToolIndex"/> to actually apply (and keep re-applying for a few ticks — see that method). The bound is <c>MaxItems</c> — the same number the app locks its grid at (see <c>GameStateSnapshot.Capture</c>'s <c>BackpackSize</c>) — so this can only ever land on a slot the app is already drawing as unlocked. Only ever called for shoulder-button presses now — see <see cref="OnButtonPressed"/>'s remarks for why the triggers no longer go through this at all.</summary>
        /// <remarks>
        /// This does NOT set <see cref="Farmer.CurrentToolIndex"/> directly
        /// — an early version of this file did, immediately, from inside
        /// <see cref="OnButtonPressed"/>, and a real-device test of that
        /// version showed the selection moving by two slots per press
        /// instead of one. Deferring the actual write to
        /// <see cref="ReassertDesiredToolIndex"/> (forced once a tick for a
        /// few ticks, rather than set once here) is what makes that safe
        /// regardless of exact timing — see this method's own doc comment
        /// for the current reasoning; for the triggers specifically, later
        /// evidence showed vanilla's own action was the ENTIRE cause of
        /// that double-step (see <see cref="OnButtonPressed"/>'s remarks
        /// for the full 2026-09-12 finding), which is why this method is
        /// only reached for shoulder presses at all today — for those,
        /// <see cref="Farmer.shiftToolbar"/> is confirmed patched out, so
        /// there's no vanilla action left to race against; the deferred
        /// write here is just conservatism carried over from when there
        /// was.
        ///
        /// The *intent* is recorded here (this field, plus
        /// <see cref="_reassertTicksRemaining"/> reset to
        /// <see cref="ReassertTicks"/>), computed from whatever
        /// <see cref="_desiredToolIndex"/> already holds if a previous
        /// press's reassert window is still running (so several quick
        /// presses stack correctly instead of each reading a
        /// possibly-already-stale <c>CurrentToolIndex</c>).
        /// </remarks>
        private void RequestCycle(int delta)
        {
            Farmer? player = Game1.player;
            if (player is null)
                return;

            int capacity = player.MaxItems;
            if (capacity <= 0)
                return;

            int baseIndex = this._desiredToolIndex ?? player.CurrentToolIndex;
            int next = ((baseIndex + delta) % capacity + capacity) % capacity;

            this.Monitor.Log(
                $"[Nav] press #{this._debugAcceptedPressCount} RequestCycle: baseIndex={baseIndex} (from {(this._desiredToolIndex is int ? "pending desired" : "live CurrentToolIndex")}) delta={delta} capacity={capacity} -> next={next}.",
                LogLevel.Debug
            );

            this._desiredToolIndex = next;
            this._reassertTicksRemaining = ReassertTicks;
            Game1.playSound("toolSwap");
        }

        /// <summary>Forces <see cref="Farmer.CurrentToolIndex"/> back to <see cref="_desiredToolIndex"/> once per tick for the next few ticks after a shoulder-button press — see <see cref="RequestCycle"/>'s doc comment for the (now largely historical) reasoning. Cancelled early — before its window naturally runs out — whenever <paramref name="cancelled"/> is true, so a fresh app-originated tap (<see cref="ApplyPendingSelection"/>, applied first in <see cref="OnUpdateTicked"/>) isn't immediately stomped back to wherever the controller last pointed.</summary>
        private void ReassertDesiredToolIndex(bool cancelled)
        {
            if (cancelled)
            {
                if (this._desiredToolIndex is int cancelledIndex)
                {
                    this.Monitor.Log(
                        $"[Nav] press #{this._debugAcceptedPressCount} reassert window CANCELLED at tick {Game1.ticks} (an app selection landed first this tick) — desired index {cancelledIndex} abandoned.",
                        LogLevel.Debug
                    );
                }
                this._desiredToolIndex = null;
                this._reassertTicksRemaining = 0;
                return;
            }

            if (this._reassertTicksRemaining <= 0 || this._desiredToolIndex is not int index)
                return;

            if (Game1.player is Farmer player && index >= 0 && index < player.MaxItems)
            {
                // Logged BEFORE overwriting: a mismatch here means
                // something else (most likely vanilla, un-suppressed)
                // changed CurrentToolIndex since our last reassert —
                // exactly the drift this whole mechanism exists to correct.
                // A match means this tick's reassert is a pure no-op.
                if (player.CurrentToolIndex != index)
                {
                    this.Monitor.Log(
                        $"[Nav] press #{this._debugAcceptedPressCount} reassert CORRECTED drift at tick {Game1.ticks}: CurrentToolIndex was {player.CurrentToolIndex}, forcing back to {index} (ticksRemaining was {this._reassertTicksRemaining}).",
                        LogLevel.Warn
                    );
                }
                else
                {
                    this.Monitor.Log(
                        $"[Nav] press #{this._debugAcceptedPressCount} reassert no-op at tick {Game1.ticks}: CurrentToolIndex already {index} (ticksRemaining was {this._reassertTicksRemaining}).",
                        LogLevel.Trace
                    );
                }

                player.CurrentToolIndex = index;
            }

            this._reassertTicksRemaining--;
            if (this._reassertTicksRemaining <= 0)
                this._desiredToolIndex = null;
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

        /// <summary>Applies (on the main thread) the most recent pending selection request from the app, if any. Returns whether one was actually applied, so <see cref="OnUpdateTicked"/> knows to cancel any still-running trigger/shoulder reassert window (see <see cref="ReassertDesiredToolIndex"/>) rather than let it stomp this fresher choice back.</summary>
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
