<#
.SYNOPSIS
  Builds the Knovara Desktop installer (a self-contained, per-user Windows setup .exe).

.PARAMETER Version
  Semantic version stamped into the app and the installer, e.g. 1.2.0.

.PARAMETER ControlPlaneUrl
  Optional address of your control plane. It is written into the installed app's settings so the
  enrollment screen opens pre-filled and users only need their enrollment code.

.EXAMPLE
  .\installer\build-installer.ps1 -Version 1.0.0 -ControlPlaneUrl https://control.example.com

.NOTES
  Needs the .NET SDK and Inno Setup 6 (free, https://jrsoftware.org/isinfo.php). The result is not
  code-signed, so Windows SmartScreen will warn on first run until you sign it with your own certificate.
#>
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')] [string] $Version,
    [string] $ControlPlaneUrl = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish'
$output = Join-Path $root 'artifacts\installer'

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 was not found. Install it from https://jrsoftware.org/isinfo.php and run this again.' }

if ($ControlPlaneUrl -and $ControlPlaneUrl -notmatch '^https?://[^\s"]+$') { throw "ControlPlaneUrl must be an http(s) address, got '$ControlPlaneUrl'." }

Write-Host "Publishing Knovara Desktop $Version (self-contained, win-x64)..."
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'src\Knovara.Desktop\Knovara.Desktop.csproj') -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -o $publish --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Write-Host 'Compiling the installer...'
$defines = @("/DAppVersion=$Version", "/DSourceDir=$publish", "/DOutputDir=$output")
if ($ControlPlaneUrl) { $defines += "/DControlPlaneUrl=$($ControlPlaneUrl.TrimEnd('/'))" }
# 2>&1 so the compiler's own error message is shown; without it a failure is just "exit code 1".
$compilerOutput = & $iscc @defines (Join-Path $PSScriptRoot 'knovara.iss') 2>&1
$compilerOutput | Where-Object { $_ -notmatch '^Parsing ' } | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed; see its message above.' }

$setup = Join-Path $output "Knovara-Desktop-Setup-$Version.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host ''
Write-Host "Built:   $setup"
Write-Host ("Size:    {0:N1} MB" -f ((Get-Item $setup).Length / 1MB))
Write-Host "SHA-256: $hash"
Write-Host 'Upload it on the control plane dashboard (Downloads > Publish a new version).'
