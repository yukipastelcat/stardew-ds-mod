using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Tools;
using StardewValley.WorldMaps;

namespace StardewDS
{
    /// <summary>
    /// A read-only snapshot of the game state the companion app cares
    /// about, captured on the main game thread once per tick and handed
    /// to <see cref="CompanionServer"/> to serve.
    ///
    /// NOTE: written against the Stardew Valley 1.6 Farmer/Game1 API as
    /// documented in the modding community, but not compiled/tested here
    /// (see the project README) — if `dotnet build` flags a renamed
    /// member, <see cref="TotalEarnings"/> and the <see cref="Equipment"/>
    /// fields are the ones most likely to have moved between versions,
    /// alongside the newer <see cref="MapMarkerX"/>/<see cref="MapMarkerY"/>
    /// fields, which lean on <c>StardewValley.WorldMaps.WorldMapManager</c>
    /// (the real 1.6 world-map API, confirmed against the official
    /// modding wiki's Data/WorldMap docs before writing — see
    /// <see cref="WorldMapCache"/>);
    /// everything else (Money, health, Stamina, CurrentToolIndex,
    /// dayOfMonth, timeOfDay, weather flags) has been stable for years.
    /// </summary>
    internal sealed class GameStateSnapshot
    {
        public string PlayerName { get; init; } = "";
        public string FarmName { get; init; } = "";
        public int Level { get; init; }

        /// <summary>Farmer.getTitle() — the title shown under the player's name on the real Skills page (e.g. "Newcomer"), derived from total skill level.</summary>
        public string Title { get; init; } = "";
        public int CurrentFunds { get; init; }

        /// <summary>The five skill levels the Skills screen draws a pip row for — Farmer.FarmingLevel/MiningLevel/ForagingLevel/FishingLevel/CombatLevel, each already clamped >= 0 and including any active buff bonus (the same value the real SkillsPage.draw reads for its own pip-fill check and level number). Luck isn't reported here: vanilla's own Skills page only shows it once the player has found the Special Charm, and the app's Skills screen doesn't draw a luck row (see skills_screen.dart's doc comment).</summary>
        public int FarmingLevel { get; init; }
        public int MiningLevel { get; init; }
        public int ForagingLevel { get; init; }
        public int FishingLevel { get; init; }
        public int CombatLevel { get; init; }

        /// <summary>Farmer.hasVisibleQuests — whether there's at least one non-hidden quest or special order in the player's log right now. Mirrors the real vanilla quest-log button's own visibility check (DayTimeMoneyBox only draws `questButton` at all when this is true); the app's Journal button is always shown regardless (unlike vanilla, which hides the whole button), but this still gates whether the app should treat "open journal" as meaningful.</summary>
        public bool HasVisibleQuests { get; init; }

        /// <summary>Farmer.hasNewQuestActivity() — true while there's a quest or special order the player hasn't seen/acknowledged yet (a brand-new quest, or one that just became completable). Drives the Journal button's pulsing "new activity" badge, the same condition that pulses the real in-game quest-log button (DayTimeMoneyBox.questPulseTimer, re-armed once a second while this stays true) — see UiIconCache's "journal-pulse" icon.</summary>
        public bool HasNewQuestActivity { get; init; }

        /// <summary>Team-wide lifetime earnings (money is shared in Stardew, so this isn't strictly "this farmer's" total — same figure the inventory menu shows).</summary>
        public long TotalEarnings { get; init; }

        public int Health { get; init; }
        public int MaxHealth { get; init; }
        public int Energy { get; init; }
        public int MaxEnergy { get; init; }

        public string Weekday { get; init; } = "";
        public string Season { get; init; } = "";
        public int DayOfMonth { get; init; }
        public int Year { get; init; }
        public int Hour24 { get; init; }
        public int Minute { get; init; }
        public string Weather { get; init; } = "";

        /// <summary>0=spring, 1=summer, 2=fall, 3=winter (Utility.getSeasonNumber) — pass to `GET /season-icon?n=` for the real HUD season icon.</summary>
        public int SeasonNumber { get; init; }

        /// <summary>The game's own Game1.weatherIcon code — pass to `GET /weather-icon?n=` for the real HUD weather icon.</summary>
        public int WeatherIconCode { get; init; }

