using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Tools;

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
    /// fields are the ones most likely to have moved between versions;
    /// everything else (Money, health, Stamina, CurrentToolIndex,
    /// dayOfMonth, timeOfDay, weather flags) has been stable for years.
    /// </summary>
    internal sealed class GameStateSnapshot
    {
        public string PlayerName { get; init; } = "";
        public string FarmName { get; init; } = "";
        public int Level { get; init; }
        public int CurrentFunds { get; init; }

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

        public int BackpackSize { get; init; }
        public int SelectedIndex { get; init; }
        public List<InventorySlotDto?> Inventory { get; init; } = new();

        public EquipmentDto Equipment { get; init; } = new();

        private static readonly string[] Weekdays = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

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
            UiIconCache.EnsureCached(Game1.graphics.GraphicsDevice);

            int seasonNumber = Utility.getSeasonNumber(Game1.currentSeason);
            SeasonWeatherIconCache.EnsureSeasonCached(seasonNumber, Game1.graphics.GraphicsDevice);
            SeasonWeatherIconCache.EnsureWeatherCached(Game1.weatherIcon, Game1.graphics.GraphicsDevice);
            PortraitBackgroundCache.EnsureCached(Game1.timeOfDay >= 1900, Game1.graphics.GraphicsDevice);
            WindowBorderCache.EnsureCached(Game1.graphics.GraphicsDevice);
        ClockCache.EnsureCached(Game1.graphics.GraphicsDevice);
        InventorySlotIconCache.EnsureCached(Game1.graphics.GraphicsDevice);

            string weather = "Sunny";
            if (Game1.isLightning) weather = "Stormy";
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
                if (item is WateringCan wateringCan)
                {
                    waterLeft = wateringCan.WaterLeft;
                    waterLeftMax = wateringCan.waterCanMax;
                }

                inventory.Add(new InventorySlotDto
                {
                    Name = item.DisplayName,
                    Quantity = item.Stack,
                    ItemId = item.ItemId,
                    QualifiedItemId = item.QualifiedItemId,
                    WaterLeft = waterLeft,
                    WaterLeftMax = waterLeftMax,
                });
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
                CurrentFunds = player.Money,
                TotalEarnings = player.totalMoneyEarned,

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
