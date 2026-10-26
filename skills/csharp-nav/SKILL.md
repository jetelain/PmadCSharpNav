---
name: csharp-nav
description: High-performance C# navigation engine for AI agents and developers. Use when navigating, searching, or exploring C# codebases. Supports finding references, searching public APIs, exploring type hierarchies and members, and decompiling external BCL/NuGet assemblies.
---

# C# Code Navigation with `csnav`

`csnav` is a fast, semantic, and token-efficient C# code navigation engine powered by Roslyn and ILSpy. It uses a warm background daemon to answer queries in milliseconds.

Always prefer `csnav` over `grep` or file text searching when navigating C# code:
- **Semantic Awareness**: Understands inheritance, overrides, interface implementations, and call sites without noise from comments, strings, or similarly named variables.
- **Token Efficiency**: Returns structured data directly (`--format json`) or clean summaries (`--format markdown`), saving context window tokens.
- **Warm Daemon**: Solutions are kept indexed in memory and automatically kept in sync via file watchers.

---

## Command Reference

### 1. `csnav references` (alias: `csnav refs`) — Find References
Finds all usages, call sites, implementations, and reads/writes of a symbol.

```bash
# Find references by symbol name (e.g. method, class, interface, property)
csnav references --symbol "ProcessOrder" --format json
# Or shorthand: csnav refs --symbol "ProcessOrder" --format json

# Exclude references in test projects
csnav references --symbol "IOrderRepository.SaveOrder" --exclude-tests --format json

# Find references at exact source file location (1-indexed line and column)
csnav references --file "src/Services/OrderService.cs" --line 42 --column 18 --format json

# Filter references to a specific project
csnav references --symbol "Order" --project "MyApp.Core" --format json
```

**Key Options:**
- `--symbol <name>`: Symbol name, qualified name, or doc ID (`M:...`).
- `--file <path> --line <int> --column <int>`: Resolve symbol at source location.
- `--exclude-tests`: Excludes usages in test projects (matches `*.Tests`, `*.Test`, etc.).
- `--project <name>`: Restrict references to a single project.
- `--limit <int>`: Max results (default `100`).
- `--format <markdown|json|text>`: Output format (default `markdown`, configurable via `CSNAV_FORMAT` or `CSNAV_DEFAULT_FORMAT` env var).
- `--no-color`: Disable ANSI color formatting (colors enabled for markdown and text by default unless output is redirected).

---

### 2. `csnav search` (alias: `csnav api`, `csnav find`) — Public API & Symbol Search
Searches type and member declarations using wildcard patterns across source code and external referenced assemblies.

```bash
# Search by pattern (wildcard * supported)
csnav search "*Repository" --format json
# Or shorthand: csnav api "*Repository" --format json

# Search solution code only (fastest)
csnav search "*Service" --scope solution --format json

# Include external dependencies (NuGet & .NET BCL)
csnav search "JsonSerializer*" --scope all --format json

# Filter by symbol kind (class, interface, method, property, enum, record, struct)
csnav search "I*Handler" --kind interface --format json

# Search within XML documentation comments (summary, param, returns)
csnav search --doc "calculates sha256" --format markdown
```

**Key Options:**
- `<pattern>`: Search pattern (e.g. `Order*`, `*Repository`, `Find*`).
- `--scope <solution|all>`: `solution` for project sources, `all` to include external dependencies (default `solution`).
- `--kind <type|class|interface|method|property|all>`: Filter by declaration kind.
- `--access <public|internal|all>`: Minimum accessibility (default `public`).
- `--doc <text>`: Full-text search across XML documentation comments (summary, param, returns).
- `--limit <int>`: Max results (default `50`).

---

### 3. `csnav inspect` (alias: `csnav explore`, `csnav type`) — Type & Member Inspection
Inspects constructors, methods, properties, fields, base types, and implemented interfaces.

```bash
# Inspect type by fully qualified name
csnav inspect --type "MyApp.Services.OrderService" --format json
# Or shorthand: csnav explore --type "MyApp.Services.OrderService" --format json

# Inspect only declared members (exclude inherited object/base members)
csnav inspect --type "MyApp.Services.OrderService" --no-inherited --format json

# Inspect type defined at source location
csnav inspect --file "src/MyApp/OrderService.cs" --line 10 --column 15 --format json

# Include internal and private members
csnav inspect --type "MyApp.Services.OrderService" --access all --format json
```

**Key Options:**
- `--type <name>`: Fully qualified type name.
- `--file <path>`: Source file containing the type declaration.
- `--no-inherited`: Show only members declared directly on this type.
- `--access <public|internal|private|all>`: Minimum accessibility (default `public`).

---

### 4. `csnav decompile` — Decompile External Assemblies
Decompiles external NuGet or .NET BCL libraries to readable C# source code, automatically mapping reference assemblies (`.Ref`) to runtime implementation assemblies.

```bash
# Decompile an entire external BCL type
csnav decompile --type "System.Uri" --format markdown

# Decompile a specific member
csnav decompile --type "System.Uri" --member "IsLoopback" --format markdown

# Decompile NuGet package symbol
csnav decompile --assembly "System.Text.Json" --type "System.Text.Json.JsonSerializer"
```

**Key Options:**
- `--type <name>`: Fully qualified type name to decompile.
- `--member <name>`: Specific member to decompile (optional).
- `--assembly <name>`: External assembly name (optional).

---

### 5. `csnav server` — Daemon Management
Controls the warm background daemon managing the Roslyn workspace.

```bash
# Check status of the background daemon
csnav server status

# Force workspace reload
csnav server reload

# Gracefully stop daemon
csnav server stop
```

---

## Agent Guidelines & Workflows

1. **Investigating an Unknown Codebase:**
   - Use `csnav search "*"` or `csnav search "*Service"` (or `csnav api`) with `--scope solution` to map available domain services.
   - Use `csnav inspect --type <Type> --no-inherited --format json` (or `csnav explore`) to inspect API contracts before reading full files.

2. **Refactoring or Changing a Method:**
   - Before modifying a method, run `csnav references --symbol <MethodName> --exclude-tests` (or `csnav refs`) to see all production call sites.
   - Run `csnav references --symbol <MethodName>` without `--exclude-tests` to see which unit tests need updating.

3. **Tracing Interface Implementations:**
   - Run `csnav references --symbol <InterfaceName>` to find all classes implementing the interface and places injecting it.

4. **Investigating External Dependencies:**
   - Use `csnav decompile --type <ExternalType>` to inspect the actual implementation when debugging third-party library behavior or framework internals.

5. **Token Efficiency:**
   - Prefer `--format json` for automated processing and data extraction.
   - File paths are automatically returned relative to current working directory (`pwd`), significantly reducing token overhead.
   - Use `--limit` when searching broad patterns to avoid unnecessary token usage.