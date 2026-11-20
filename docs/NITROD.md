# Nitrod: modded multiplayer guide

Nitrod is a fork of [Nitrox](https://github.com/SubnauticaNitrox/Nitrox) (base commit `7815b4e4`, Nitrox 1.9.0.0) focused on playing Subnautica multiplayer with BepInEx mods. This document covers what Nitrod adds, how mod synchronization works and why it is safe, what works with which mods, and how to build and test it.

- [Mod synchronization](#mod-synchronization)
- [Mod compatibility](#mod-compatibility)
- [Launcher](#launcher)
- [Building](#building)
- [Testing](#testing)
- [Known limitations](#known-limitations)
- [Development checklist](#development-checklist)

## Mod synchronization

When a player joins a server, Nitrod compares their BepInEx mods with the host's **before they enter the world** and, with their consent, downloads the host's mod files directly from the host. There is no central mod hosting.

### Flow

1. The server starts and scans `<game>/BepInEx/plugins` and `BepInEx/patchers` of the game folder it runs against (`ModSyncService`). Each top-level folder or loose file is a *package*. For each file it records the path, size, SHA-256 and kind (managed DLL, native library, program/script, data). Plugin GUIDs, versions, dependencies and incompatibilities are read from assembly metadata **without loading the assemblies**.
2. A player connects and receives the session policy as before. Before the name/password screen, the client asks for the host's manifest (`ModManifestRequest`, allowed before joining).
3. The client scans its own mods (plugin info comes from BepInEx's chainloader, i.e. what actually loaded) and compares (`ModCompatibilityChecker`). The result is written to `nitrod-compatibility-report.txt` in the log folder.
4. Everything matches: the join continues as before. Something differs: a Subnautica-style panel lists the differences and offers **Download and install (size)**, **Join without them** (only if the host allows it) or **Cancel**.
5. Download: files are requested by content hash in 64 KB chunks with up to 16 requests in flight on dedicated network channels. Each file is verified with SHA-256. Interrupted downloads resume from the partial file. Files that already exist locally with the same hash are copied instead of downloaded.
6. Downloaded files are *staged* in `BepInEx/NitrodSync/staging` and recorded in `pending.json`. Nothing in `plugins` changes while the game runs.
7. The player quits and presses **Play** in the launcher. The launcher shows what will be installed, re-verifies every staged file, backs up the files it replaces or removes, installs, and records everything in `ledger.json`. Any failure rolls back.
8. The player joins again with matching mods.

### Security model

A host must not be able to run code on a player's machine without them knowing. The design assumes the host is hostile:

| Threat | Mitigation |
|---|---|
| Silent install | Nothing is downloaded without the player confirming in-game, and nothing is installed without confirming again in the launcher. The server's name and address are shown both times. |
| Path traversal / overwriting arbitrary files | Every path from the host is validated (`ModPathSafety`): only `plugins/` and `patchers/`, no `..`, absolute paths, drive letters, `:` (alternate data streams), reserved device names, trailing dots/spaces, control characters, or Nitrod's own folder. Resolved paths must stay inside `BepInEx`, and the install refuses to write through symbolic links or junctions. |
| Reading files from the host | Clients request files by SHA-256, never by path. The server only serves files listed in its own manifest. |
| Tampered or corrupt data | SHA-256 verification after download, and again right before install. |
| Native code | DLLs without a .NET header (and `.so`/`.dylib`) need a separate approval screen that lists them. |
| Executables and scripts | `.exe`, `.bat`, `.ps1`, `.sh`, ... are never downloaded and don't count as differences. Real example: VehicleFramework ships `Fix-VFLocalizationKeys.cmd/.ps1`. Nitrod never executes downloaded files. |
| Huge downloads | Per-file (1 GB), total (4 GB) and file-count limits (`ModSyncLimits`). |
| Overwriting files in use | Installs only run from the launcher with the game closed. A locked file makes the whole install roll back. |
| Upload abuse against hosts | Per-player upload rate limit (default 20 MB/s, `ModSyncMaxUploadMegabytesPerSecond`), chunk size limit, and requests that would queue for more than 10 s are refused. |
| Elevated rights | The launcher runs as a normal user again (`asInvoker`), so the game and downloaded mods don't run as administrator. |

Downloads come from the host's own machine; Nitrod can't tell whether a mod is malicious. The consent screens say so, and **Undo** in the launcher restores the previous state.

### Host options (`server.cfg`, also editable in the launcher)

| Option | Default | Meaning |
|---|---|---|
| `ModSyncEnabled` | `true` | Share the mod list with joining players. When off, players join without a mod check. |
| `ModSyncAllowDownloads` | `true` | Let players download mod files from this server. |
| `ModSyncRequireMatchingMods` | `true` | Hide "Join without them" so players need the same required mods. This is enforced by the client, not the server. |
| `ModSyncOptionalMods` | empty | Packages players may skip (folder names or plugin GUIDs, comma separated), e.g. client-side UI mods. |
| `ModSyncExcludedMods` | empty | Packages never shared or compared (host-only tools). |
| `ModSyncMaxUploadMegabytesPerSecond` | `20` | Upload limit per player; `0` = unlimited. |

Always excluded from manifests: archives (`*.zip`, `*.7z`, `*.rar`), `*.log`, `*.tmp`, `*.bak`, per-user `config.json` files, and folders starting with `.` (the BepInEx pack's own `plugins/.tobey.bepinex.pack`). Plugins in excluded folders still count when checking dependencies.

### Files

| Path | Content |
|---|---|
| `<game>/BepInEx/NitrodSync/staging/` | Verified downloads named by hash (`.blob`), partial downloads (`.part`) |
| `<game>/BepInEx/NitrodSync/pending.json` | Downloaded install waiting for the next launch |
| `<game>/BepInEx/NitrodSync/ledger.json` | Every applied sync: files installed, files replaced/removed and where their backups are |
| `<game>/BepInEx/NitrodSync/backups/<id>/` | Backups used by **Undo** |
| `<game>/BepInEx/NitrodDisabled/` | Mods disabled from the launcher (BepInEx doesn't scan it) |
| `%AppData%/Nitrox/logs/nitrod-compatibility-report.txt` | Last server comparison |
| `%AppData%/Nitrox/logs/nitrod-harmony-overlaps.txt` | Methods patched by both Nitrod and other mods |
| `%AppData%/Nitrox/known-incompatibilities.json` | Optional list of plugins with known multiplayer problems (format in `KnownIncompatibilityList`) |

## Mod compatibility

BepInEx mods load in Nitrod as in singleplayer. Whether they *work in multiplayer* depends on what they do. Nitrox synchronizes vanilla game systems (entities, inventories, building, vehicles, story goals, ...) through its own patches; a mod's custom logic runs separately on every player's game.

### What Nitrod now handles for mods

- **Same mods everywhere.** Mod sync makes every player run the host's exact mod files. Most modded desyncs come from different mod versions.
- **Custom TechTypes** (items, equipment, recipes, creatures added through Nautilus). TechTypes are now sent by name from the game's TechType string table (`TechTypeExtensions.AsString`/`FromString`, where Nautilus registers custom types) instead of `Enum.ToString`/`Enum.Parse`. Nautilus assigns numeric TechType ids per installation, so a raw number would map to a different item on another machine. Unknown TechTypes now fail with a message naming the missing mod.
- **Custom prefabs.** Vehicles keep their prefab `ClassId` and are spawned through `PrefabDatabase` (which Nautilus extends) before falling back to the TechType.
- **Modded vehicles** (VehicleFramework subs such as R5Submersible). They get a generic movement replicator (upstream tried to add an abstract component, which fails). Remote players in modded vehicles no longer crash on vehicles without an animator or seat transform. Piloting a modded vehicle acquires the simulation lock even when the mod bypasses the vanilla entry path.
- **Diagnostics.** The client logs every game method that Nitrod and another mod both patch, flagging prefixes that can skip the original and transpilers. Prefixes and transpilers are the most common reason a mod breaks Nitrox sync.
- **An API for mods that need their own sync** (`NitrodModApi`): size- and rate-limited messages relayed by the server, delivered on the main thread. See [NITROD-MOD-API.md](NITROD-MOD-API.md).

### What doesn't sync without mod-specific work

- **Mod-added world spawns** (Nautilus coordinated spawns, loot distributions). The Nitrox server builds the world from the vanilla game files. Mods that spawn objects at runtime do so on each player's game, without a Nitrox id, so the objects are not shared.
- **Mod state** kept in Nautilus `SaveDataCache` or custom save files. It is saved per player, not with the server world.
- **Mod logic** driven by local timers, randomness, or custom AI that Nitrox doesn't simulate (mods can use `NitrodModApi` to sync it).
- **Mods that rewrite systems Nitrox patches** (story goals, saving, `LargeWorldStreamer`, `Player` respawn). Check `nitrod-harmony-overlaps.txt`.

### The Red Plague and The Prototype

Neither mod could be run in a real multiplayer session for this release (they aren't installed on the development machine and in-game testing wasn't possible). This assessment comes from their public source code ([The Red Plague mirror](https://github.com/ThePlagueSpreads/TheRedPlague-PublicMirror), [Nexus #1820](https://www.nexusmods.com/subnautica/mods/1820); [The Prototype](https://github.com/Indigocoder1/PrototypeSub), [Nexus #2461](https://www.nexusmods.com/subnautica/mods/2461)) and from what Nitrod synchronizes.

| | The Red Plague (`com.lee23.theredplague`) | The Prototype Expansion (`com.prototech.prototypesub`) |
|---|---|---|
| Dependencies | Nautilus, ECCLibrary, TerrainPatcher, Kallie's Prop Pack | Nautilus, SubLibrary, TerrainPatcher, SuitLib |
| Mod sync | Supported: every player gets identical files, and dependencies are checked | Supported |
| Custom items / recipes | Expected to work (TechTypes by name) | Expected to work |
| Terrain / biomes | Terrain is the same if files match; biome effects are client side | Same |
| Custom creatures | Spawned locally by the mod; not shared between players | Wyrm AI runs per player |
| Custom submarine | n/a | SubLibrary sub: transform replication is the vehicle path's job; its abilities, power and modules are **not** synced |
| Story / progression | Replaces the main story with ~58 compound goals and custom events. Vanilla story goals sync, but its custom events and timers run per player | ~77 custom story events; per player |
| Global events | Plague infection, insanity effects, the heart-destruction countdown: per player and save-file based | Sub destruction event: per player |
| Save data | ~12 Nautilus `SaveDataCache` classes, per player | Global + per-sub save data, per player |
| Conflicts with Nitrox patches | Patches `IngameMenu.GetAllowSaving`, story goal handlers, `Player`, `Creature`, `LiveMixin` | Patches `StoryGoal.Execute`, `LargeWorldStreamer.Start`, respawn, `PrefabPlaceholder.Spawn` |

**Expected result:** both load and are playable with matching files, but the shared world diverges for anything driven by the mods' own story and event systems. Making them fully cooperative needs mod-specific compatibility modules that use `NitrodModApi` to sync their events and save data, ideally written with the mods' authors. Both repositories are MIT licensed; check each mod's Nexus permissions before redistributing outside a play group.

### Mods tested in this pass

These are the mods on the development machine. Mod sync was tested end to end against a real Nitrod server, over the network, with their real files. In-game multiplayer was **not** tested.

| Mod | Version | Manifest / dependencies | Download + install + undo |
|---|---|---|---|
| Nautilus | 1.0.0.54 | ✔ | ✔ |
| VehicleFramework | 2.0.7.301 | ✔ (2 helper scripts skipped as designed) | ✔ |
| R5Submersible | 2.0.1 | ✔ (depends on VehicleFramework and Nautilus ≥ 1.0.0.46) | ✔ |
| SubLibrary | 1.7.11 | ✔ | ✔ |
| SuitLib | 1.1.8 | ✔ | ✔ |
| Tobey's BepInEx pack helpers | — | Recognized as loader components, not synced | — |

## Launcher

- **Home**: Play buttons plus a status row (game installation and build, BepInEx, mods and problems, hosting). Each tile opens the page where the item can be fixed.
- **Multiplayer**: hosting as before, plus **Join a server**, which saves servers by IP/host name into the same list the in-game Multiplayer menu shows.
- **Mods**: installed mods with versions, plugin GUIDs, dependencies and health. Also: enable/disable, install from ZIP, install BepInEx, install or discard downloaded mods, the list of mods synchronized from servers with **Undo**, and how sync works.
- **Diagnostics**: environment, mod problems, BepInEx loading errors from the last session, the last server comparison, patch overlaps, recent logs, and **Copy diagnostics** (which includes local paths, so review it before sharing).
- **Settings**: renamed from Options.

## Building

Requirements: .NET SDK 10.0.4xx, Windows (Linux/macOS work for the launcher and server), and Subnautica installed. The build references the game's assemblies; `Nitrox.Discovery.MSBuild` finds the installation automatically.

```powershell
dotnet build Nitrox.slnx -c Debug          # or -c Release
dotnet test Nitrox.Test/Nitrox.Test.csproj
```

Output: `Nitrox.Launcher/bin/<Configuration>/net10.0/Nitrod.Launcher.exe`. Debug and Release builds can't play together, because Release adds a CRC layer to the network protocol.

The build treats NuGet vulnerability warnings as errors. `MessagePack` is pinned to 3.1.12 (fixes GHSA-qhrr-8q5h-9q3h). `SixLabors.ImageSharp` stays on 2.1.x with five advisories suppressed in `Directory.Packages.props`: the server only uses `Image.LoadPixelData` + `Flip` on a raw texture from the game's own files, which none of those advisories affect. Re-evaluate if ImageSharp is ever used to decode untrusted images.

## Testing

### Automated (no game needed)

`dotnet test` runs 368 tests (355 pass, 13 are skipped by OS or pre-existing skips). Mod sync coverage:

- `ModPathSafetyTest`: traversal, absolute paths, alternate data streams, reserved names, junction escapes (Windows), managed/native/script classification.
- `ModScannerTest`: package grouping, plugin metadata from assembly attributes, exclusions, optional/excluded packages, hash cache invalidation.
- `ModCompatibilityCheckerTest`: missing/different versions/different files, relocated folders, extra mods, missing and too-old dependencies, declared incompatibilities, optional mods, game/BepInEx differences, native code and scripts, limits.
- `ModDownloaderTest`: chunking, out-of-order responses, resume, corrupt chunk retry, permanent corruption, host file changed, host refusal, local dedupe, cancellation.
- `ModSyncStoreTest`: end to end host → client sync, backups and undo, tampered staging, hand-edited `pending.json` with traversal, executable approval, rollback when a file is locked (Windows), discard.
- `PacketsSerializableTest` round-trips every new packet.
- `NitrodModApiTest` exercises the documented reflection bridge.

### Mod sync over the network (no game needed)

Done for this release against the real server and the mods listed above; to repeat:

1. Start a server on a test port without touching your router or saves:
   `Nitrox.Server.Subnautica.exe --save SyncTest --data-path <empty folder> --GameServer:ServerPort 11555 --GameServer:PortForward false --GameServer:LanDiscovery false`
2. The log shows `Mod sync: sharing N mod package(s)` with each mod's version.
3. Connect a client and run the flow. The scratch harness used for this release connected over LiteNetLib, fetched the manifest (24 ms), checked that hostile requests were refused, and downloaded 27.4 MB at about 3 MB/s on localhost. It then installed, rescanned (everything `UP_TO_DATE`) and undid the install.

### In game (manual)

1. **Host:** install mods, start a server from the launcher, check the server log for the mod list.
2. **Player with no mods:** add the host in Multiplayer › Join a server (or in-game), press Play, open Multiplayer and join. Expect the "Mods required" panel listing the host's mods and the download size.
3. Download: watch the progress (current mod, overall progress, speed, size, mods remaining, verification). Expect "Restart required".
4. Quit, press Play in the launcher, confirm the install dialog, join again. Expect no mod panel.
5. **Undo** in Mods restores the previous files.
6. Variations: cancel mid-download and resume; disconnect the host mid-download (retry); a player with an older mod version (only changed files download, old ones are backed up); a player-only UI mod (warning, can still join); `ModSyncRequireMatchingMods=false` (Join without them appears); `ModSyncAllowDownloads=false` (manual install message).
7. After a session, check Diagnostics › Patch overlaps for the mods in use.

## Known limitations

- **Not verified in game**: the in-game panel (layout built from Subnautica's menu elements, not visually checked), the join hook, and Unity's handling of the client's async code. The launcher pages were checked with offscreen renders; the full launcher window was not.
- The mod check runs when joining from the in-game menu. The launcher can save servers but doesn't check a server's mods before starting the game.
- "Require matching mods" is enforced by the client; a modified client can still join.
- Mod configs (`BepInEx/config`) are not synchronized. Per-user `config.json` files next to mod DLLs are deliberately excluded.
- Throughput is about 3 MB/s (LiteNetLib reliable channels); large mod packs take minutes.
- See [What doesn't sync without mod-specific work](#what-doesnt-sync-without-mod-specific-work).
- Unit tests write to the real Nitrox log folder (pre-existing test setup), so they show up in Diagnostics › Recent logs.

## Development checklist

Findings from reviewing the fork against upstream base `7815b4e4`, ordered by severity. ✔ = fixed in this pass.

| Severity | Finding | Status |
|---|---|---|
| Critical | Build broken: NuGet audit errors (MessagePack 3.1.7, ImageSharp 2.1.11) with warnings-as-errors | ✔ MessagePack pinned to 3.1.12; ImageSharp advisories suppressed with justification |
| Critical | Launcher manifest required administrator, so the game and every mod ran elevated | ✔ back to `asInvoker` (the game folder is user-writable; upstream's one-time permission fix still exists) |
| High | Anti-piracy checks removed (`PirateDetection` neutralized, `EscapePod_Start_Patch` deleted) | **Not changed.** Flagged: this lets unlicensed copies of Subnautica run Nitrod. Restoring both is recommended |
| High | `MSStore.OwnsGame` returned `true` in Debug builds, so Steam installs were treated as Microsoft Store (Steam never started, Debug ≠ Release) | ✔ |
| High | Test failure after the 2.0.0 version bump (1.8/1.9-only compatibility rule) | ✔ test derives the version; rules documented and tested for 2.x |
| High | Modded vehicles: upstream replicator fallback added an abstract component; remote players in vehicles without animator/seat would NRE | ✔ (your uncommitted `GenericVehicleMovementReplicator`, hardened) |
| Medium | `Vehicle_OnPilotModeBegin_Patch` re-requested the exclusive lock on every pilot start | ✔ only requests when not already held |
| Medium | A failed BepInEx download aborted the whole multiplayer launch and re-prompted on every launch | ✔ |
| Medium | Patch worker exceptions were swallowed while the UI pointed to a log that never received them; `MainModule` path breaks under `dotnet` host | ✔ |
| Medium | TechTypes sent via `Enum.ToString` (numeric for runtime-added TechTypes) | ✔ |
| Low | Epic ownership check logged an error with stack trace for every non-Epic install (now checked first) | ✔ |
| Low | Release download speed / LiteNetLib window tuning | Open (about 3 MB/s) |
| Info | `nitrox-patch-trace.log` tracing in the launch path left from debugging | Kept (useful), consider removing |
| Info | Platform order changed (Steam last) | Kept; harmless with the MS Store fix |

The uncommitted work found at the start (generic vehicle replicator, piloting position broadcast, remote-player vehicle guard, pilot lock) was kept and completed rather than replaced.
