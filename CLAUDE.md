# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## This file is the repo-root marker

`BoulderDash.Tests/TestPaths.cs` locates the repository root by walking up until it finds a `CLAUDE.md`. Do not move or delete this file, and do not add a `CLAUDE.md` in a subdirectory — asset-dependent tests resolve `BoulderDash.Game/Assets` relative to it.

## Commands

- Build: `dotnet build BoulderDash.slnx`
- Run all tests: `dotnet test BoulderDash.Tests`
- Run one test class: `dotnet test BoulderDash.Tests --filter "FullyQualifiedName~GoldenStateTests"`
- Run one test: `--filter "FullyQualifiedName~ClassName.MethodName"` (test method names are German, with underscores)
- Run the game: `dotnet run --project BoulderDash.Game`

Requires the .NET 10 SDK (`net10.0` targets; the `.slnx` solution format also needs a recent SDK).

## What this is

A C# port of Boulder Dash (MonoGame), ported from a DOS C++ original. Comments cite the original as `src/BOULDER.CPP:<line>` etc. — that C++ source is **not** in this repository; the citations are historical anchors, not paths you can open. Cave assets were generated from BD1 raw data (`Boulder-Dash-C64/extracted/caves`, also external).

**Language convention:** all comments, XML docs, and test names are German. Keep it that way.

## Architecture

Three projects: `BoulderDash.Core` (engine, no MonoGame dependency, fully headless), `BoulderDash.Game` (MonoGame DesktopGL frontend), `BoulderDash.Tests` (xUnit, tests Core only).

### BoulderDash.Core

- `Objects/` — the object model. Exactly one `CaveObject` instance per tile; **game rules live on the objects** (a boulder knows how it falls, a creature how it hugs walls), not in a central rule engine. The cave only provides the beat: `NextFrame()` per tick (animation), `Interact()` per cave scan (physics). Scan order (row-wise, top-down) is behavior-relevant; the `ScannedThisFrame` flag prevents an object from being moved twice in one scan. `CaveObject.ToRaw()` converts object state back to the original packed tile byte — required by the golden-hash tests and the proof that the object model is bit-exact to the original byte model.
- `Simulation/` — `Cave` is grid *and* world: it owns `GameState` (score, quota, remaining times, sound event queue), `InputState`, `Camera`, and the shared `Random`. `GameTick` is the port of the original timer ISR (clocks, camera scroll, countdowns, entrance build-up, screen cover). `ScreenCover`, `ExploreMap`, `Clocks`, `Palette`, `ViewportSize`/`ViewportSteps` live here too.
- `Flow/` — `GameSession` orchestrates everything as a state machine over `SessionPhase` (title screen → option screen → playing → level-end/death/game-over transitions → demo attract mode). The original's blocking `delay()/getch()` calls became phases with remaining time. `DemoPlayer` replays the recorded demo input.
- `Data/` — **all assets are human-readable text files**: caves (`CaveTextFile`, one file per cave/level in `BoulderDash.Game/Assets/Caves/cave-X-N.txt`), sprites (`SpriteTextFile`, one file per object), demo (`DemoTextFile`), settings (`SettingsFile`).
- `Audio/` — sound is fully synthesized (SID-style: `SidSynth`, `Envelope`, `SoundRecipes`, `ThemeTune`); there are no audio files. Core emits `SoundEvent`s via `GameState.SoundEvents`; the Game project renders them.

### BoulderDash.Game

No MonoGame content pipeline (no MGCB): `Assets/**` is copied to the output directory and parsed at runtime. Renders to a logical-resolution RenderTarget (menus fixed at 320×200 like the original's VGA mode 13h; in-game the viewport size), then integer-scales centered into the window. It only picks a renderer per `SessionPhase` — all game/menu flow is in Core's `GameSession`.

## Determinism and golden tests

- The simulation is deterministic: one fixed-seed `System.Random` stream shared by amoeba growth, boulder pushing, etc. **Order and count of random draws are behavior-relevant** — don't add, remove, or reorder draws casually.
- `GoldenStateTests` plays the demo headless and asserts a frozen FNV-1a hash over every grid state plus final score/steps; `GoldenCaveScanTests` covers the objects the demo doesn't reach. If one fails after an *intentional* physics/timing/RNG change, re-freeze: the test output shows the actually computed hash — update the frozen constants.

## Faithfulness conventions

The port follows the DOS original's behavior by default (comments frequently justify code with original line references, including preserved quirks and dead code notes). Deliberate deviations toward the original C64 BD1 behavior are called **"BD1-Ausnahme"** in comments and documented at their site — e.g. ScreenCover, amoeba timing, `CaveSpeed`, and the BD1 title/option screen replacing the DOS menu (`GameSession`, which also intentionally offers all 16 caves A–P in the menu instead of BD1's A/E/I/M). When touching such code, keep the documented rationale intact.
