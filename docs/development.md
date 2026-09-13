# Development

Use the prerequisites in the README. All commands below start at the repository root.

## One-command setup

```powershell
./scripts/setup.ps1
```

For hot reload, first run setup with `-NoLaunch`. In terminal one:

```powershell
npm --prefix frontend run dev
```

In terminal two, still at the repository root:

```powershell
$env:PT_FRONTEND_DEV_URL = "http://localhost:5173"
$env:PT_DEV_CORS_ORIGIN = "http://localhost:5173"
dotnet run --project src/PortfolioTracker.Shell
```

## Verify

```powershell
dotnet test PortfolioTracker.slnx
npm --prefix frontend test
npm --prefix frontend run lint
npm --prefix frontend run build
```

## Package

```powershell
./scripts/package.ps1
```

This creates a self-contained Windows x64 ZIP and SHA-256 checksum in artifacts.
The GitHub workflow builds and tests on pushes and pull requests, and exposes the
package as a workflow artifact. A maintainer can attach the verified ZIP to a
GitHub Release after reviewing the change. Packaging does not create Git commits,
tags, or releases.

## Isolated development data

Set `PT_DATA_DIR` to a separate absolute directory before launching the shell to
experiment with sample data without opening your normal portfolio. Unset it to
return to the default data directory. Do not point two running instances at the
same database.
