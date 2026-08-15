Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Locate the repo root that owns the local .NET SDK and NuGet caches. Walking upward keeps this
# working both in a plain clone and from a git worktree, where the worktree root has no caches
# of its own. Falls back to whatever `dotnet` is on PATH.
$dotnet = "dotnet"
$shared = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$probe = $shared
for ($i = 0; $i -lt 6 -and $probe; $i++) {
    $candidate = Join-Path $probe ".dotnet\dotnet.exe"
    if (Test-Path $candidate) {
        $dotnet = $candidate
        $shared = $probe
        break
    }
    $probe = Split-Path $probe -Parent
}

$project = Join-Path $PSScriptRoot "TriffView.Tests.csproj"

New-Item -ItemType Directory -Force `
  (Join-Path $shared ".dotnet-home"), `
  (Join-Path $shared ".nuget"), `
  (Join-Path $shared ".appdata\NuGet"), `
  (Join-Path $shared ".nuget-cache"), `
  (Join-Path $shared ".nuget-plugin-cache") | Out-Null

# Pin restores to the repo-local caches instead of the user profile.
$env:DOTNET_CLI_HOME = (Join-Path $shared ".dotnet-home")
$env:NUGET_PACKAGES = (Join-Path $shared ".nuget")
$env:APPDATA = (Join-Path $shared ".appdata")
$env:NUGET_HTTP_CACHE_PATH = (Join-Path $shared ".nuget-cache")
$env:NUGET_PLUGINS_CACHE_PATH = (Join-Path $shared ".nuget-plugin-cache")
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

& $dotnet test $project @args
exit $LASTEXITCODE
