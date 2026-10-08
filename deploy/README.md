# Running a public XPilot server

These files run the dedicated game server and the master server (the internet server list) as systemd
services on an Ubuntu machine. They're written to share a machine with other services: XPilot gets its own
`xpilot` user and `/opt/xpilot`, can't write anywhere, and is capped at half a CPU and 512 MB (the master at a
fifth of a CPU and 256 MB). It uses UDP 15345 (game) and UDP 15346 (server list), and nothing else.

## 1. Open the ports in the cloud firewall

`server-setup.sh` opens the ports in `ufw`, but a cloud firewall (Hetzner's, for example) sits in front of
that and has to be changed in the provider's console. Add two inbound rules:

| Port | Protocol | Source |
|---|---|---|
| 15345 | UDP | Any |
| 15346 | UDP | Any |

## 2. Set up the server once

From this repository, with the server's public name or IP:

```powershell
scp .\deploy\server-setup.sh .\deploy\xpilot-server.service .\deploy\xpilot-master.service root@example.com:/tmp/
ssh root@example.com "bash /tmp/server-setup.sh example.com"
```

This creates the user and folders, writes `/etc/xpilot/server.env` (kept if it already exists), opens the
ports in `ufw` and installs both services.

## 3. Deploy

```powershell
.\deploy\deploy.ps1 -Server example.com
```

This runs the tests, builds a self-contained Linux release, copies it over SSH, switches
`/opt/xpilot/current` to it and restarts both services. If the game server doesn't report that it is
listening, it switches back to the previous release. The five newest releases are kept.

## Changing the game

Settings are in `/etc/xpilot/server.env` on the server: the server name, mode (`dogfight`, `team`, `elimination`, `koth`, `race`, `ball`, or `random` for every map of every mode in shuffled order),
number of bots, bot skill, and any other options such as `--map arena,caverns` or `--time-limit 300`
(`XPilot.Server --help` lists them all). After editing:

```
ssh root@example.com "systemctl restart xpilot-server"
```

Logs: `ssh root@example.com "journalctl -u xpilot-server -f"`.

## Playing on it

- **Directly:** *Join network game*, then type the server's name as the address.
- **From the server list:** set *Settings > Network > Master server* to the server's name. The game server
  lists itself there, and other servers can too with `--master example.com`.
