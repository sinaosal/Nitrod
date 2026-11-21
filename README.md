# Nitrod

Subnautica multiplayer built for modded games. Nitrod is a fork of [Nitrox](https://github.com/SubnauticaNitrox/Nitrox), the open-source multiplayer mod for [Subnautica](https://unknownworlds.com/subnautica/), with a focus on playing together with BepInEx mods.

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![GitHub release (latest by date)](https://img.shields.io/github/v/release/sinaosal/Nitrod)](https://github.com/sinaosal/Nitrod/releases)

## What Nitrod adds

- **Mod sync.** When you join a server, Nitrod compares your BepInEx mods with the host's. If something is missing or different, you can download the host's mods directly from the host, after confirming. Every file is checked, installed the next time you start the game, and can be undone.
- **Compatibility checks.** Missing mods, different versions, missing dependencies, incompatible plugins and game/BepInEx version differences are explained before you enter the world.
- **A launcher for modded play.** Mods page (installed mods, problems, enable/disable, install from ZIP, undo synced mods), Diagnostics page (logs, mod loading errors, compatibility reports, one-click copy for support), status at a glance on Home, and saved servers by IP.
- **Better mod support in multiplayer.** Modded vehicles (e.g. VehicleFramework subs) are replicated, custom items from mods are sent between players correctly, and mods can sync their own state through [`NitrodModApi`](docs/NITROD-MOD-API.md).
- **Live status for hosts.** The server console shows who is checking mods and how fast downloads are going.

Everything Nitrox syncs is still there: players, items, building, bases, vehicles, the Cyclops, story progress, chat and more.

## Getting started

1. Download the latest ZIP from [Releases](https://github.com/sinaosal/Nitrod/releases).
2. Extract it into a folder and run `Nitrod.Launcher.exe`.
3. If Subnautica isn't found automatically, set its folder under **Settings**.
4. Press **Play**. Nitrod offers to install [BepInEx](https://github.com/toebeann/BepInEx.Subnautica) if you want to use mods.

You need a copy of Subnautica (Steam, Epic Games or Microsoft Store) and Windows. All players must use the same Nitrod version line (2.0.x players can't join 1.x servers).

### Hosting with mods

1. Install your mods in Subnautica's `BepInEx/plugins` folder (or with **Mods → Install from ZIP**).
2. Create and start a server under **Multiplayer**. The server console lists the mods it shares.
3. Optional: in the server settings, mark client-side mods as optional (`ModSyncOptionalMods`), keep host-only tools private (`ModSyncExcludedMods`), or turn sharing off.

### Joining

1. Add the server under **Multiplayer → Join a server**, or in-game under **Multiplayer**.
2. Press **Play**, open **Multiplayer** and pick the server.
3. If your mods differ from the host's, Nitrod shows what's missing and the download size. Accept, wait for the download, quit the game, press **Play** in the launcher again, and join.

## Is mod sync safe?

Mods are code, and they come from the host's computer, so only accept downloads from hosts you trust. Nitrod's safeguards:

- Nothing downloads or installs without your confirmation.
- Files can only go into `BepInEx/plugins` and `BepInEx/patchers`, and every file is checked with SHA-256 before it's installed.
- Programs and scripts are never downloaded, and native code needs a separate approval.
- Mods are installed only while the game is closed. Replaced files are backed up, and **Undo** under **Mods** restores them.

Details: [docs/NITROD.md](docs/NITROD.md#mod-synchronization).

## Which mods work?

Most mods load fine. Whether a mod works *together* in multiplayer depends on what it does:

- **Items, equipment, recipes, buildables:** usually work when everyone has the same mods, which mod sync takes care of.
- **Modded vehicles:** movement is synced; special abilities of a modded vehicle may not be.
- **Mods with their own story, events, creatures or spawns** (e.g. The Red Plague, The Prototype): they load, but their custom events run separately for each player.

The full notes are in [docs/NITROD.md](docs/NITROD.md#mod-compatibility). Mod authors can make their mods multiplayer-aware with the [Nitrod mod API](docs/NITROD-MOD-API.md).

## Problems and bugs

Open an issue on [GitHub](https://github.com/sinaosal/Nitrod/issues). In the launcher, **Diagnostics → Copy diagnostics** collects your setup, mod errors and the last compatibility check; paste it into the issue (it contains folder paths, so check it first).

## Building from source

Requires the .NET 10 SDK and an installed copy of Subnautica (the build references the game's files).

```powershell
dotnet build Nitrox.slnx -c Release
dotnet test Nitrox.Test/Nitrox.Test.csproj
```

The launcher is built to `Nitrox.Launcher/bin/Release/net10.0/Nitrod.Launcher.exe`. More in [docs/NITROD.md](docs/NITROD.md#building).

## Credits and license

Nitrod is built on [Nitrox](https://github.com/SubnauticaNitrox/Nitrox) by Sunrunner and the Nitrox contributors. All credit for the multiplayer foundation goes to them; please don't report Nitrod issues to the Nitrox team.

Nitrod fork: Sinaosal (code), Prosims (logo).

Licensed under the [GNU GPL v3](LICENSE.txt), like Nitrox. Subnautica is a trademark of Unknown Worlds Entertainment; Nitrod is not affiliated with or endorsed by them.
