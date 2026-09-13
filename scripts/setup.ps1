param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repositoryRoot
try {
    if (-not (Get-Command node -ErrorAction SilentlyContinue) -or -not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'Install Node.js 24 and the .NET 10 SDK before running setup.'
    }
    if ([int]((& node -p 'process.versions.node.split(".")[0]')) -ne 24) { throw 'Use Node.js 24 for this project.' }
    & npm --prefix frontend ci
    if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency installation failed.' }
    & npm --prefix frontend run build
    if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
    # MSBuild mirrors the fresh UI into wwwroot and embeds it, in both configurations.
    & dotnet build src/PortfolioTracker.Shell -p:SkipFrontendBuild=true
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
    if (-not $NoLaunch) {
        & dotnet run --no-build --project src/PortfolioTracker.Shell
        if ($LASTEXITCODE -ne 0) { throw 'The app could not start. Check the runtime prerequisites in README.md.' }
    }
} finally { Pop-Location }
