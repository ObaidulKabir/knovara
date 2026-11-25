<#
.SYNOPSIS
  Builds the Knovara installer (a self-contained, per-user Windows setup .exe): the desktop app, the
  local runtime (API in Runtime:Mode=Local, with its two ONNX models bundled), the `knovara` CLI and the
  `knovara-mcp` server for Claude Code/Desktop - everything "install it, add a folder, ask" needs, with
  no Docker and no account.

.PARAMETER Version
  Semantic version stamped into the app and the installer, e.g. 1.2.0.

.PARAMETER ControlPlaneUrl
  Optional address of your control plane. Written into the installed app's settings so the "Connect to a
  team or organization server" panel opens pre-filled; the local runtime works without this.

.PARAMETER ModelsDir
  Where the two bundled ONNX models are read from (default: the same folder the local runtime itself
  reads from, see fetch-models.ps1). Run fetch-models.ps1 first if this is empty.

.EXAMPLE
  .\installer\build-installer.ps1 -Version 1.0.0 -ControlPlaneUrl https://control.example.com

.NOTES
  Needs the .NET SDK and Inno Setup 6 (free, https://jrsoftware.org/isinfo.php). The result is not
  code-signed, so Windows SmartScreen will warn on first run until you sign it with your own certificate.
#>
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')] [string] $Version,
    [string] $ControlPlaneUrl = '',
    [string] $ModelsDir = (Join-Path $env:LOCALAPPDATA 'Knovara\models')
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

foreach ($modelFile in @('bge-small\model.onnx', 'bge-small\tokenizer.json', 'minilm-rerank\model.onnx', 'minilm-rerank\tokenizer.json')) {
    if (-not (Test-Path (Join-Path $ModelsDir $modelFile))) {
        throw "Model file missing: $(Join-Path $ModelsDir $modelFile). Run installer\fetch-models.ps1 first (or pass -ModelsDir)."
    }
}

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

function Publish-Component([string] $Project, [string] $SubDir, [string] $AssemblyName) {
    Write-Host "Publishing $AssemblyName (self-contained, win-x64)..."
    dotnet publish (Join-Path $root "src\$Project\$Project.csproj") -c Release -r win-x64 --self-contained true `
        -p:Version=$Version -o (Join-Path $publish $SubDir) --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Project." }
}

Publish-Component 'Knovara.Desktop' '.' 'Knovara.Desktop.exe'
Publish-Component 'Knovara.Api' 'runtime' 'Knovara.Api.exe'
Publish-Component 'Knovara.Cli' 'cli' 'knovara.exe'
Publish-Component 'Knovara.Mcp' 'mcp' 'knovara-mcp.exe'

Write-Host 'Bundling the embedding and reranking models...'
Copy-Item $ModelsDir (Join-Path $publish 'runtime\models') -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot 'register-mcp.ps1') (Join-Path $publish 'mcp\register-mcp.ps1') -Force

Write-Host 'Compiling the installer...'
$defines = @("/DAppVersion=$Version", "/DSourceDir=$publish", "/DOutputDir=$output")
if ($ControlPlaneUrl) { $defines += "/DControlPlaneUrl=$($ControlPlaneUrl.TrimEnd('/'))" }
# 2>&1 so the compiler's own error message is shown; without it a failure is just "exit code 1".
$compilerOutput = & $iscc @defines (Join-Path $PSScriptRoot 'knovara.iss') 2>&1
$compilerOutput | Where-Object { $_ -notmatch '^Parsing ' } | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed; see its message above.' }

$setup = Join-Path $output "Knovara-Setup-$Version.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host ''
Write-Host "Built:   $setup"
Write-Host ("Size:    {0:N1} MB" -f ((Get-Item $setup).Length / 1MB))
Write-Host "SHA-256: $hash"
Write-Host 'Upload it on the control plane dashboard (Downloads > Publish a new version), or distribute it directly - it works standalone, with no control plane at all.'