        /// <summary>Display name of the location the player is currently in (e.g. "Farm", "Town", "The Mines") — <c>GameLocation.GetDisplayName()</c>, falling back to the raw internal <c>Name</c> for any location without a translated display name.</summary>
        public string LocationName { get; init; } = "";

        /// <summary>The player's position on the real vanilla world map (see <see cref="WorldMapCache"/>), as a 0-1 fraction of the map texture's width/height — multiply by the app's own rendered map size to place a marker. Null when the current location isn't mapped in `Data/WorldMap` (most mine/cave levels, a handful of interiors) — same as the real in-game map page, which simply shows no marker there either.</summary>
        public double? MapMarkerX { get; init; }

        /// <summary>See <see cref="MapMarkerX"/>.</summary>
        public double? MapMarkerY { get; init; }

        public int BackpackSize { get; init; }
        public int SelectedIndex { get; init; }
        public List<InventorySlotDto?> Inventory { get; init; } = new();

        public EquipmentDto Equipment { get; init; } = new();

        private static readonly string[] Weekdays = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

        /// <summary>
        /// Cooldown-window lengths, in milliseconds, for each melee-weapon
        /// type's special move — the divisors real vanilla
        /// <see cref="MeleeWeapon"/>.<c>drawInMenu</c> uses to turn its
        /// matching static cooldown field into the 0-1 fill fraction for
        /// the red "reloading" wipe it draws over the icon. Verified
        /// against the decompiled <c>drawInMenu</c> switch and
        /// <c>doAnimateSpecialMove</c>:
        /// <list type="bullet">
        ///   <item>stabbing sword (type 0) / defense sword (type 3): the
        ///     shared static <c>defenseCooldown</c>, set to 1500 on a
        ///     block, divided by 1500.</item>
        ///   <item>dagger (type 1): the static <c>daggerCooldown</c>, set
        ///     to 3000 on a special stab, divided by 3000.</item>
        ///   <item>club (type 2): the static <c>clubCooldown</c>, set to
        ///     6000 on a ground pound, divided by 6000.</item>
        /// </list>
        /// (Professions/enchantments halve the value that gets *set*
        /// in-game but not the divisor, so the fraction still starts at
        /// &lt;= 1.) The wipe itself is <c>Color.Red * 0.66f</c> over
        /// <c>Game1.staminaRect</c> (a 1x1 tinted pixel), not game art —
        /// the app mirrors the fraction and paints a flat color overlay
        /// rather than fetching a sprite. All three cooldown fields are
        /// <c>static</c> (shared by every weapon instance, a real vanilla
        /// quirk), so this reports the same fraction for every weapon slot
        /// of the matching type, exactly as <c>drawInMenu</c> would draw
        /// each of them. Scythes are excluded, same as <c>drawInMenu</c>.
        /// </summary>
        private const int DefenseCooldownWindowMs = 1500;
        private const int DaggerCooldownWindowMs = 3000;
        private const int ClubCooldownWindowMs = 6000;

