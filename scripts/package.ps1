$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repositoryRoot
try {
    $artifactRoot = Join-Path $repositoryRoot 'artifacts'
    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
    $packageDirectory = Join-Path $artifactRoot ('package-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $packageDirectory | Out-Null
    & dotnet publish 'src/PortfolioTracker.Shell' -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $packageDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }
    Copy-Item -LiteralPath 'README.md','LICENSE','CONTRIBUTING.md','SECURITY.md' -Destination $packageDirectory
    Copy-Item -LiteralPath 'docs' -Destination $packageDirectory -Recurse
    Copy-Item -LiteralPath 'samples' -Destination $packageDirectory -Recurse
    $zipPath = Join-Path $artifactRoot 'PortfolioTracker-windows-x64.zip'
    Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $zipPath -Force
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($zipPath + '.sha256') -Value ($hash + '  ' + [IO.Path]::GetFileName($zipPath)) -Encoding ascii
    Write-Output $zipPath
} finally { Pop-Location }
