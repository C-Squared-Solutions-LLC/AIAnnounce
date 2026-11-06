<#
.SYNOPSIS
Builds AI Announce. Needs the .NET 8 SDK (or newer) and nothing else.

.EXAMPLE
.\build.ps1
dist\AIAnnounce.exe: framework-dependent single file, for running from source. Close the app first.

.EXAMPLE
.\build.ps1 -Release
artifacts\: the MSI installer and both portable zips, the same files CI attaches to a release.
#>
param(
    [switch]$Release,
    # x.y.z, the only form an MSI version takes. Defaults to <Version> in AIAnnounce.csproj.
    [string]$Version,
    # what goes in file names, e.g. 1.2.6-ci.42. Defaults to the version.
    [string]$Label
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $Version) {
    $Version = ([xml](Get-Content AIAnnounce.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be x.y.z, got '$Version'." }
if (-not $Label) { $Label = $Version }

# Quote every -p:Name=value: unquoted, PowerShell splits it at the colon on the way through @args.
function Invoke-Dotnet {
    dotnet @args
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
}

if (-not $Release) {
    $exe = Join-Path $PSScriptRoot 'dist\AIAnnounce.exe'
    if (Get-Process AIAnnounce -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) {
        throw 'dist\AIAnnounce.exe is running. Close it first so the exe can be replaced.'
    }
    Invoke-Dotnet publish AIAnnounce.csproj -c Release -r win-x64 --self-contained false '-p:PublishSingleFile=true' "-p:Version=$Version" -o dist
    Write-Host "Built $exe"
    return
}

$out = Join-Path $PSScriptRoot 'artifacts'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

# Small exe that needs the .NET 8 Desktop Runtime or newer.
Invoke-Dotnet publish AIAnnounce.csproj -c Release -r win-x64 --self-contained false '-p:PublishSingleFile=true' `
    "-p:Version=$Version" -o "$out\framework-dependent"

# Larger exe that runs with no .NET installed. The installer carries this one.
Invoke-Dotnet publish AIAnnounce.csproj -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' `
    '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' `
    "-p:Version=$Version" -o "$out\self-contained"

Invoke-Dotnet build installer\AIAnnounce.Installer.wixproj -c Release `
    "-p:Version=$Version" "-p:PublishDir=$out\self-contained\" "-p:OutputName=AIAnnounce-$Label-win-x64" -o $out

foreach ($flavor in 'framework-dependent', 'self-contained') {
    $dir = "$out\$flavor"
    Copy-Item README.md, LICENSE $dir
    Get-ChildItem $dir -Filter *.pdb | Remove-Item
    Compress-Archive -Path "$dir\*" -DestinationPath "$out\AIAnnounce-$Label-win-x64-$flavor.zip"
}
Get-ChildItem "$out\*" -Include *.msi, *.zip | Format-Table Name, Length