        /// <summary>
        /// Builds a snapshot from the current game state. Must be called
        /// from the main game thread (e.g. from an UpdateTicked handler) —
        /// Stardew Valley's game state is not thread-safe. Returns null if
        /// no save is loaded, which the server reports as "not connected".
        /// </summary>
        public static GameStateSnapshot? Capture()
        {
            // Captured into a local first (rather than re-reading Game1.player
            // after the check) so the compiler's null analysis can actually
            // narrow it to non-null below, instead of just warning about it.
            Farmer? maybePlayer = Game1.player;
            if (!Context.IsWorldReady || maybePlayer is null)
                return null;

            Farmer player = maybePlayer;

            // Main-thread only (touches the graphics device) — same
            // constraint as SpriteCache, called from the same place.
            PortraitRenderer.Refresh(player, Game1.graphics.GraphicsDevice);
            MiniPortraitRenderer.Refresh(player, Game1.graphics.GraphicsDevice);
            UiIconCache.EnsureCached(Game1.graphics.GraphicsDevice);

            int seasonNumber = Utility.getSeasonNumber(Game1.currentSeason);
            SeasonWeatherIconCache.EnsureSeasonCached(seasonNumber, Game1.graphics.GraphicsDevice);
            SeasonWeatherIconCache.EnsureWeatherCached(Game1.weatherIcon, Game1.graphics.GraphicsDevice);
            PortraitBackgroundCache.EnsureCached(Game1.timeOfDay >= 1900, Game1.graphics.GraphicsDevice);
            WindowBorderCache.EnsureCached(Game1.graphics.GraphicsDevice);
        ClockCache.EnsureCached(Game1.graphics.GraphicsDevice);
        InventorySlotIconCache.EnsureCached(Game1.graphics.GraphicsDevice);
        WorldMapCache.EnsureCached(Game1.graphics.GraphicsDevice);

            // Green rain is its own event that also sets Game1.isRaining, so
            // check it first — matches the game's own weatherIcon == 999 case.
            string weather = "Sunny";
            if (Game1.isGreenRain) weather = "Green Rain";
            else if (Game1.isLightning) weather = "Stormy";
            else if (Game1.isRaining) weather = "Rainy";
            else if (Game1.isSnowing) weather = "Snowy";
            else if (Game1.isDebrisWeather) weather = "Windy";

            var inventory = new List<InventorySlotDto?>();
            for (int i = 0; i < player.MaxItems; i++)
            {
                Item? item = i < player.Items.Count ? player.Items[i] : null;
                if (item is null)
                {
                    inventory.Add(null);
                    continue;
                }

                // Cache-warm this item's icon crop now, on the main thread,
                // *before* this snapshot is published — so by the time the
                // app sees this item in a /state response, GET /sprite for
                // it is already cached and won't 404.
                SpriteCache.EnsureCached(item.QualifiedItemId);

                // Per-item state beyond name/quantity — currently just the
                // watering can's remaining water, requested explicitly.
                // `waterCanMax` turned out (confirmed by two real build
                // attempts) to be a plain instance int — the can's current
                // capacity already resolved for its upgrade level, not an
                // array to index. No array, no clamping needed.
                int? waterLeft = null;
                int? waterLeftMax = null;
                bool waterCanIsBottomless = false;
                if (item is WateringCan wateringCan)
                {
                    waterLeft = wateringCan.WaterLeft;
                    waterLeftMax = wateringCan.waterCanMax;
                    // Real vanilla `WateringCan.drawInMenu` tints its
                    // water-gauge fill BlueViolet (full opacity) for a
                    // bottomless can, vs. DodgerBlue at 70% opacity
                    // otherwise (verified against decompiled
                    // `WateringCan.cs` before writing) — the app mirrors
                    // that same color choice, so this needs to ride the
                    // snapshot alongside waterLeft/waterLeftMax.
                    waterCanIsBottomless = wateringCan.IsBottomless;
                }

                // Quality (rarity star) — verified against the
                // decompiled Object class: `Quality` (silver=1, gold=2,
                // iridium=4; 3 is unused) lives on StardewValley.Object
                // specifically, not the base Item class, so anything
                // that isn't an Object (tools, weapons, hats, rings,
                // boots) has no quality concept here and reports 0 (no
                // star) — same as vanilla's own inventory menu, which
                // only ever draws this badge for Object-type items.
                int quality = item is StardewValley.Object obj ? obj.Quality : 0;

                // "Reloading" status — the real vanilla red cooldown-wipe
                // overlay a melee weapon draws over its own icon while its
                // special move is recovering (see the cooldown-window
                // consts' doc comment). vanilla `drawInMenu`'s own switch
                // drives this off a different static field and divisor per
                // weapon type, so an earlier version here that only checked
                // `defenseCooldown` missed daggers and clubs entirely
                // (issue #3) — this mirrors the full switch instead.
                double? cooldownFraction = null;
                if (item is MeleeWeapon weapon && !weapon.isScythe())
                {
                    (int cooldown, int window) = weapon.type.Value switch
                    {
                        MeleeWeapon.stabbingSword or MeleeWeapon.defenseSword
                            => (MeleeWeapon.defenseCooldown, DefenseCooldownWindowMs),
                        MeleeWeapon.dagger => (MeleeWeapon.daggerCooldown, DaggerCooldownWindowMs),
                        MeleeWeapon.club => (MeleeWeapon.clubCooldown, ClubCooldownWindowMs),
                        _ => (0, 1),
                    };
                    if (cooldown > 0)
                        cooldownFraction = System.Math.Clamp(cooldown / (double)window, 0.0, 1.0);
                }

                inventory.Add(new InventorySlotDto
                {
                    Name = item.DisplayName,
                    Quantity = item.Stack,
                    ItemId = item.ItemId,
                    QualifiedItemId = item.QualifiedItemId,
                    WaterLeft = waterLeft,
                    WaterLeftMax = waterLeftMax,
                    WaterCanIsBottomless = waterCanIsBottomless,
                    Quality = quality,
                    CooldownFraction = cooldownFraction,
                });
            }

            GameLocation? location = player.currentLocation;
            string locationName = location?.GetDisplayName() ?? location?.Name ?? "";

            // Real vanilla world-map placement — StardewValley.WorldMaps.WorldMapManager
            // is the actual 1.6 API MapPage itself uses to place the
            // player's marker (replacing the old hardcoded per-region
            // Rectangle math from earlier versions), confirmed against
            // the official modding wiki's Data/WorldMap documentation
            // before writing this rather than guessed. GetPositionData
            // returns null for any location that isn't mapped (most
            // mine/cave levels, a handful of interiors) — same as the
            // real map page, which just shows no marker there.
            double? markerX = null;
            double? markerY = null;
            if (location is not null && WorldMapCache.Width > 0 && WorldMapCache.Height > 0)
            {
                // GetPositionData/GetMapPixelPosition take the tile as a
                // Point, not a Vector2 (player.Tile's own type) — confirmed
                // by a real `dotnet build` error (CS1503) the wiki
                // paraphrase didn't spell out the exact overload for.
                Point tilePoint = new((int)player.Tile.X, (int)player.Tile.Y);

                // GetPositionData actually returns MapAreaPositionWithContext
                // (a wrapper pairing the real MapAreaPosition with its
                // resolution context), not MapAreaPosition directly — a
                // second real `dotnet build` error (CS0029) caught this;
                // the wiki's paraphrased example elides the wrapper. `.Data`
                // unwraps it — same fix Annosz/UIInfoSuite2 landed for the
                // identical SV 1.6.14 API-shape bug (their PR #635, filed
                // against stardew-valley-dedicated-server/server#13).
                MapAreaPosition? positionData = WorldMapManager.GetPositionData(location, tilePoint)?.Data;
                if (positionData is not null)
                {
                    Vector2 pixelPos = positionData.GetMapPixelPosition(location, tilePoint);
                    // Real bug found 2026-08-28 from a screenshot showing
                    // the marker pinned to the map's bottom-right corner
                    // regardless of the player's actual position — root-
                    // caused by fetching MapAreaPosition.cs from a real
                    // 1.6 decompile (Dannode36/StardewValleyDecompiled,
                    // not previously checked — the earlier wiki-based
                    // research never surfaced this): GetMapPixelPosition
                    // returns coordinates already scaled 4x — its own
                    // GetPixelArea() does `rawArea.X * 4` etc, matching
                    // MapPage.draw's own `scale: 4f` when it draws the
                    // 300x180 base texture on screen — i.e. pixelPos is
                    // in a 0..1200 x 0..720 space for the base region,
                    // not the 0..300 x 0..180 raw-texture space
                    // WorldMapCache.Width/Height describe. Dividing by
                    // the un-scaled Width/Height alone gave a fraction up
                    // to ~4.0 — always >1, hence always clamped to the
                    // farthest corner app-side (see map_screen.dart's
                    // clamp). Fixed by dividing out that same 4x factor.
                    const double zoom = 4.0;
                    markerX = pixelPos.X / (WorldMapCache.Width * zoom);
                    markerY = pixelPos.Y / (WorldMapCache.Height * zoom);
                }
            }

            Item? hat = player.hat.Value;
            Item? leftRing = player.leftRing.Value;
            Item? rightRing = player.rightRing.Value;
            Item? boots = player.boots.Value;
            SpriteCache.EnsureCached(hat?.QualifiedItemId);
            SpriteCache.EnsureCached(leftRing?.QualifiedItemId);
            SpriteCache.EnsureCached(rightRing?.QualifiedItemId);
            SpriteCache.EnsureCached(boots?.QualifiedItemId);

            return new GameStateSnapshot
            {
                PlayerName = player.Name,
                FarmName = player.farmName.Value,
                Level = player.Level,
                Title = player.getTitle(),
                CurrentFunds = player.Money,
                TotalEarnings = player.totalMoneyEarned,

                FarmingLevel = player.FarmingLevel,
                MiningLevel = player.MiningLevel,
                ForagingLevel = player.ForagingLevel,
                FishingLevel = player.FishingLevel,
                CombatLevel = player.CombatLevel,
                HasVisibleQuests = player.hasVisibleQuests,
                HasNewQuestActivity = player.hasNewQuestActivity(),

                Health = player.health,
                MaxHealth = player.maxHealth,
                Energy = (int)player.Stamina,
                MaxEnergy = player.MaxStamina,

                Weekday = Weekdays[(Game1.dayOfMonth - 1) % 7],
                Season = Capitalize(Game1.currentSeason),
                DayOfMonth = Game1.dayOfMonth,
                Year = Game1.year,
                Hour24 = Game1.timeOfDay / 100,
                Minute = Game1.timeOfDay % 100,
                Weather = weather,
                SeasonNumber = seasonNumber,
                WeatherIconCode = Game1.weatherIcon,

                LocationName = locationName,
                MapMarkerX = markerX,
                MapMarkerY = markerY,

                BackpackSize = player.MaxItems,
                SelectedIndex = player.CurrentToolIndex,
                Inventory = inventory,

                Equipment = new EquipmentDto
                {
                    Hat = hat?.DisplayName,
                    HatId = hat?.QualifiedItemId,
                    LeftRing = leftRing?.DisplayName,
                    LeftRingId = leftRing?.QualifiedItemId,
                    RightRing = rightRing?.DisplayName,
                    RightRingId = rightRing?.QualifiedItemId,
                    Boots = boots?.DisplayName,
                    BootsId = boots?.QualifiedItemId,
                },
            };
        }

