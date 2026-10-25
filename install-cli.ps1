<#
.SYNOPSIS
    Builds the Pmad.CSharpNav tool package and installs it globally as a .NET tool.
.DESCRIPTION
    Packs the Pmad.CSharpNav.Cli project (which bundles the Server daemon binaries)
    into a standalone NuGet tool package (.nupkg), clears any cached local versions
    in the user NuGet cache to ensure fresh binaries, and installs or updates
    the 'csnav' global tool.
.PARAMETER Configuration
    The build configuration to pack (default: Release).
.PARAMETER Force
    Uninstalls the existing global tool and wipes the cache before reinstalling.
.PARAMETER SkipBuild
    Skips the build/pack step and installs the existing .nupkg from the output folder.
.PARAMETER InstallSkill
    Automatically installs the agent skill globally to ~/.gemini/config/skills/ and ~/.agents/skills/.
.EXAMPLE
    .\install-cli.ps1
.EXAMPLE
    .\install-cli.ps1 -Force
.EXAMPLE
    .\install-cli.ps1 -InstallSkill
#>
param(
    [string]$Configuration = "Release",
    [switch]$Force,
    [switch]$SkipBuild,
    [switch]$InstallSkill
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $scriptDir "src/Pmad.CSharpNav.Cli/Pmad.CSharpNav.Cli.csproj"
$packageOutputDir = Join-Path $scriptDir "src/Pmad.CSharpNav.Cli/bin/package"
$packageName = "Pmad.CSharpNav"
$toolCommand = "csnav"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Pmad.CSharpNav CLI - Build and Global Tool Installer     " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Build and Pack
if (-not $SkipBuild) {
    Write-Host "`n[1/3] Packaging $packageName ($Configuration)..." -ForegroundColor Gray
    if (Test-Path $packageOutputDir) {
        Remove-Item "$packageOutputDir\*.nupkg" -Force -ErrorAction SilentlyContinue
    } else {
        New-Item -ItemType Directory -Path $packageOutputDir -Force | Out-Null
    }

    dotnet pack $projectPath -c $Configuration -o $packageOutputDir --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to package $packageName. See build output above."
        exit $LASTEXITCODE
    }
    Write-Host "Package created in: $packageOutputDir" -ForegroundColor Green
} else {
    Write-Host "`n[1/3] Skipping build/pack step (-SkipBuild specified)." -ForegroundColor Yellow
}

# 2. Clear NuGet cache for local package
Write-Host "`n[2/3] Clearing local NuGet cache for $packageName..." -ForegroundColor Gray
$userCacheDir = Join-Path $HOME ".nuget/packages/$($packageName.ToLowerInvariant())"
if (Test-Path $userCacheDir) {
    Remove-Item $userCacheDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Cleared cache: $userCacheDir" -ForegroundColor Gray
}

# 3. Install or Update Global Tool
Write-Host "`n[3/3] Installing or updating global dotnet tool '$toolCommand'..." -ForegroundColor Gray

$installArgs = @("--add-source", $packageOutputDir, "--no-cache")
$nupkgFile = Get-ChildItem "$packageOutputDir\$packageName.*.nupkg" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($nupkgFile -and $nupkgFile.Name -match "^$packageName\.(.+)\.nupkg$") {
    $packageVersion = $Matches[1]
    Write-Host "Target package version: $packageVersion" -ForegroundColor Gray
    $installArgs += @("--version", $packageVersion)
} else {
    $installArgs += @("--prerelease")
}

$installedTools = dotnet tool list -g
$isInstalled = $installedTools -match "(?i)\b$packageName\b"

if ($Force -and $isInstalled) {
    Write-Host "[Force] Uninstalling existing global tool if present..." -ForegroundColor Yellow
    & dotnet tool uninstall -g $packageName | Out-Null
    $isInstalled = $false
}

if ($isInstalled) {
    Write-Host "Updating global tool '$toolCommand'..." -ForegroundColor Cyan
    & dotnet tool update -g $packageName @installArgs
} else {
    Write-Host "Installing global tool '$toolCommand'..." -ForegroundColor Cyan
    & dotnet tool install -g $packageName @installArgs
}

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to install/update global tool. See error output above."
    exit $LASTEXITCODE
}

# 4. Optional / Recommended: Install AI Agent Skill
if ($InstallSkill) {
    Write-Host "`nInstalling AI agent skill 'csharp-nav' globally..." -ForegroundColor Cyan
    & $toolCommand skills install --global
}

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host " Installation Successful!                                 " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green

Write-Host "`nYou can now run '$toolCommand [command] [options]' in any C# repository." -ForegroundColor Cyan
Write-Host "Tip: Run '$toolCommand skills install' in any project or '$toolCommand skills install --global' to install the AI agent skill.`n" -ForegroundColor Gray
