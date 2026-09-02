# stardew-ds-mod

SMAPI mod for **StardewDS** — hides the vanilla hotbar, clock/day/money
box, and health/energy (stamina) bars (the phone app becomes the source of
truth for those instead of the game drawing its own copy on screen), and
exposes the player's inventory and stats to the
[`companion-app`](../companion-app) Flutter app over a small local HTTP
server. The app redraws the health/energy bars next to its clock —
including the vanilla "tired" face, the low-health pulse, the on-damage /
low-stamina shake, and the blood/sweat droplet particles.

## Status

Companion server + HUD hiding implemented (2026-08-27), but **not yet
compiled or run against the real game** — this sandbox has no `dotnet` SDK
and no network access to NuGet, so `dotnet build` has never actually been
attempted. The first real test is on your machine; see "Known risk areas"
below for exactly what to check if the build fails.

What it does once running:
- Harmony-patches `Toolbar.draw` and `DayTimeMoneyBox.draw` (both
  `StardewValley.Menus`) to skip drawing entirely — this hides just the
  hotbar and the clock/day/money/season/weather box, nothing else. An
  earlier version of this mod set `Game1.displayHUD = false` every tick
  instead, which was coarser than intended: that flag also gates the
  health/energy (stamina) bars (drawn inline inside `Game1.drawHUD`,
  right alongside the toolbar/clock, via the same `onScreenMenus` loop —
  confirmed against the decompiled source — so there was no way to hide
  only some of what it draws), so it was hiding those too; switched to
  per-widget Harmony patches (toolbar + clock) instead. The health/energy
  bars are now hidden as well, but deliberately *not* via `displayHUD` —
  an IL transpiler on `Game1.drawHUD` (`HudBarPatches.cs`) snips out just
  the two bar-drawing blocks and leaves the rest of `drawHUD` (buff icons,
  level-up bars, the profession-17 forage sparkles) alone. The companion
  app redraws the bars from real cropped sprites (`UiIconCache`'s
  `vitals-*` entries) next to its clock.
- Strips the vanilla stamina "sweat" droplet particles from
  `Game1.uiOverlayTempSprites` each tick (`ModEntry.OnUpdateTicked`) — the
  transpiler keeps `Game1.showingHealthBar` permanently false so the
  *blood* droplets never spawn, but the sweat ones aren't gated on that
  flag and would otherwise float next to a bar that's no longer drawn.
- Forces `Game1.options.hardwareCursor = true` and calls
  `Game1.options.reApplySetOptions()` every tick, so the OS mouse cursor
  stays visible during gameplay. Confirmed against the decompiled
  `Options.hardwareCursor` setter that setting the flag alone does
  *nothing* visible — the actual `IsMouseVisible` toggle only happens
  inside `reApplySetOptions()` (the same method the game's own options
  menu calls after you check "Hardware Cursor"), which is why an earlier
  version of this fix (setting the option without calling that method)
  didn't actually make the cursor appear.
