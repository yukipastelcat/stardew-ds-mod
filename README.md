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
  - `GET /icon?name=backpack|map|crafting|organize|quality-silver|quality-gold|quality-iridium|skill-farming|skill-mining|skill-foraging|skill-fishing|skill-combat|pip-empty|pip-filled|pip-empty-wide|pip-filled-wide|journal|journal-pulse|watering-can-gauge|vitals-energy-cap-top|vitals-energy-body|vitals-energy-cap-bottom|vitals-health-cap-top|vitals-health-body|vitals-health-cap-bottom|vitals-exhausted|vitals-droplet`
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
    below).
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
    in-game quest-log button's own pulse).
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
