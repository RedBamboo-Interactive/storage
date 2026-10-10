# Storage

Storage is RedLeaf's visual disk-usage extension. It scans the physical `S:`
scratch area, logical `L:` Agent workspaces, and RedLeaf application data,
then renders the largest branches as a zoomable treemap.

The distinction between physical and logical usage is deliberate: `L:` is an
entity-backed projection whose bytes already live in RedLeaf's database. The
dashboard reports it separately rather than double-counting it as physical disk
use.

## Safety contract

- Every route requires an authenticated installation administrator.
- Scans skip reparse points, tolerate inaccessible and changing files, and keep
  only the largest 120 children per directory in memory.
- Application data and Agent workspaces are read-only.
- Agent root folders are protected. Only inactive marked execution directories
  and inactive loose items directly under scratch can be selected for cleanup.
- Scratch modified within the last 30 minutes is protected.
- Cleanup first moves the exact selected item into a same-volume quarantine.
  Restore is available. Permanent deletion exists only inside quarantine and
  requires typing the exact quarantine ID.
- Every cleanup action revalidates path containment, link status, existence, and
  modification time after the scan.

## API

All routes are under `/api/apps/storage`.

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/status` | Current scan progress and latest snapshot |
| POST | `/scan` | Start one background scan |
| POST | `/cleanup/preview` | Revalidate a selected node and mint a two-minute token |
| POST | `/cleanup/quarantine` | Consume the token and move the item |
| GET | `/quarantine` | List recoverable items |
| POST | `/quarantine/{id}/restore` | Restore to the original path |
| POST | `/quarantine/{id}/purge` | Permanently delete after exact-ID confirmation |

Snapshots are intentionally rebuildable and remain in memory. Quarantine
manifests live under RedLeaf's local application-data folder and survive a
restart.

## Layout

```
plugin.json                     manifest: id, version, kernelApi range, routes, permissions
src/Leaf.Plugins.Storage/    backend — ILeafPlugin against the Leaf.Sdk package
web/                            frontend — library-mode ESM bundle + own compiled Tailwind
seeds/                          entity types & data seeded at install (idempotent upserts)
build-leafpkg.ps1               produce dist/storage-<version>.leafpkg
```

## Build

```powershell
cd web && pnpm install && cd ..
dotnet build                 # backend
./build-leafpkg.ps1          # full package + sha256
```

Building against a kernel checkout instead of the Leaf.Sdk package:
`dotnet build -p:LeafSdkProject=<kernel>\src\Leaf.Sdk\Leaf.Sdk.csproj`.

Verification:

```powershell
dotnet run --project tests/Storage.Tests -p:LeafSdkProject=<kernel>\src\Leaf.Sdk\Leaf.Sdk.csproj
corepack pnpm --dir web install
corepack pnpm --dir web run typecheck
corepack pnpm --dir web test
corepack pnpm --dir web run build:pkg
```

## Develop against a running RedLeaf

1. Enable Dev Mode in RedLeaf Settings.
2. Extensions → **Add dev plugin** → this directory. The kernel registers it in
   `leaf.workspace.json` and loads it in place of any installed copy.
3. Frontend loop: `pnpm dev` in `web/` (watch-build; reload the app to pick up
   changes). Backend loop: `dotnet build`, then restart RedLeaf
   (`POST /api/setup/restart` or the tray menu).

## Publish

`./build-leafpkg.ps1` → attach the `.leafpkg` to a GitHub Release → submit the
release URL + sha256 to a registry index. Users install it from `/extensions`
(or by URL) with sha256 verification.

## Rules that will save you a debugging afternoon

- Never remove entries from the `external` list in `web/vite.config.ts` — a
  second bundled React forks hooks/context at runtime.
- Never ship `Leaf.Sdk.dll` / `RedBamboo.AppHost.dll` inside the package — it
  breaks plugin type identity; the installer refuses such packages
  (`build-leafpkg.ps1` strips them automatically).
- Kernel services in endpoint handlers need
  `[FromKeyedServices(StoragePlugin.PluginId)]` — a plain parameter fails at
  kernel startup, not at build time.