        private static string Capitalize(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }

    internal sealed class InventorySlotDto
    {
        public string Name { get; init; } = "";
        public int Quantity { get; init; }

        /// <summary>Unqualified item id (e.g. "24") — kept for backwards compat, not used for sprite lookup.</summary>
        public string ItemId { get; init; } = "";

        /// <summary>Qualified item id (e.g. "(O)24") — pass this to `GET /sprite?id=` to fetch the item's real icon.</summary>
        public string QualifiedItemId { get; init; } = "";

        /// <summary>Remaining water in a watering can; null for anything else.</summary>
        public int? WaterLeft { get; init; }

        /// <summary>Watering can capacity at its current upgrade level; null for anything else.</summary>
        public int? WaterLeftMax { get; init; }

        /// <summary>True when this watering can is enchanted bottomless (never empties); always false for anything else. Mirrors the color choice real vanilla's own `WateringCan.drawInMenu` makes for its water gauge fill (BlueViolet, full opacity, vs. DodgerBlue at 70% opacity) — see <see cref="Capture"/>'s doc comment where this is computed.</summary>
        public bool WaterCanIsBottomless { get; init; }

        /// <summary>Item quality: 0=normal (no star), 1=silver, 2=gold, 4=iridium. Only ever non-zero for <see cref="StardewValley.Object"/> items — see the doc comment where this is computed in <see cref="Capture"/>.</summary>
        public int Quality { get; init; }

        /// <summary>0-1 fraction of a melee weapon's real vanilla "reloading" cooldown-wipe overlay still remaining (1 = special move just used, 0/null = ready), or null if this item isn't a weapon on cooldown. Covers stabbing/defense swords (block), daggers (special stab) and clubs (ground pound) — each off its own vanilla cooldown field and divisor. Rides the JSON snapshot directly as a plain number rather than a sprite endpoint — the real effect is a flat color overlay, not cropped game art (see <see cref="GameStateSnapshot.DefenseCooldownWindowMs"/> and siblings' doc comment).</summary>
        public double? CooldownFraction { get; init; }
    }

    internal sealed class EquipmentDto
    {
        public string? Hat { get; init; }
        public string? HatId { get; init; }
        public string? LeftRing { get; init; }
        public string? LeftRingId { get; init; }
        public string? RightRing { get; init; }
        public string? RightRingId { get; init; }
        public string? Boots { get; init; }
        public string? BootsId { get; init; }
    }
}
