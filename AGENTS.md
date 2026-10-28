# AGENTS.md — stardew-ds-mod

SMAPI mod (`UniqueID: yukipastelcat.StardewDS`) for Stardew Valley, target
`net6.0`, Harmony patching via `Lib.Harmony`, built with
`Pathoschild.Stardew.ModBuildConfig`. It hides parts of the vanilla HUD
(hotbar, clock/day/money box, health/energy bars) and exposes game state
and real game sprites to the Flutter companion app
(`yukipastelcat/stardew-ds-companion`) over a local server on port 8082.
It is a git submodule (`mod/`) of the umbrella `yukipastelcat/stardew-ds`
repo.

## Build

There are no tests. Verification is a successful build plus running the
game under SMAPI.

```bash
dotnet build            # needs the .NET 6 SDK and a game install
```

`ModBuildConfig` must find the game DLLs. Locally it auto-detects the
install; otherwise pass `-p:GamePath=<dir containing the game executable>`
(on macOS that is `.../Contents/MacOS`). Docker variant and details:
README → "Building & installing". `EnableModDeploy` copies the built mod
into `<game>/Mods/StardewDS/`, so a local build writes into the game dir.

CI (`.github/workflows/release.yml`) builds on GitHub Release publish
against reference DLLs from the private `vendor` submodule
(`stardew-ds-refs`, fetched with an SSH deploy key — **not** via
`actions/checkout`'s `submodules:` input; see the comments in the
workflow and README → "CI builds" before touching this). `vendor/` is
empty in a plain clone without access to that repo, so a build there will
fail to resolve game assemblies — that is an environment limitation, not
a code bug.

## Versioning

Bump **both** `manifest.json` `Version` and `StardewDS.csproj`
`<Version>` together (they must match). Releases are cut manually;
the workflow only builds and attaches the zip.

## Code map

All sources are flat in the repo root (no subfolders other than
`ci-tools/` and `vendor/`):

- `ModEntry.cs` — entry point: Harmony setup, per-tick HUD/toolbar state
  enforcement, input handling, wiring the server to the game loop.
- `CompanionServer.cs` — `HttpListener` server: `/ws` (WebSocket),
  `/state`, `POST /select|/move|/organize|/open-journal`, and the image
  `GET` endpoints.
- `GameStateSnapshot.cs` — builds the JSON state payload from live game
  data. Counterpart of the companion's `lib/models/game_state.dart`.
- `HudPatches.cs`, `HudBarPatches.cs` — Harmony patches (skip toolbar /
  clock draw; IL transpiler on `Game1.drawHUD` removing only the
  health/energy bar blocks). Prefer per-widget patches over
  `Game1.displayHUD = false`, which also hides things we keep.
- `InventoryNavigationPatches.cs` — disables vanilla toolbar-row rotation
  (`Farmer.shiftToolbar`); the mod owns `CurrentToolIndex`.
- `*Cache.cs`, `SpriteCache.cs`, `PortraitRenderer.cs`,
  `MiniPortraitRenderer.cs`, `PngEncoder.cs` — crop/render real game
  textures and encode PNGs for the image endpoints. Sprites are real
  game art, never re-drawn or approximated.
- `ci-tools/refstrip` — standalone tool to strip method bodies from game
  DLLs before committing to `stardew-ds-refs`. Excluded from the main
  compile via `<Compile Remove="ci-tools/**/*.cs" />`; keep it that way.

## Conventions and gotchas

- Match the existing style: nullable reference types enabled, heavy
  explanatory comments that record *why* (including what was verified
  against the decompiled game and which approaches failed on a real
  device). Preserve that rationale when editing; add a comment when you
  fix something non-obvious.
- Game/render work must happen on the game thread (SMAPI events /
  `Game1` draw loop); the HTTP server runs on its own threads. Follow how
  `CompanionServer` and the caches already hand off to the game thread
  instead of touching `Game1` state or GPU textures directly from a
  request handler.
- Any JSON field you add/rename/remove in `GameStateSnapshot.cs` needs
  the matching change in the companion; keep changes backward-compatible
  where practical (older app ↔ newer mod and vice versa).
- Target platform is an Android touchscreen handheld, gamepad-only;
  desktop keyboard/mouse behaviour is secondary (README lists known
  limitations).
- `README.md` is long and doubles as a design log ("Status" and "Known
  risk areas" record real-device findings). Read the relevant section
  before changing HUD hiding, toolbar/inventory navigation, or CI.
- The README notes the mod was written without a local `dotnet`
  toolchain at times; don't assume a previously committed state compiles
  — build when you can.
