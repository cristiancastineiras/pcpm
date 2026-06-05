# pcpm

> A fast, pnpm-style package manager for .NET, built on Central Package Management.

`pcpm` brings the ideas that made pnpm great — a content-addressable global store, hardlinks
instead of duplicate copies, deterministic lockfiles, strict dependency resolution — to the
.NET / NuGet ecosystem. It leans on [Central Package Management (CPM)][cpm] for version
centralisation, so you get pnpm's "one source of truth" feel without inventing a new
manifest format.

[cpm]: https://learn.microsoft.com/nuget/consume-packages/Central-Package-Management

## Why

| Pain                                       | pnpm solves it with                | pcpm does the same                 |
|--------------------------------------------|------------------------------------|------------------------------------|
| Same package stored N times across projects| Content-addressable store + links  | `%LOCALAPPDATA%\pcpm\store` + NTFS hardlinks |
| Floats like `1.0.*` create non-reproducible builds | Pinned lockfile               | `pcpm.lock`                        |
| Hoisting / phantom dependencies            | Strict, non-flat layout            | `pcpm` does not flat-hoist; `dotnet restore` reads only what the lockfile says |
| Monorepos with shared deps                | `pnpm-workspace.yaml`              | `pcpm-workspace.yaml`              |
| Slow `dotnet restore` on big solutions    | Pre-warmed global store            | `pcpm install` hydrates `~/.nuget/packages` once, every project links to it |

## Quick start

```bash
# 1. Drop pcpm on your PATH (or use `dotnet run` from this repo)
dotnet build pcpm.slnx

# 2. In your repo's root
pcpm init                          # creates pcpm.json, Directory.Packages.props
pcpm add Newtonsoft.Json           # adds the latest stable to CPM + .csproj
pcpm install                       # resolves, downloads, links, runs dotnet restore
pcpm list                          # pretty-prints pcpm.lock
pcpm why Newtonsoft.Json           # shows who pulls it in
pcpm outdated                      # checks for newer versions
pcpm store status                  # disk usage of the global store
pcpm remove Newtonsoft.Json        # undo
```

## Commands

