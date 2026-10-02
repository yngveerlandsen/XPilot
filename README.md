# XPilot (remake)

A modern single-player take on [XPilot](https://en.wikipedia.org/wiki/XPilot): inertia-based ship
physics, gravity, shields, fuel, and glowing vector graphics, against AI bots.
Modes: **Dogfight** (free-for-all, first to 10 kills), **Race** (checkpoints and laps) and
**Capture the ball** (Red vs Blue: tow the enemy ball into your own treasure, first to 3).

## Run

Requires the .NET 10 SDK.

```
dotnet run --project src/XPilot.Desktop
dotnet test
```

> On this machine `C:\Program Files (x86)\dotnet` is first on PATH and has no SDKs. Use
> `"C:\Program Files\dotnet\dotnet.exe"` or move `C:\Program Files\dotnet` above it in PATH.

Developer quick start (skips the menu): `XPilot --map arena --bots 5 [--difficulty hard] [--spectate]`.

## Controls

| Action | Modern (default) | Classic (original XPilot) | Gamepad |
|---|---|---|---|
| Turn | Left/Right, A/D | A / S | Left stick, D-pad |
| Thrust | Up, W | Shift | A, Right trigger |
| Fire | Space, Ctrl | Enter | X, Right shoulder |
| Shield | Down, S, Shift | Space | B, Left trigger/shoulder |
| Grab / release ball | E, Right Shift | Ctrl | Y |

Tab shows scores, Esc pauses, F11 toggles fullscreen. Switch the preset in the menu, or override single
actions in `%APPDATA%\XPilot\settings.json`, e.g. `"CustomBindings": { "Fire": ["J"] }`.

## Rules worth knowing

- You can't fire while your shield is up, and the shield and thrust both burn fuel. Hover near an `F` station to refuel.
- Hitting a wall slowly bounces you off; hitting it fast destroys you (unless your shield is up).
- Dogfight: +1 per kill, -1 for crashing or shooting yourself.
- Race: pass checkpoints in order; the highest number is the finish line. Respawns put you at your last checkpoint.
- Capture the ball: fly near the enemy ball and press Grab to tow it on a rope (it drags you around), then pull it
  into your own treasure box. Kill a carrier to make them drop it. Touch your own dropped ball to send it home;
  otherwise it returns by itself after 20 seconds. Teammates cannot hurt each other. Capture = +3, kill = +1.

## Project layout

- `src/XPilot.Core`: the simulation, with no graphics dependency. It runs at a fixed 60 Hz, and all control goes through `ShipInput`, so a network server can run it later unchanged.
  - `Maps/`: map format, loader and collision. `Simulation/`: the `World` and its entities. `Rules/`: dogfight, race and ball capture. `AI/`: navigation fields and `BotController`.
- `src/XPilot.Desktop`: the MonoGame (DesktopGL) client, covering rendering, HUD, input, synthesized sound and menus. There is no content pipeline: text uses a built-in stroke font and sounds are generated in code.
- `tests/XPilot.Core.Tests`: unit tests plus headless bot matches on every shipped map.
- `maps/`: text maps (`*.xpm`).

## Map format

```
name: My Map
mode: dogfight          # or race, ball
size: 40 24             # optional; otherwise taken from the grid
border: true            # surround with walls
wrap: false             # fly off one edge, appear on the other
gravity: 0 30           # constant pull (px/s^2)
attractor: 3000000      # strength of + / - gravity points
laps: 3                 # race only
checkpoint_radius: 96   # race only
---
x # solid   q w a s  diagonal walls (solid half: top-left, top-right, bottom-left, bottom-right)
_ base      F fuel    + attractor   - repeller   1..9 checkpoints   . or space = empty
r b red/blue team base     R B red/blue treasure (ball mode)
```
