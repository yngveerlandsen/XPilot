# XPilot (remake)

A modern take on [XPilot](https://en.wikipedia.org/wiki/XPilot): inertia-based ship physics, gravity,
shields, fuel, and glowing vector graphics, against AI bots or other players over the network.
Modes: **Dogfight** (free-for-all, first to 10 kills), **Race** (checkpoints and laps) and
**Capture the ball** (Red vs Blue: tow the enemy ball into your own treasure, first to 3).

## Install

Download `XPilot-win-Setup.exe` from the [latest release](https://github.com/yngveerlandsen/XPilot/releases/latest)
and run it. It installs for your user only (no administrator prompt), adds Start menu and desktop shortcuts,
and keeps the game up to date: new releases download in the background and install when you quit. Uninstall it
from *Settings > Apps*. The releases also have a plain zip, for running without installing.

The installer isn't code signed yet, so Windows shows "Windows protected your PC" the first time: choose
*More info* and *Run anyway*.

## Run

Requires the .NET 10 SDK.

```
dotnet run --project src/XPilot.Desktop
dotnet test
```

> On this machine `C:\Program Files (x86)\dotnet` is first on PATH and has no SDKs. Use
> `"C:\Program Files\dotnet\dotnet.exe"` or move `C:\Program Files\dotnet` above it in PATH.

Developer quick start (skips the menu): `XPilot --map arena --bots 5 [--difficulty hard] [--spectate]`,
or `XPilot --connect host[:port] [--name Ace]` to join a server.

## Controls

| Action | Modern (default) | Classic (original XPilot) | Gamepad |
|---|---|---|---|
| Turn | Left/Right, A/D | A / S | Left stick, D-pad |
| Thrust | Up, W | Shift | A, Right trigger |
| Fire | Space, Ctrl | Enter | X, Right shoulder |
| Shield | Down, S, Shift | Space | B, Left trigger/shoulder |
| Grab / release ball | E, Right Shift | Ctrl | Y |

Tab shows scores, Esc pauses (in network games it opens a menu and the game keeps running), T chats in
network games, F11 toggles fullscreen. Change the preset or rebind any action under *Settings > Controls*.

## Settings

*Settings* in the main menu (also in the Esc menu during a game) has five pages:

- **Game:** name, skill (bots, and race checkpoint size), and match rules for games you play or host: kills or captures to win, time
  limit and race laps. Also whether other pilots' names show under their ships.
- **Video:** fullscreen, VSync, antialiasing, screen shake, particle amount and an FPS counter.
- **Audio:** separate sound effects and music volumes, the track playing (Enter skips to another), and
  muting when the window is in the background.
- **Controls:** the Modern or Classic preset, rebinding each action (press the new key), and resetting to
  the preset. Esc, P, Tab, T and F11 are kept for the game itself.
- **Network:** the port to host on, and a master server for the internet game list.

Everything is saved as you change it, to `%APPDATA%\XPilot\settings.json`.

## Rules worth knowing

- You can't fire while your shield is up, and the shield and thrust both burn fuel. Hover near an `F` station to refuel.
- Hitting a wall slowly bounces you off; hitting it fast destroys you (unless your shield is up).
- Dogfight: +1 per kill, -1 for crashing or shooting yourself.
- Race: pass checkpoints in order; the highest number is the finish line. Respawns put you at your last checkpoint.
  A checkpoint counts anywhere inside its circle, as long as no wall is in the way. The circle is the same size
  on every map and shrinks with the skill setting: 144 px radius on Easy, 104 on Normal, 72 on Hard.
- Capture the ball: fly near the enemy ball and press Grab to tow it on a rope (it drags you around), then pull it
  into your own treasure box. Kill a carrier to make them drop it. Touch your own dropped ball to send it home;
  otherwise it returns by itself after 20 seconds. Teammates cannot hurt each other. Capture = +3, kill = +1.

## Multiplayer

- **Host:** pick a mode and map in the main menu and choose *Host*. The game runs a server inside itself on
  UDP port 15345 and you join it. The server goes through every map of that mode in turn, starting with the
  one you picked, with 10 seconds of results between matches. Bots fill the free seats, up to the bot count
  you chose. When the map is full, a joining player takes a bot's place.
- **Join:** *Join network game* lists servers on your LAN, or type an address (`host` or `host:port`).
  People outside your network need UDP port 15345 forwarded to the host, or a master server (below).
- Set your name with *Name* in the main menu. In ball mode teams are balanced automatically. *Switch team*
  is in the Esc menu.
- If every seat already has a human in it, newcomers spectate.

### Dedicated server

```
dotnet run --project src/XPilot.Server -- --mode ball --bots 4 --name "My server"
dotnet run --project src/XPilot.Server -- --map arena,caverns --time-limit 300
dotnet run --project src/XPilot.Server -- --help
```

Releases include ready-built servers for Windows and Linux (`XPilot-Server-*.zip`). On Linux, run
`chmod +x XPilot.Server` once after unzipping.

### Internet server list

`XPilot.Server --run-master` runs a master server (UDP 15346), like the original XPilot meta-server. It
must run somewhere reachable from the internet. Game servers started with `--master host:port` register
with it. The game's default master server is `xpilot.hjemmelaga.online`; change or clear it under *Settings > Network*.
Games you host from the menu are only listed there if you turn on *List my hosted games*, since that shows
your address to everyone. Players using a master server see its servers in the join
list. Joining goes through NAT punch-through, so a host behind a typical home router usually doesn't need to
forward a port. The master only lists servers and introduces players; game traffic goes directly between
players and the server.

### How it works

The server is the authority: it runs the only real simulation at 60 Hz. Clients send their controls 60
times a second, repeating the last 8 in each packet so a lost packet costs nothing. The server sends
snapshots of every ship 30 times a second, and sends events (explosions, captures, bullets fired) reliably.
Other ships are drawn 100 ms in the past, blended between two snapshots. Your own ship is predicted from
your inputs, so it responds instantly. When a snapshot shows the server disagreed, the client replays the
inputs the server hasn't seen yet and smooths out the difference. Bullets fly in straight lines, so clients
simulate them from where they were fired rather than receiving their positions. The tests in
`tests/XPilot.Net.Tests` run full games over a simulated network with latency, jitter and packet loss.

## Maps

`maps/` holds this game's own maps (`.xpm`) plus:

- **Grand Tour** (`grand-tour.xp`): a huge race circuit, about three minutes a lap, with a chicane, a
  switchback climb, gravity wells on the top straight and a slalom home.
- **Classic XPilot maps** (`maps/classic/`): 22 maps from the original game, among them Blood's Music, The
  Globe, Tourmination, Teamball and Grand Prix II. They are loaded directly in XPilot's own `.xp` format; see
  [maps/classic/README.md](maps/classic/README.md) for authors, license (GPL-2.0) and what carries over.

Drop more `.xpm` or `.xp` files anywhere under `maps/` and they appear in the menu.

## Music

Any `.ogg` files in `music/` are played in random order, without repeating a track until all have played.
They're copied next to the game when it builds. To change the music, add or remove files there.

## Project layout

- `src/XPilot.Core`: the simulation, with no graphics dependency. It runs at a fixed 60 Hz, and all control goes through `ShipInput`. `Match` holds the bots and any number of humans.
  - `Maps/`: map format, loader and collision. `Simulation/`: the `World` and its entities. `Rules/`: dogfight, race and ball capture. `AI/`: navigation fields and `BotController`.
- `src/XPilot.Net`: multiplayer. `GameServer` and `GameClient` hold the game logic and don't depend on a transport. `ServerHost`, `ClientConnection`, `ServerBrowser` and `MasterServer` carry them over UDP with [LiteNetLib](https://github.com/RevenantX/LiteNetLib).
- `src/XPilot.Server`: the dedicated server and master server console app.
- `src/XPilot.Desktop`: the MonoGame (DesktopGL) client, covering rendering, HUD, input, synthesized sound and menus. There is no content pipeline: text uses a built-in stroke font and sounds are generated in code.
- `tests/XPilot.Core.Tests`: unit tests plus headless bot matches on every shipped map.
- `tests/XPilot.Net.Tests`: protocol tests, network games over a simulated network, and end-to-end UDP tests on loopback.
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
---
x # solid   q w a s  diagonal walls (solid half: top-left, top-right, bottom-left, bottom-right)
_ base      F fuel    + attractor   - repeller   1..9 checkpoints   . or space = empty
r b red/blue team base     R B red/blue treasure (ball mode)
```

## License

The code is under the [MIT License](LICENSE). Two parts of the repository are not:

- `maps/classic/`: the original XPilot maps, under the GNU GPL version 2. See [maps/classic/README.md](maps/classic/README.md).
- `music/`: four tracks used under the Pixabay Content License. See [music/README.md](music/README.md).