- Runs an `HttpListener` on port **8082** (must match
  `lib/services/game_connection_service.dart`'s default) with these routes:
  - `GET /ws` — WebSocket upgrade; pushes a fresh JSON state snapshot
    (same shape `/state` returns) to every connected client whenever it
    actually changes, instead of the app polling. This is what the app
    itself uses now.
  - `GET /state` — kept for compatibility/manual testing only; same JSON
    snapshot of player/farm stats, inventory, and equipped items as the
    `/ws` push. `{"connected": false}` when no save is loaded.
  - `POST /select` — body `{"index": N}`; makes inventory slot `N` the
    active/equipped item, same as pressing that number key in-game.
  - `POST /move` — body `{"from": N, "to": M}`; swaps whatever's in
    backpack slots `N` and `M` — the app's drag-and-drop between slots.
  - `POST /organize` — no body; runs the game's own organize-button
    logic (`ItemGrabMenu.organizeItemsInList`) on the player's backpack —
    the app's organize button.
  - `POST /open-journal` — no body; opens the real vanilla `QuestLog`
    menu in-game, the same menu class the journal key (or the in-game
    quest-log button) opens — the app's new Journal button (see
    `companion/lib/widgets/backpack_toolbar.dart`). Guarded the same way
    the real quest-log button's own click handler is (player able to
    move, no dialogue/event/farm-event in progress), and also skipped
    if some other menu is already open, so a remote tap mid-cutscene
    doesn't force one open.
  - `GET /sprite?id=<qualifiedItemId>` — PNG of that item's real in-game
    icon, cropped straight out of the player's own loaded game textures
    (`SpriteCache.cs`) — not anything the app bundles or downloads itself.
    404s until the item has appeared in at least one `/state` response
    (which is what warms the crop into the cache); every `qualifiedItemId`
    in `/state`'s inventory/equipment fields is guaranteed already cached
    by the time you see it there.
  - `GET /animal-sprite?type=<breed>` — PNG of that farm-animal breed's
    OR house pet's real portrait (e.g. `?type=White Chicken` or
    `?type=Cat`), cropped straight out of the animal's/pet's own loaded
    sprite texture (`AnimalIconCache.cs`) — same not-bundled-by-the-app
    approach as `/sprite`. Keyed by breed, not by individual
    animal/pet, since every animal/pet of the same type shares one
    texture; for a pet, `type` is `Pet.petType.Value` ("Cat"/"Dog"/a
    modded pet type) plus `Pet.whichBreed.Value` when it isn't the
    default "0" breed (e.g. "Cat", "Dog-1") rather than a `FarmAnimal.
    type` string — see `AnimalIconCache.GetPetCacheKey`'s doc comment
    for why (in short: `Pet.whichBreed` is a `NetString` in 1.6, not
    the `int` an earlier pass here assumed). 404s until that breed has
    appeared in at least one
    `/state` response (same cache-warming pattern as `/sprite`); every
    `type` in `/state`'s `animals` list is guaranteed already cached by
    the time you see it there.
  - `GET /portrait` — PNG of the player's actual composited farmer sprite
    (body/shirt/pants/hair/hat/accessories), rendered off-screen the same
    way the vanilla inventory menu draws its own portrait box
    (`PortraitRenderer.cs`). Re-rendered roughly twice a second so it
    picks up a wardrobe/haircut change without re-rendering every tick.
  - `GET /mini-portrait` — PNG of the real vanilla head+hair-only icon
    (no shirt/pants/hat/accessories) — the exact
    `FarmerRenderer.drawMiniPortrat` call the GameMenu's Skills tab and
    the MapPage's own player marker both use in real vanilla
    (`MiniPortraitRenderer.cs`). A deliberately different, much smaller
    render than `/portrait` — reuse `/portrait` for anything that wants
    the full standing body. Same refresh cadence as `/portrait`.
  - `GET /icon?name=backpack|map|crafting|organize|quality-silver|quality-gold|quality-iridium|skill-farming|skill-mining|skill-foraging|skill-fishing|skill-combat|pip-empty|pip-filled|pip-empty-wide|pip-filled-wide|heart-filled|heart-empty|hand-cursor|scroll-arrow-up|scroll-arrow-down|journal|journal-pulse|watering-can-gauge|vitals-energy-cap-top|vitals-energy-body|vitals-energy-cap-bottom|vitals-health-cap-top|vitals-health-body|vitals-health-cap-bottom|vitals-exhausted|vitals-droplet|petting-status-unpet|petting-status-pet|table-divider-h|table-divider-v|animals-tab`
    — PNG of one of the app's bottom-nav icons, the backpack screen's
    organize/journal buttons, an item-quality star badge, a Skills
    screen skill icon or level-pip segment, the journal button's
    "new activity" pulse badge, or the watering can's water-gauge
    frame, cropped from the game's own UI spritesheet (`UiIconCache.cs`)
    — same icons the vanilla game itself uses (`organize` is the exact
    icon `InventoryPage`'s own organizeButton uses; the three
    `quality-*` icons are the same silver/gold/iridium star crops
    `Object.drawInMenu` draws over a quality item's own icon;
    `skill-*`/`pip-*` are the exact icons/pip segments `SkillsPage.draw`
    uses; `journal`/`journal-pulse` are `DayTimeMoneyBox`'s own
    quest-log button icon and its pulse badge; `watering-can-gauge` is
    the exact background crop `WateringCan.drawInMenu` draws behind its
    water-level fill — the fill itself is a plain solid-color rect, not
    a sprite, so it rides the snapshot as `waterLeft`/`waterLeftMax`/
    `waterCanIsBottomless` instead, same pattern as `cooldownFraction`
    below). `hand-cursor` is the Animals table's "needs petting" icon
    (vanilla's own pick-up-item hand cursor, repurposed rather than a
    made-up icon) and `scroll-arrow-up`/`scroll-arrow-down` are the
    Animals table's tap-to-scroll rail — both cropped from
    `Game1.mouseCursors`, CORRECTED to the exact rects the real,
    decompiled `StardewValley.Menus.AnimalPage` itself uses for these
    same elements, from an earlier round's community-wiki-table
    guesses — see risk area 9 below.
    `petting-status-unpet`/`petting-status-pet` are the Animals table's
    per-row "already pet today" indicator — CORRECTED to a dedicated,
    purpose-built icon on a *different* sheet
    (`Game1.mouseCursors_1_6`, not `Game1.mouseCursors`) that
    `AnimalPage.drawNPCSlot` itself draws here, from an earlier round
    that used the real but wrong `OptionsCheckbox` checkbox sprite
    instead — see risk area 9 below. `table-divider-h`/
    `table-divider-v` are the table's internal grid-line graphics,
    cropped from a *different* sheet (`Game1.menuTexture`, not
    Cursors) at the real vanilla
    `IClickableMenu.drawHorizontalPartition`/`drawVerticalPartition`
    tile indices, CORRECTED to the exact indices (25/26) `AnimalPage`
    itself passes to those methods — see risk area 9 below.
    `animals-tab` is the Animals nav tab's own icon — CORRECTED
    twice now: a previous round briefly added a `social` icon
    (removed, borrows a vanilla icon that means something else), then
    replaced that with a standalone `/animal-tab-icon` route serving a
    raw "White Chicken" creature-sprite crop (also removed, on the
    mistaken assumption vanilla has no real `GameMenu` tab for
    Animals — true in 1.5.6, not in 1.6). `animals-tab` now folds into
    this same `/icon` route like every other UI icon, cropped from the
    real vanilla `GameMenu` "animals" tab icon 1.6 itself added — see
    risk area 9 below for the decompile citation.
  - `GET /state`'s (and `/ws`'s) inventory entries now also report
    `quality` (0=normal, 1=silver, 2=gold, 4=iridium — only ever
    non-zero for `StardewValley.Object` items, matching what vanilla
    itself puts a star on) and `cooldownFraction` (0-1, present only for
    a stabbing/defense sword currently recovering from a block — mirrors
    the red cooldown-wipe `MeleeWeapon.drawInMenu` draws over its own
    icon; see `GameStateSnapshot.DefenseCooldownWindowMs`'s doc comment).
    Neither rides its own route: `quality` is plain JSON on the existing
    inventory list, and so is `cooldownFraction` — the real vanilla
    effect is a flat color overlay, not a sprite, so there's nothing to
    crop for it. Inventory entries for a Watering Can also report
    `waterCanIsBottomless` (bool) alongside the existing `waterLeft`/
    `waterLeftMax` — it picks the water-gauge fill's color (BlueViolet
    full-opacity vs. DodgerBlue 70%-opacity), mirroring
    `WateringCan.drawInMenu`'s own color choice.
  - `GET /state`'s (and `/ws`'s) snapshot also reports `title` (Farmer.getTitle(),
    shown under the player's name on the Skills screen), `farmingLevel`/
    `miningLevel`/`foragingLevel`/`fishingLevel`/`combatLevel` (the five
    skill levels the Skills screen draws a pip row for — luck is
    deliberately not reported here, since vanilla hides that row until
    the Special Charm is found and the app's Skills screen doesn't draw
    it either), and `hasVisibleQuests`/`hasNewQuestActivity` (mirroring
    `Farmer.hasVisibleQuests`/`hasNewQuestActivity()` — the latter drives
    the Journal button's pulsing badge, same trigger as the real
    in-game quest-log button's own pulse). `/state`'s (and `/ws`'s)
    snapshot also reports `animals` — one entry per farm animal
    (`name`, `type` breed string, `friendship` 0-1000, `wasPet`) for the
    app's Animals screen. Scoped to friendship + petting status only —
    no produce-ready state — matching the real, currently-published
    `AnimalSocialMenu` mod's own scope (see `GameStateSnapshot.
    AnimalDto`'s doc comment for the full reasoning). Defaults to an
    empty list for backwards compat with older mod builds.
  - `GET /state`'s (and `/ws`'s) snapshot reports `exhausted`
    (`Farmer.exhausted` — over-tired; drives the app's "tired" face),
    `energyShake` (`Game1.staminaShakeTimer > 0`) and `healthShake`
    (`Game1.hitShakeTimer > 0`) alongside the existing `health`/
    `maxHealth`/`energy`/`maxEnergy` — the app redraws the health/energy
    bars (hidden in-game, see `HudBarPatches.cs`) next to its clock and
    uses these to reproduce the vanilla shake/pulse/droplet effects.
  - `GET /season-icon?n=<0-3>` and `GET /weather-icon?n=<code>` — PNGs of
    the real season/weather icons the vanilla clock HUD itself draws
    (`SeasonWeatherIconCache.cs`), keyed by `GameStateSnapshot`'s
    `SeasonNumber` and `WeatherIconCode` fields — the latter is the
    game's own `Game1.weatherIcon`, read directly rather than re-derived,
    since the real logic behind it (festival days, weddings, etc.) is
    more involved than the isRaining/isSnowing flags this mod already
    tracks for the plain-text `Weather` field.
  - `GET /portrait-background?night=true|false` — PNG of the actual
    background image the vanilla inventory menu draws behind the
    portrait (`Game1.daybg`/`Game1.nightbg`, cropped whole — not a
    spritesheet crop — by `PortraitBackgroundCache.cs`). The app picks
    `night` itself from the same hour the clock badge already shows
    (`hour24 >= 19`, matching the game's own `Game1.timeOfDay >= 1900`
    swap) rather than the mod deciding for it.
  - `GET /window-border` — PNG of the game's own 9-slice menu window
    border (`Game1.menuTexture` at `Rectangle(0, 256, 60, 60)`, via
    `WindowBorderCache.cs`) — a single 60x60 image the app stretches
    itself with `Image.centerSlice`, the same corners/edges/center-tile
    split `IClickableMenu.drawTextureBox` does on the game's own side.
  - `GET /clock-box` and `GET /clock-needle` — PNGs of the two sprites
    the vanilla clock/day box (`DayTimeMoneyBox`) draws itself
    (`ClockCache.cs`): the wood-and-parchment backdrop, and a single
    sundial-style needle that sweeps a half circle from 6am to ~2am.
    There's no 12-hour analog clock face anywhere in the real game —
    just this box, plain digital time text, and the one needle — so the
    app positions/rotates these the same way the real draw call does
    instead of drawing its own clock face.
  - `GET /slot-frame`, `GET /slot-locked-overlay`, and
    `GET /slot-selected-frame` — PNGs of the backpack grid's own slot
    background frame, the darkened overlay drawn over a slot beyond the
    player's current backpack capacity, and the highlighted frame for
    whichever slot is the player's currently selected/equipped item
    (`InventorySlotIconCache.cs`), all from `Game1.menuTexture` via
    `Game1.getSourceRectForStandardTileSheet` (tiles 10, 57, and 56) —
    the exact same tiles/helper the real game uses (tiles 10/57 in
    `InventoryMenu.draw`, tile 56 in `Toolbar.draw`'s own
    currently-selected-slot check), rather than a hand-copied pixel
    Rectangle. The app composites the locked overlay at ~50% opacity on
    top of the frame for a locked slot, matching the vanilla
    `tint * 0.5f` draw call; the selected frame replaces the normal
    frame outright for the selected slot, matching how the real hotbar
    swaps tile 56 in for tile 10 rather than layering it on top.
  - `GET /world-map` — PNG of the real vanilla world map background:
    the exact `Rectangle(0, 0, 300, 180)` region `StardewValley.Menus
    .MapPage.draw` itself draws from `Game1.content.Load<Texture2D>
    ("LooseSprites\\map")` (via `WorldMapCache.cs`). **Corrected after a
    real screenshot showed the bug**: an earlier version of this cache
    served the *entire* `LooseSprites\\map` texture, which turned out to
    be a much bigger spritesheet packing in every `Data/WorldMap` map
    area (alternate farm layouts, the quarry, Ginger Island, the volcano
    dungeon, etc.) below the base view — the app was rendering all of it
    stitched into one tall, garbled image. Cropping to the same
    `Rectangle` vanilla itself draws fixed it. Still not the per-farm-type
    overlay `MapPage` layers on top of that same region for the six farm
    layouts — those composite over this rect rather than living
    elsewhere on the sheet, so the base map alone still reads correctly
    without them. `/state` and `/ws`'s snapshots also report `locationName`
    (e.g. "Farm", "Town", "The Mines") and `mapMarkerX`/`mapMarkerY` — the
    player's position as a 0-1 fraction of this image's own width/height,
    computed via the real `StardewValley.WorldMaps.WorldMapManager` API
    (the actual 1.6 world-map-placement system, replacing the old
    hardcoded per-region math) so it lines up exactly like opening the
    real in-game map would. `mapMarkerX`/`Y` are `null` whenever the
    current location isn't mapped in `Data/WorldMap` (most mine/cave
    levels, a handful of interiors) — same as the real map page, which
    shows no marker there either.
- Snapshotting and applying selection requests both happen on the main
  game thread (`GameLoop.UpdateTicked`) — the HTTP listener runs on a
  background thread and only ever reads a cached snapshot / queues
  requests, since Stardew Valley's game state isn't thread-safe. Sprite
  cropping (`SpriteCache.EnsureCached`) also runs on the main thread for
  the same reason — it touches the graphics device — triggered from
  inside `GameStateSnapshot.Capture()`.

## Requirements

- [Stardew Valley](https://www.stardewvalley.net/) with [SMAPI](https://smapi.io/) installed
- Either [Docker Desktop](https://www.docker.com/products/docker-desktop/) (recommended — no local .NET install needed), or the [.NET 6 SDK](https://dotnet.microsoft.com/download/dotnet/6.0) directly

## Building & installing

### Option A — Docker (no local .NET install)

The [`Pathoschild.Stardew.ModBuildConfig`](https://www.nuget.org/packages/Pathoschild.Stardew.ModBuildConfig)
package normally auto-detects your game install by scanning known Windows/macOS
paths, which doesn't work inside a Linux container — so this passes the game
path explicitly via the `GamePath` MSBuild property instead. Run from this
folder, with `GAME` pointing at your actual Stardew Valley install (the
folder containing the game executable):

```bash
# This Steam Mac build's actual executable lives one level deeper than
# the top-level Steam folder, at .../Contents/MacOS/ — pointing GAME at
# the top-level folder fails with "doesn't contain the Stardew Valley
# file" (confirmed by actually trying it).
GAME="/Users/YOUR_USERNAME/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS"
docker run --rm -v "$PWD:/src" -v "$GAME:/game" -w /src mcr.microsoft.com/dotnet/sdk:6.0 dotnet build -p:GamePath=/game
```

The `/game` mount needs write access (not `:ro`) because `EnableModDeploy`
copies the built mod straight into `/game/Mods/StardewDS/` — since it's a
bind mount, that lands directly in your real `Mods` folder on the host, no
extra copy step. Nothing else under the game folder gets touched.

The container uses Docker's own network, not this sandbox's — NuGet
restore (`Pathoschild.Stardew.ModBuildConfig`, `Lib.Harmony`) works fine
there even though it's blocked in the environment this was written in.

### Option B — local .NET SDK

Build normally with `dotnet build` or your IDE — same auto-detection
caveat doesn't apply on your actual machine, `ModBuildConfig` should find
the game itself. If it can't, it'll fail the build with a clear error
asking you to set the game path explicitly (same `GamePath` property as
above, via a `~/.stardewvalley.targets` file — see the package's docs).

Watch the SMAPI console output on launch for a line like:

```
StardewDS loaded. Companion server starting on port 8082 — point the app at this PC's IP address on the same network.
```

You'll need this PC's local IP address (not `localhost`) for the phone app
to connect — e.g. from Terminal: `ipconfig getifaddr en0` (Wi-Fi) on a Mac.

## Known risk areas (check these first if `dotnet build` fails)

Written against the Stardew Valley 1.6 Farmer/Game1 API as documented in
the modding community, but never compiled here. In rough order of how
likely each is to have shifted:

1. `GameStateSnapshot.cs` — `player.leftRing`/`rightRing`/`hat`/`boots`
   (equipment) and `Game1.currentSeason` (vs. the newer `Game1.season`
   enum) are the fields most likely to need adjusting.
2. `SpriteCache.cs` — uses the SDV 1.6 `ItemRegistry.GetData`/`ParsedItemData`
   API (the documented modern replacement for indexing the old hardcoded
   spritesheets by hand); confirmed working against a real build (icons
   render correctly). `Game1.graphics.GraphicsDevice` was the one guess in
   here and it held up.
3. `PortraitRenderer.cs` / `UiIconCache.cs` — the newest, least-verified
   pieces. The farmer-portrait draw call in `PortraitRenderer.cs` and the
   Cursors-sheet rects in `UiIconCache.cs` were both copied from the
   actual decompiled game source (not guessed — see the doc comments in
   each file for exactly where), so the *values* should be right, but
   nothing here has been compiled or run yet. `RenderTarget2D`/`SpriteBatch`
   usage off the game's normal draw path is the part most likely to need
   an adjustment if `dotnet build` or the runtime output looks wrong
   (e.g. a garbled or blank portrait PNG rather than a compile error).
4. `HudPatches.cs` — the `Toolbar.draw(SpriteBatch)` and
   `DayTimeMoneyBox.draw(SpriteBatch)` signatures Harmony targets; if
   either method's been renamed/moved that one patch just won't apply
   (SMAPI logs a warning, doesn't crash) and that one widget (toolbar or
   clock box) reappears — there's no `displayHUD = false` fallback
   anymore.
4a. `HudBarPatches.cs` — the IL transpiler on `Game1.drawHUD` that snips
   out the health/energy bar blocks. It anchors on the literal `0.625f`
   stamina-bar `modifier` and the `Game1.onScreenMenus` field load that
   immediately follows the bars. If a future game version reshapes
   `drawHUD` past those anchors, the transpiler logs a warning and
   no-ops (the vanilla bars stay visible and get duplicated by the app,
   rather than the HUD crashing). Verified against the decompiled SV 1.6
   `Game1.drawHUD` (github.com/Dannode36/StardewValleyDecompiled). The
   `UiIconCache` `vitals-*` rects and the `Rectangle(366, 412, 5, 6)`
   droplet crop `ModEntry.OnUpdateTicked` filters on came from the same
   decompile.
5. `PortraitBackgroundCache.cs` / `WindowBorderCache.cs` / `ClockCache.cs`
   — like `PortraitRenderer.cs`/`UiIconCache.cs`, these were written
   against the real decompiled source (`InventoryPage.draw` for the
   background swap, `IClickableMenu.drawTextureBox`'s default call for
   the border rect, `DayTimeMoneyBox.draw` for the clock box/needle rects
   and the needle's rotation formula), not guessed — but none of the
   three has been compiled or run yet either.
6. Everything else (`Money`, `health`, `Stamina`/`MaxStamina`,
   `CurrentToolIndex`, `MaxItems`, `dayOfMonth`, `timeOfDay`, the
   `isRaining`/`isSnowing`/`isLightning`/`isDebrisWeather` flags) has been
   stable across versions and is used the same way in most published mods.
7. `GameStateSnapshot.cs`'s new `Quality`/`CooldownFraction` fields —
   `item is StardewValley.Object obj ? obj.Quality : 0` and
   `MeleeWeapon.stabbingSword`/`defenseSword`/`defenseCooldown` were all
   read from decompiled 1.6-era source, not guessed, but (like everything
   past the first round) unverified against a real build. If `dotnet
   build` complains here, `MeleeWeapon.defenseCooldown`'s accessibility
   (it's a `public static int` in the decompile checked) is the most
   likely thing to have changed — that field being `static` rather than
   per-weapon-instance is real vanilla behavior confirmed from source,
   not an assumption made here, so don't "fix" it into an instance field
   without re-checking against source first.
8. `WorldMapCache.cs` / `GameStateSnapshot.cs`'s new `LocationName`/
   `MapMarkerX`/`MapMarkerY` fields — `Game1.content.Load<Texture2D>
   ("LooseSprites\\map")` (the world map texture path) and
   `StardewValley.WorldMaps.WorldMapManager.GetPositionData(GameLocation,
   Point)` (the 1.6 world-map-placement API) were both confirmed against
   the official modding wiki's Data/WorldMap documentation before
   writing, not guessed, but the wiki's own paraphrased example turned
   out to elide two real API details a `dotnet build` caught: the tile
   argument is a `Point`, not the `Vector2` `player.Tile` itself is
   (CS1503), and `GetPositionData` actually returns a
   `MapAreaPositionWithContext?` wrapper, not `MapAreaPosition?` directly
   — unwrap it via `.Data` (CS0029; same real SV 1.6.14 API-shape bug
   filed as stardew-valley-dedicated-server/server#13 and fixed the same
   way in Annosz/UIInfoSuite2#635, found via web search once the compiler
   flagged it). Both build errors are fixed. A third, non-compile bug
   was then caught by a real screenshot: `WorldMapCache.cs` was serving
   the *entire* `LooseSprites\\map` texture rather than cropping the
   `Rectangle(0, 0, 300, 180)` region `MapPage.draw` actually draws for
   the base overworld — the full texture is a much bigger sheet packing
   in every `Data/WorldMap` map area, and it was all rendering stitched
   into one garbled image. Fixed by cropping to that exact `Rectangle`
   (the same one an earlier, pre-1.6 decompile of `MapPage.draw` had
   already told this project about — see `WorldMapCache.cs`'s doc
   comment). `GameLocation.GetDisplayName()` (used for `LocationName`)
   is the only piece of this round's work still not confirmed by a real
   build/screenshot.
9. `AnimalIconCache.cs` / `GameStateSnapshot.cs`'s `Animals` field —
   the newest, least-verified piece added this round. `Farm.
   getAllFarmAnimals()`, `FarmAnimal.type`/`friendshipTowardFarmer`/
   `wasPet`/`Name`, and `animal.Sprite.Texture`/`SpriteWidth`/
   `SpriteHeight` are all long-stable, widely-used FarmAnimal members
   (cross-checked against the real, currently-published
   `AnimalSocialMenu` mod's own source rather than guessed — see
   `GameStateSnapshot.AnimalDto`'s and `AnimalIconCache`'s doc
   comments), but — like everything in this file — not compiled
   against the real game here. The `heart-filled`/`heart-empty`
   `UiIconCache` rects are the same friendship-heart crop
   `AnimalSocialMenu` reads off `Game1.mouseCursors`, also
   cross-checked rather than independently re-verified against a
   decompile. Deliberately does *not* report `currentProduce`
   (produce-ready state) — its exact 1.6 field type/shape wasn't
   confidently determined here (see `GameStateSnapshot.AnimalDto`'s
   doc comment for the scope reasoning), so it's left out rather than
   guessed; a future round wanting it needs to confirm that field
   against a real build first.

   The `hand-cursor`/`scroll-arrow-up`/`scroll-arrow-down`
   `UiIconCache` rects (`(32,0,16,16)`, `(76,72,40,44)`,
   `(13,76,40,44)`) are lower-confidence than the heart rects above:
   sourced from a community-maintained wiki table of `Cursors.png`
   crops (stardewmodding.wiki.gg), not cross-checked against another
   published mod's own source or a decompile, and that same wiki page
   had at least one conflicting/unreliable entry for a nearby
   coordinate that was discarded rather than used. If these look wrong
   in-game (wrong crop, wrong icon entirely), that's the first place
   to check.

   House-pet (Cat/Dog) support — `GameStateSnapshot.CollectPets`,
   `AnimalIconCache.EnsureCachedForPet`/`GetPetCacheKey` — added to fix
   pets not appearing in the Animals list (a
   `StardewValley.Characters.Pet` is an `NPC`, not a `FarmAnimal`, so
   `Farm.getAllFarmAnimals()` never included it). This one has already
   been through one real correction: the first pass here was written
   against decompiled *1.5.6* source and shipped `int breed =
   pet.whichBreed.Value` plus a `pet is Cat` type-check — a real
   `dotnet build` against the actual installed 1.6 game failed with
   CS0029 (`Cannot implicitly convert type 'string' to 'int'`) and a
   CS0618 obsolete warning on `Cat`. Re-confirmed against real *1.6*
   decompiled source this time
   (Dannode36/StardewValleyDecompiled, `StardewValley.Characters/
   Pet.cs`) rather than the older 1.5.6 repo used elsewhere in this
   file: `Cat`/`Dog` are now `[Obsolete]` — every pet is just a `Pet`
   with a `petType` (`NetString`, "Cat"/"Dog"/a modded pet type ID) and
   a `whichBreed` (`NetString`, e.g. "0", not an int — content-pack
   breed IDs can be arbitrary strings). `friendshipTowardFarmer`
   (`NetInt`, `maxFriendship = 1000`) and `grantedFriendshipForPet`
   (`NetBool`, reset in `Pet.dayUpdate`) are unchanged from 1.5.6, so
   the friendship-scale/wasPet mapping this project already had was
   still correct. `GetPetCacheKey` keys the cache on `petType.Value`
   plus `whichBreed.Value` (e.g. "Cat", "Dog-1") — see that method's
   doc comment.

   This one went through a *second* real correction too, this time
   caught by an in-app screenshot rather than a build error: the fix
   above still cropped the portrait itself from `pet.Sprite`'s frame
   (0, 0), same as `AnimalIconCache.EnsureCached` does for a
   `FarmAnimal` — reasonable by analogy, but nothing actually confirmed
   frame 0 was a pet's idle/portrait pose, and the resulting crop
   looked wrong once tested against a real running game (not
   recognizable as the pet's breed). `AnimalIconCache.
   EnsureCachedForPet` now calls the real `Pet.GetPetIcon(out string
   assetName, out Rectangle sourceRect)` instead — its own doc comment
   in the 1.6 decompile says "Get the icon to show in the game menu for
   this pet", i.e. this *is* vanilla's own intended portrait crop,
   sourced from `Data/Pets` breed data and falling back to a known-good
   `"Animals\dog"` crop if that data is missing. `CropAndCache` was
   refactored to take an already-resolved `Texture2D`+`Rectangle`
   instead of an `AnimatedSprite`, since `FarmAnimal` and `Pet` no
   longer derive their crop the same way (FarmAnimal: sprite frame
   (0,0); Pet: `GetPetIcon`'s own rect).

   This one went through a *third* real correction, again caught by an
   in-app screenshot rather than a build error: the `GetPetIcon` fix
   above shipped, but the user's next screenshot still called it "wrong
   sprite". Re-reading `GetPetIcon`'s own doc comment explains why —
   "The 16x16 pixel area within the texture for the icon" is a small
   menu-list thumbnail (the kind of icon a pet-customization/naming
   list would use), not a portrait-scale crop, and was never a good
   match for the reference screenshot's full-body pose in the first
   place. What actually draws a pet on-screen in vanilla is `Pet.
   draw`'s own `b.Draw(Sprite.Texture, ..., Sprite.SourceRect, ...)`
   (confirmed by reading `Characters/Pet.cs`'s `draw` override
   directly) — so `AnimalIconCache.EnsureCachedForPet` now reads
   `pet.Sprite.Texture`/`pet.Sprite.SourceRect` directly instead,
   the pet's own *live* current animation frame — the same texture and
   rect vanilla's own renderer is using for that pet at that instant,
   which by definition can't be "the wrong sprite" the way a menu-icon
   crop or a blind frame-(0,0) guess could be. In practice this still
   didn't fix it (see the *fourth* correction right below) — the flaw
   wasn't the reasoning, it was that a live frame is non-deterministic
   and `AnimalIconCache`'s cache is write-once per key (see
   `CropAndCache`): whatever frame the pet happened to be mid-animation
   on the *first* tick it was seen got cached forever for that pet.

   This one went through a *fourth* real correction, this time
   prompted by the user pointing at the actual Stardew Valley Wiki
   modding docs (stardewvalleywiki.com/Modding:Pets, "Spritesheet
   Layout") instead of another decompile-only inference. Per that page:
   pet spritesheets are 128px wide with 32x32 frames (4 per row,
   matching `Pet`'s own constructor call, `new AnimatedSprite(
   getPetTextureName(), 0, 32, 32)`), and frames 0-3 are the "move
   down" cycle — frame 0 *is* the standing-still, facing-the-camera
   pose, the same "row 0 = idle facing down" convention every other
   character/animal spritesheet in this game follows (and the same
   assumption `EnsureCached(FarmAnimal, GraphicsDevice)` already relies
   on successfully). So `AnimalIconCache.EnsureCachedForPet` reverted to
   frame (0, 0) after all — attempt 1's *rectangle* was right, it just
   wasn't backed by anything better than a guess at the time, and the
   "orange flame"-looking result that first moved this method away
   from it was more likely a dimension/texture bug in that early draft
   than a genuinely wrong frame choice. This crop reads the sprite's
   own reported `SpriteWidth`/`SpriteHeight` rather than hardcoding 32,
   the same defensive pattern the `FarmAnimal` overload already uses,
   so it can't silently drift from whatever size the live
   `AnimatedSprite` actually reports. This is now deterministic (no
   more write-once-cache risk from an unlucky mid-animation frame),
   unlike the third correction.

   The table's internal grid divider lines (`table-divider-h`/
   `table-divider-v`) and the per-row petting status glyph (now
   `petting-status-unpet`/`petting-status-pet` — renamed this round,
   see below) were added in an earlier round in `UiIconCache.cs`,
   replacing flat colored placeholders. The divider lines used
   `UiIconCache.EnsureCached` calling the real
   `Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, index)`
   helper directly, with tile indices 6 (horizontal)/5 (vertical) —
   both the helper's signature/default tile size (64x64, not 16x16 —
   verified by reading `Game1.cs`'s own
   `getSourceRectForStandardTileSheet` body) and `Game1.menuTexture`'s
   asset name (`"Maps\MenuTiles"`, confirmed by reading `Game1.cs`'s
   own `LoadContent`) came from the real decompiled source. The
   checkbox-shaped status glyph's rects (`(227,425,9,9)`/
   `(236,425,9,9)`) were read directly off the decompiled
   `StardewValley.Menus.OptionsCheckbox` — the exact crop every real
   in-game options checkbox uses, on the assumption the Animals
   table's "already pet today" indicator was a reused generic
   checkbox. Both turned out to be wrong once tested — see the fifth
   correction below.

   `animals-tab` (the Animals nav tab's own icon) went through its own
   two corrections, both in `UiIconCache.cs` rather than here, but
   worth recording alongside the rest of this Animals-feature history:
   a first pass borrowed a `social` tab icon (removed — it means
   something else in real vanilla); a second pass gave up on finding a
   real vanilla tab for Animals at all and served a raw "White
   Chicken" creature-sprite crop instead (via a dedicated
   `/animal-tab-icon` route, now removed). Both were wrong for the
   same reason: vanilla 1.6 actually added a real `animals` tab to
   `GameMenu` (confirmed by reading the decompiled `GameMenu.cs`
   directly — its own `tabs.Add(...)` list includes one, string key
   `"1_6_Strings:GameMenu_Animals"`, and its `draw` method's per-tab
   icon switch has a case for it), it's just drawn from a second,
   newer sheet 1.6 introduced (`Game1.mouseCursors_1_6`, asset
   `"LooseSprites\Cursors_1_6"`) rather than the original Cursors
   sheet every other tab icon in this file reads — easy to miss if you
   only check the original sheet's own tab-icon row. `animals-tab`
   crops `Rectangle(257, 246, 16, 16)` off that second sheet, the
   exact rect `GameMenu.draw`'s `"animals"` case passes to its own
   `b.Draw(Game1.mouseCursors_1_6, ...)` call.

   This whole feature got a *fifth* real correction, and by far the
   most consequential one: finding that `GameMenu` really does have an
   `"animals"` tab (above) prompted browsing the rest of the decompiled
   `StardewValley.Menus` directory, which turned up
   `AnimalPage.cs` — vanilla 1.6's own complete "Animals" `GameMenu`
   page. It turns out to be almost exactly what this app's own Animals
   screen has been trying to reproduce from partial citations and
   inference across all four corrections above, and reading it
   directly settled every one of them at once with an authoritative,
   exact answer:
   - **Pet portrait** (`AnimalIconCache.EnsureCachedForPet`): the
     fourth correction's wiki-sourced `Rectangle(0, 0, SpriteWidth,
     SpriteHeight)` "frame 0 is idle" guess is superseded by
     `AnimalEntry`'s own real formula,
     `Rectangle(0, SourceRect.Height * 2 - 24, SourceRect.Width, 24)`
     — a fixed pixel-math crop that (per the wiki's own 32px-frame,
     4-frames-per-row layout) lands in the bottom 24px of the third
     row ("move up"), not row 0 ("move down"/idle) — consistent with
     the user's own follow-up observation that the render looked like
     a side-facing pose rather than a front-facing idle one.
   - **FarmAnimal portrait** (`AnimalIconCache.EnsureCached`): the
     original frame-(0,0) crop (cross-checked only against the
     `AnimalSocialMenu` community mod, never against vanilla's own
     Animals menu) is superseded by `AnimalEntry`'s own branching
     formula — tall breeds (cows/pigs/sheep/goats, `SourceRect.Height
     > 16`) crop `Rectangle(0, SourceRect.Height * 2 - 28,
     SourceRect.Width, 28)` (Ostrich specifically uses `* 2 - 32`);
     short breeds (chickens/ducks/rabbits) crop the fixed
     `Rectangle(0, 16, 16, 16)` instead.
   - **hand-cursor**: `AnimalPage.drawNPCSlot`'s own hand-cursor draw
     call uses `Rectangle(32, 0, 10, 10)`, not the earlier `(32, 0, 16,
     16)` community-wiki guess — the guess over-cropped 6px into the
     neighboring cursor frames on both axes. `drawNPCSlot` also draws
     this icon *unconditionally*, every row, at full opacity — there's
     no real vanilla dimming/fading based on pet status; only the
     status glyph below it (next bullet) changes.
   - **petting-status-unpet/petting-status-pet** (renamed from
     `petting-checkbox-unchecked`/`petting-checkbox-checked`): the
     `OptionsCheckbox` sprite above was an entirely wrong real sprite,
     not a wrong rect — `drawNPCSlot` reveals the real per-row
     indicator is a dedicated, purpose-built icon on the *newer*
     `Game1.mouseCursors_1_6` sheet (the same sheet `animals-tab`
     itself reads), at `Rectangle(273 + WasPetYet * 9, 253, 9, 9)` —
     a real 3-state enum (`WasPetYet`: 0 = not pet, 1 = auto-pet, 2 =
     hand-pet) this app's own `AnimalDto.wasPet` bool only
     distinguishes two states of. This fully explains repeated
     in-app feedback that the checkbox/"green cross" icon was wrong —
     it was a real, decompile-verified sprite, just the wrong one for
     this specific menu.
   - **scroll-arrow-up/scroll-arrow-down**: `AnimalPage`'s own
     constructor builds its up/down scroll buttons from
     `Rectangle(421, 459, 11, 12)`/`Rectangle(421, 472, 11, 12)`, not
     the earlier `(76, 72, 40, 44)`/`(13, 76, 40, 44)` pair sourced
     from an unrelated community wiki table.
   - **table-divider-h/table-divider-v**: `AnimalPage.draw()` calls
     `drawHorizontalPartition`/`drawVerticalPartition` with
     `small: true` — and the decompiled `IClickableMenu`'s `small`
     branch uses *different* tile indices (25 horizontal, 26
     vertical) than the non-`small` branch this file had used (6, 5).
   - **hearts** (`heart-filled`/`heart-empty`): the only element
     `AnimalPage.drawNPCSlot` confirmed as already correct, no change
     — it reads this exact same pair.

   None of this was guessed or inferred a fifth time — every rect and
   formula above was read directly out of `AnimalPage.cs`'s own draw
   and constructor code, the same real menu this app's Animals screen
   is modeled after, which is why this correction is treated as
   settling these specific elements rather than opening a sixth round
   of inference.

   Where a pet actually *is* at snapshot time is the one part still
   worth flagging: a Pet sits in a `GameLocation`'s own `characters`
   collection rather than any animal-specific list (confirmed via
   `Farm.cs`'s own
   `this.characters[index] is Pet` check, both in the 1.5.6 and 1.6
   decompiles), and can be either out on the farm or asleep in the
   farmhouse (`Pet.warpToFarmHouse` moves it into
   `Utility.getHomeOfFarmer(who)`'s `characters`) — both locations are
   checked, but a modded location that relocates a pet somewhere else
   entirely wouldn't be covered.

   A later round fixed how the companion app *renders* the
   `table-divider-h`/`table-divider-v` crops (app-side, `_HorizontalRule`/
   `_VerticalRule` in `animals_screen.dart`) rather than what's cropped:
   they were being stretched with `BoxFit.fill` from the full 64x64 crop
   down to a 6px-tall/wide box, which — sampled with nearest-neighbor
   filtering — squashed whatever beveled wood-grain detail the tile
   actually has into what read as a flat solid line, not the textured
   divider vanilla draws. Changed to tile the crop at its native pixel
   size instead (`Image.network`'s `scale: 4` — `Game1.pixelZoom` —
   maps the served 64x64 PNG back to a 16x16 logical tile, matching the
   16x16 native size every other icon in this cache already uses, then
   `ImageRepeat.repeatX`/`repeatY` tiles it across the divider's length,
   the same repeated-small-tile technique vanilla's own
   `drawHorizontalPartition`/`drawVerticalPartition` use rather than one
   stretched instance). `_AnimalTable`/`_AnimalRow` also now reserve a
   real 16px gap for each divider instead of overlaying it on the
   hearts/status columns' own zero-margin content, which a full-size
   tile would otherwise clip into.

   Unlike every other correction in this risk area, this one is **not**
   decompile-confirmed: several attempts this round to reach a real
   `IClickableMenu.cs` decompile (a few public decompile-mirror repos on
   GitHub, GitHub's own code search/contents API, jsdelivr's file
   listing, modding-forum threads) all failed — 404s on guessed file
   paths, 403s that looked like rate-limiting on the larger listings,
   robots.txt blocks on GitHub's code-search UI, and one large
   file-listing response that got truncated before reaching the `M`s
   alphabetically. So the 16px/4x figures are inferred from the
   64x64-tile-is-4x-native-16x16 pattern every other `UiIconCache` entry
   already follows, not read off `drawHorizontalPartition`/
   `drawVerticalPartition`'s own draw loop the way the crop rects
   (tile indices 25/26) were. If a future round gets real decompile
   access, this is the next thing to verify — the crop rects themselves
   aren't in question, just the exact size/loop vanilla renders them at.

`player.totalMoneyEarned` (a `uint` proxying the co-op team's shared
lifetime earnings) is now read into the snapshot's `TotalEarnings` field
as a `long`. The app still treats "Total Earnings" as optional (omits the
row when absent) in case this needs reverting on an older save format.

## Project layout

- `ModEntry.cs` — mod entry point: Harmony setup, HUD hiding, wires the
  companion server to the game loop
- `CompanionServer.cs` — the `HttpListener`-based local HTTP server
- `GameStateSnapshot.cs` — builds the JSON payload from live game state
- `SpriteCache.cs` — crops real item icons via `ItemRegistry`/`ParsedItemData`
- `PortraitRenderer.cs` — renders the composited farmer portrait off-screen
- `MiniPortraitRenderer.cs` — renders the real vanilla head+hair-only mini portrait off-screen
- `UiIconCache.cs` — crops the bottom-nav tab icons from the game's UI sheet
- `SeasonWeatherIconCache.cs` — crops the clock HUD's season/weather icons
- `PortraitBackgroundCache.cs` — crops the day/night portrait backdrop
- `WindowBorderCache.cs` — crops the 9-slice menu window-border texture
- `WorldMapCache.cs` — serves the real vanilla world map background texture
- `AnimalIconCache.cs` — crops real farm-animal AND house-pet (Cat/Dog) breed portraits, keyed by type
- `ClockCache.cs` — crops the clock/day box backdrop and its sundial needle
- `HudPatches.cs` — Harmony patch that skips drawing the toolbar
- `manifest.json` — SMAPI mod manifest
- `StardewDS.csproj` — project file (net6.0, references ModBuildConfig + Lib.Harmony)

## CI builds

`.github/workflows/release.yml` (in this repo) builds the mod zip
whenever a GitHub Release is published on `stardew-ds-mod`, then attaches
it to that release. (The parent `stardew-ds` repo's own `release.yml`
handles pinning the `mod`/`companion` submodules and republishing their
already-built artifacts under a `stardew-ds` release — it doesn't build
anything itself.)

The mod build uses `Pathoschild.Stardew.ModBuildConfig`, which compiles
against the actual game/SMAPI DLLs (`Stardew Valley.dll`,
`StardewValley.GameData.dll`, `MonoGame.Framework.dll`, `xTile.dll`,
`StardewModdingAPI.dll`, `smapi-internal/SMAPI.Toolkit.CoreInterfaces.dll`).
Those are commercial/copyrighted files, so GitHub's hosted runners don't
have them and they can't be committed or downloaded publicly from this
repo.

The fix: those six files live in a separate **private** repo,
[`yukipastelcat/stardew-ds-refs`](https://github.com/yukipastelcat/stardew-ds-refs),
checked out here as the `vendor` git submodule (`vendor/lib/`). CI loads a
**read-only SSH deploy key** (registered on `stardew-ds-refs`, stored here
as the `REFS_DEPLOY_KEY` secret) via the `webfactory/ssh-agent` action
*before* `actions/checkout`, so `actions/checkout`'s `submodules: recursive`
step can fetch `vendor` over SSH, then points `ModBuildConfig` at
`vendor/lib` with `/p:GamePath=...`. Being a private repo is what keeps the
DLLs from leaking, rather than any encoding trick — the deploy key only
grants read access to that one repo, nothing else.

`vendor/lib/` needs exactly these six files, laid out like this:

```
vendor/
└── lib/
    ├── MonoGame.Framework.dll
    ├── Stardew Valley.dll
    ├── StardewModdingAPI.dll
    ├── StardewValley.GameData.dll
    ├── xTile.dll
    └── smapi-internal/
        └── SMAPI.Toolkit.CoreInterfaces.dll
```

That mirrors the layout inside a normal Stardew Valley install —
`SMAPI.Toolkit.CoreInterfaces.dll` lives under the game's own
`smapi-internal/` subfolder — so a contributor seeding `vendor/lib` for
the first time can copy straight from their local install into the same
relative paths.

**Gotcha**: the workflow does *not* use `actions/checkout`'s own
`submodules: recursive` input for this. By design, `actions/checkout`
unconditionally rewrites `git@github.com:` submodule URLs to HTTPS
(authenticating with its own `GITHUB_TOKEN`) whenever *it* performs the
submodule fetch, unless you give it an `ssh-key` input — and that key
would then apply to checking out this repo too, which `REFS_DEPLOY_KEY`
isn't authorized for (it's a deploy key scoped read-only to
`stardew-ds-refs`). So the workflow checks out this repo normally, loads
`REFS_DEPLOY_KEY` via `webfactory/ssh-agent`, then runs a plain
`git submodule update --init --recursive` as its own step — an ordinary
git command isn't subject to `actions/checkout`'s URL rewriting, so it
honors the `git@github.com:` URL in `.gitmodules` and authenticates with
the loaded agent normally. (An earlier attempt used
`submodules: recursive` plus `persist-credentials: false` on the checkout
step, expecting that to stop the rewrite — it didn't; `persist-credentials`
only controls whether credentials are *left behind* for later steps, not
what `actions/checkout` itself does during its own submodule fetch.)

`mod/ci-tools/refstrip` is still available if you want to strip method
bodies out of the DLLs (replacing each with a 3-byte `ldnull; throw` stub)
before committing them to `stardew-ds-refs`. The mod build only needs the
DLLs' public type/method *signatures* to compile against — never the
actual game logic — so stripping is safe and shrinks what ends up
committed, even though it's no longer required to fit under a secret size
cap the way it was with the old approach.

**One-time / version-update setup**: copy the six files listed above from
your Stardew Valley install into `vendor/lib` (the `vendor` submodule is its
own clone of `stardew-ds-refs`), optionally running them through `refstrip`
first, then commit and push from inside `vendor` and bump the submodule
pointer here (`git add vendor && git commit`). No `gh` CLI or secret
management needed — just normal git pushes, since `REFS_DEPLOY_KEY` only
needs to be set once.

If the `mod` and `companion` repos are private, `actions/checkout` also
needs a token with read access to fetch them as submodules: set a repo
secret `SUBMODULES_TOKEN` to a PAT with `repo` scope. Public repos don't
need this — the workflow falls back to the default `GITHUB_TOKEN`.
