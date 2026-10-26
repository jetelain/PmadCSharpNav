# Pmad.CSharpNav (`csnav`)

A high-performance, machine-oriented C# code navigation engine designed for AI coding agents and developers.

`csnav` turns complex Roslyn workspace analysis into fast, token-efficient, and structured navigation queries. It operates as a lightweight client-daemon architecture: queries are answered in milliseconds by a warm in-memory Roslyn workspace daemon that automatically updates as files change on disk.

---

## Key Features

- **Blazing Fast Navigation via Daemon Architecture**: Auto-spawns a background HTTP daemon per solution on demand with loopback token security and automatic idle shutdown.
- **Incremental Roslyn Workspace Sync**: Tracks file system changes via debounced watchers and provides instant manual synchronization for uncommitted buffer changes.
- **Reference Finding**: Resolves calls, implementations, overrides, reads/writes with snippet extraction and test exclusion.
- **Fast Public API Search**: Search declarations and external dependencies (NuGet/BCL) with wildcard patterns and MVID-keyed disk caching.
- **Deep Member Exploration**: Inspect type signatures, inheritance, interfaces, constructors, methods, properties, and doc comments with partial class support.
- **External Library Decompiler**: Built on `ICSharpCode.Decompiler` (ILSpy) to decompile external types and methods, automatically resolving reference assemblies (`Microsoft.NETCore.App.Ref`) to runtime implementation assemblies.
- **Agent-Friendly Output Formats**: Choose between concise human `text`, clean `markdown`, or dense token-efficient `json`.

---

## Solution Structure & Clean Architecture

The solution uses the XML-based `.slnx` format (`PmadCSharpNav.slnx`) and strictly adheres to Clean Architecture:

```
├── docs/
│   └── ARCHITECTURE.md                  # Comprehensive architectural specification
├── src/
│   ├── Pmad.CSharpNav.Domain/           # Zero dependencies: Core models & domain primitives
│   ├── Pmad.CSharpNav.Contracts/        # Shared DTOs, discovery info, and JSON serializer
│   ├── Pmad.CSharpNav.Application/      # Ports (interfaces) & use case query handlers
│   ├── Pmad.CSharpNav.Infrastructure/   # Roslyn workspace, file watcher, and ILSpy adapters
│   ├── Pmad.CSharpNav.Server/           # Minimal API daemon with bearer security & idle timer
│   └── Pmad.CSharpNav.Cli/              # System.CommandLine client depending ONLY on Contracts
└── tests/
    ├── Pmad.CSharpNav.Domain.Tests/
    ├── Pmad.CSharpNav.Application.Tests/
    ├── Pmad.CSharpNav.Infrastructure.Tests/
    └── Pmad.CSharpNav.IntegrationTests/ # End-to-end tests against real Roslyn solutions
```

---

## Building and Testing

### Prerequisites
- [.NET 10.0 SDK](https://dotnet.microsoft.com/)

### Build the Solution
```bash
dotnet build
```
*(All projects build with `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` under `latest-recommended` code analysis).*

### Run All Unit and Integration Tests
```bash
dotnet test
```

---

## Installation

### Local Installation (Testing without NuGet.org)
You can build, package, and install `csnav` locally as a global `.NET` tool using the PowerShell installation script:

```powershell
# Build, pack, and install/update globally as 'csnav'
.\install-cli.ps1

# Force reinstall (wiping existing global tool & local package cache)
.\install-cli.ps1 -Force

# Install tool and also install the AI agent skill globally
.\install-cli.ps1 -InstallSkill
```

### AI Agent Skill Integration
`csnav` includes a built-in skill for AI coding agents (compatible with Antigravity / Gemini / Copilot / Claude agent environments):

```bash
# Install skill in current repository (.agents/skills/csharp-nav/SKILL.md)
csnav skills install

# Install skill globally (~/.gemini/config/skills/ and ~/.agents/skills/)
csnav skills install --global

# Inspect the skill instructions directly in the console
csnav skills show
```

---

## CLI Usage

### Global Options
All commands support the following options:
- `-s, --solution <path>`: Path to `.slnx`, `.sln`, `.csproj`, or directory (auto-detected by walking up parent directories if omitted).
- `-f, --framework <tfm>`: Target framework filter (e.g. `net10.0`) when multi-targeting.
- `--format <format>`: Output format: `markdown` (default), `json`, or `text` (configurable via `CSNAV_FORMAT` or `CSNAV_DEFAULT_FORMAT` environment variable).
- `--no-color`: Disable ANSI color formatting (colors enabled for markdown and text by default unless output is redirected or `NO_COLOR` is set).

File paths returned in results (`references`, candidates) are automatically formatted relative to the current working directory (`pwd`) for compactness and readability.

---

### Commands

#### 1. `references` (alias `refs`) — Find References
Find all usages, call sites, implementations, or overrides of a symbol or source location:
```bash
# By symbol name
csnav references --symbol "NavConstants"
# Or shorthand: csnav refs --symbol "NavConstants"

# Exclude references in test projects
csnav references --symbol "NavConstants" --exclude-tests

# By file location (1-indexed line and column)
csnav references --file src/Pmad.CSharpNav.Contracts/NavConstants.cs --line 5 --column 25

# Format as JSON
csnav references --symbol "FindReferencesRequest" --format json
```

#### 2. `search` (alias `api`, `find`) — Search Public API Signatures
Search public type and member signatures across source code and referenced libraries:
```bash
# Search by name pattern
csnav search "*Serializer"
# Or shorthand: csnav api "*Serializer"

# Search in solution scope only
csnav search "Nav*" --scope solution

# Search within XML doc comments
csnav search --doc "sha256" --format markdown
```

#### 3. `inspect` (alias `explore`, `type`) — Type & Member Inspection
Inspect type hierarchy, base classes, interfaces, constructors, methods, and properties:
```bash
# Inspect type by fully qualified name
csnav inspect --type "Pmad.CSharpNav.Contracts.FindReferencesRequest"
# Or shorthand: csnav explore --type "Pmad.CSharpNav.Contracts.FindReferencesRequest"

# Exclude inherited members
csnav inspect --type "Pmad.CSharpNav.Contracts.FindReferencesRequest" --no-inherited

# By source file location
csnav inspect --file src/Pmad.CSharpNav.Domain/SymbolKind.cs --line 8 --column 12
```

#### 4. `decompile` — Decompile External Assemblies
Decompile BCL or NuGet library types and members to C# source code:
```bash
# Decompile standard library type
csnav decompile --type "System.Uri"

# Decompile specific member
csnav decompile --type "System.Uri" --member "IsLoopback"
```

#### 5. `server` — Manage Background Daemon
Control and inspect the background daemon for the active solution:
```bash
# Check daemon status
csnav server status

# Start daemon in background
csnav server start

# Reload workspace manually
csnav server reload

# Gracefully stop daemon
csnav server stop
```

---

## Daemon Discovery & Lifecycle

When a query is issued:
1. `csnav` computes a SHA-256 hash of the normalized solution path.
2. It looks for a discovery file in `%LOCALAPPDATA%/PmadCSharpNav/servers/<hash>.json`.
3. If no healthy daemon is found, the CLI acquires a per-solution lock (`<hash>.lock`), spawns `Pmad.CSharpNav.Server` in the background, and polls `/api/health` until ready.
4. Each daemon generates a cryptographic bearer token saved with restricted permissions in the discovery file.
5. Inactivity triggers automatic shutdown after 30 minutes of idle time.
