using System.Reflection;
using HarmonyLib;
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

        /*********
        ** Public methods
        *********/
        /// <summary>The mod entry point, called after the mod is first loaded.</summary>
        /// <param name="helper">Provides simplified APIs for writing mods.</param>
        public override void Entry(IModHelper helper)
        {
            Harmony harmony = new(this.ModManifest.UniqueID);
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            this._server = new CompanionServer(this.Monitor, Port, this.OnSelectRequested, this.OnMoveRequested, this.OnOrganizeRequested);

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

        /// <summary>Raised once per game tick -- force-removes the toolbar/clock from <c>Game1.onScreenMenus</c> as a backstop to the Harmony draw() prefixes in <see cref="HudPatches"/>, applies any pending item-selection/move/organize request from the app, and republishes the current state snapshot for the companion server to serve. Does not touch <c>Game1.options.hardwareCursor</c>, which is left entirely to the player.</summary>
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

                this.ApplyPendingSelection();
                this.ApplyPendingMove();
                this.ApplyPendingOrganize();
            }

            this._server?.UpdateSnapshot(GameStateSnapshot.Capture());
        }

        /// <summary>Raised after the player returns to the title screen — clears the published snapshot so the app correctly reports "not connected" instead of showing stale data.</summary>
        private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
        {
            this._server?.UpdateSnapshot(null);
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

        /// <summary>Applies (on the main thread) the most recent pending selection request from the app, if any.</summary>
        private void ApplyPendingSelection()
        {
            int? index;
            lock (this._pendingLock)
            {
                index = this._pendingSelectIndex;
                this._pendingSelectIndex = null;
            }

            if (index is int i && Game1.player is not null && i >= 0 && i < Game1.player.MaxItems)
                Game1.player.CurrentToolIndex = i;
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
    }
}
