# Codex Token Monitor

[中文说明](README.md)

A small, local Windows widget for understanding Codex task usage. It shows
input, cached input, output, estimates, account usage snapshots, and separate
main-task and nested-agent usage. This is an independent community tool.

Previews use synthetic logs:

![Widget](docs/images/main.png) ![Task summary](docs/images/details.png)

It reads local session logs and optional model-name evidence from a read-only
SQLite connection. It does not read authentication files, modify Codex data, or
make network requests. Task names may appear on screen; use synthetic data for
public screenshots.

## Run

The framework-dependent package needs Windows 10/11 and .NET 8 Desktop Runtime.
Keep the EXE, DLL, deps.json, and runtimeconfig.json together. Double-click
`CodexTokenMonitor.exe`.

The default data directory is the current user's `.codex` directory. `CODEX_HOME`
is respected. Settings use the current user's LocalApplicationData directory,
independent of where the repository or EXE lives.

```powershell
.\CodexTokenMonitor.exe '--codex-home=D:\CodexData'
.\CodexTokenMonitor.exe '--sessions=D:\CodexData\sessions'
.\CodexTokenMonitor.exe --portable
.\CodexTokenMonitor.exe '--diagnose-paths=paths.json'
```

Other overrides: `--settings=...` and `--model-log=...`. Paths support `~` and
environment-variable expansion. Relative paths are resolved against the EXE's
directory. `--portable` only stores widget settings in `data/settings.json`
beside the EXE; Codex session data stays in its original directory.

Path diagnosis writes paths and existence flags without reading session or
authentication contents. Review it before sharing because paths can contain
your username. See the Chinese README for override precedence and full details.

## Build and check

Install the .NET 8 SDK. From the repository root:

```powershell
.\scripts\Build.ps1
.\scripts\Test.ps1
.\scripts\Publish.ps1
```

Output: `artifacts/publish/framework-dependent/`. The project has no third-party
NuGet package dependencies. A self-contained Windows package can be produced with
`./scripts/Publish.ps1 -SelfContained -Runtime win-x64`; that build downloads the
official .NET runtime packs and includes them in its output.

Tests use synthetic local files. GitHub Actions builds, checks, and publishes a
framework-dependent artifact on Windows. It does not publish a release.

## Limits

Cost estimates are not subscription bills. Account usage is a possibly stale
snapshot, and unavailable evidence remains unavailable. Local session schemas
and SQLite locations are internal implementation details and may change.
Archived sessions are not scanned. WSL/Linux data needs an explicit directory
that is accessible from Windows; the UI itself is Windows-only.

Detailed pricing assumptions, counting rules, diagnostics, and known limitations
are documented in [README.md](README.md).