| Command                | What it does                                                                         |
|------------------------|--------------------------------------------------------------------------------------|
| `pcpm init`            | Initialise a workspace (`pcpm.json`, `Directory.Packages.props`, `pcpm-workspace.yaml` if multi-project). |
| `pcpm add <pkg>`       | Add a direct dependency. `--version <v>` to pin, `--project <path>` to target one csproj, `--no-install` to skip the implicit `install`. |
| `pcpm install`         | Resolve the transitive graph, materialise packages into the store, hardlink to `~/.nuget/packages`, run `dotnet restore`. |
| `pcpm list`            | Pretty-print `pcpm.lock` as a Spectre table.                                          |
| `pcpm remove <pkg>`    | Remove from CPM and every referencing `.csproj`.                                     |
| `pcpm why <pkg>`       | Show the chains of dependents that pull `<pkg>` into the lockfile.                   |
| `pcpm outdated`        | Query the feed for newer versions, report bump type (major / minor / patch).          |
| `pcpm store status`    | Disk usage of the global content-addressable store.                                  |
| `pcpm store path`      | Print the store path.                                                                |
| `pcpm store prune`     | (Reserved for a future release — would walk the current workspace's lockfile and drop unused hashes.) |

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  pcpm.Cli          Spectre.Console.Cli commands (one per verb)   │
│  ─────────         thin orchestration only, no business logic    │
└────────────────────────┬────────────────────────────────────────┘
                         │ DI (Microsoft.Extensions.DependencyInjection)
┌────────────────────────┴────────────────────────────────────────┐
│  pcpm.Core          Domain models, abstractions, pure logic     │
│  ────────           DependencyResolver, no I/O                   │
└────────────────────────┬────────────────────────────────────────┘
                         │
┌────────────────────────┴────────────────────────────────────────┐
│  pcpm.Infrastructure   Implementations:                          │
│  ──────────────────    • NuGetFeed (raw HTTP, no NuGet.Protocol) │
│                        • PackageStore (content-addressable)     │
│                        • HardlinkCreator (Win32 P/Invoke)        │
│                        • CpmFileService, ProjectFileService,     │
│                          LockfileService (XML/JSON I/O)          │
│                        • PhysicalFileSystem, ProcessRunner      │
└─────────────────────────────────────────────────────────────────┘
```

### Why this layout

- **Core has zero I/O.** Easy to test in isolation, and the abstractions it defines are
  the actual contract. If we want a future in-memory store, or a fake feed for unit tests,
  we drop in another implementation.
- **Infrastructure is the only place that touches the disk or the network.** That means a
  small, well-understood surface for "is this safe under the test runner".
- **Cli is orchestration.** Every command takes its dependencies through constructor
  injection. The `dotnet run … pcpm install` flow is `Program → InstallCommand → (CpmFileService,
  ProjectFileService, ProjectDiscovery, NuGetFeed, PackageStore, LockfileService,
  DependencyResolver, ProcessRunner, IAnsiConsole)`.

### The content-addressable store (the pnpm part)

```
%LOCALAPPDATA%\pcpm\store\
  v1\
    <sha256>\
      pkg.nupkg          # immutable copy
      extracted\
        <id>\<version>\lib\net10.0\…
```

On `pcpm install`, every resolved package is downloaded once, hashed, moved into the store
under its content hash, and then **hardlinked** (NTFS, same volume) into
`~/.nuget/packages/<id>/<version>/`. The hardlinks are free — zero extra disk, instant
materialisation — and `dotnet restore` sees a perfectly normal NuGet layout, so all the
MSBuild magic keeps working.

Future plans: cross-volume copies when the system drive is unavailable; symlinks on
non-Windows (currently copy fallback).

### Dependency resolution

Pure logic in `pcpm.Core.Services.DependencyResolver`. BFS over the dependency graph, with
union-of-constraints semantics: when a package is required by multiple paths, we pick the
highest version that satisfies *all* the accumulated ranges. If no version satisfies them
all, the package is reported as a `ResolutionConflict` and `pcpm install` exits 1.

Version semantics are delegated to `NuGet.Versioning` — the same library `dotnet restore`
itself uses, so range parsing, pre-release handling, and floating semantics are
consistent.

## Layout on disk

```
C:\Users\carana\Desktop\pcpm\
├── pcpm.slnx                          # solution
├── Directory.Packages.props           # CPM file (pcpm itself uses CPM)
├── Directory.Build.props              # common MSBuild (warnings as errors, nullable, etc.)
├── NuGet.config                       # nuget.org as the only source
├── global.json                        # pin to .NET 10.0.202
├── src\
│   ├── pcpm.Core\                     # domain, no I/O
│   ├── pcpm.Infrastructure\           # I/O implementations
│   └── pcpm.Cli\                      # Spectre commands
└── tests\
    └── pcpm.Tests\                    # xUnit + FluentAssertions + NSubstitute
```

## Build & test

```bash
dotnet build pcpm.slnx
dotnet test  tests/pcpm.Tests
```

The test suite covers:

- `PackageId` / `PackageVersion` validation
- `DependencyResolver` happy path, transitive resolution, conflict detection
- `CpmFileService` round-trips and user-property preservation
- `ProjectDiscoveryService` glob patterns and `pcpm-workspace.yaml` overrides

## Roadmap

- TFM-aware dependency group selection (real `NuGet.Frameworks` compatibility reduction)
- `pcpm update [pkg]` — bump one or all packages, re-resolve, re-materialise
- `pcpm store prune` — drop hashes not referenced by the current lockfile
- Cross-platform symlink store (so non-Windows gets the same zero-copy win)
- Optional offline / vendored mode (no network, only the store)
- `pcpm ci` — strict, fail-fast install (refuses if `pcpm.lock` is stale)

## License

MIT.
