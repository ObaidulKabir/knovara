<#
.SYNOPSIS
  Registers the installed knovara-mcp server with Claude Code, so Claude can ground answers in your
  Knovara documents right after installing. Safe to re-run any time (for example after moving the
  install, or if you skipped this during setup).

.DESCRIPTION
  Prefers `claude mcp add` (the Claude Code CLI already does the right thing for its own config and for
  Claude Desktop's `claude_desktop_config.json`); if `claude` isn't on PATH, prints the equivalent JSON
  to add by hand. The server is registered with KNOVARA_TENANT_ID=local, matching the CLI's and the
  desktop app's own default tenant for the local runtime - change it with --env if you keep several
  tenants on one runtime.
#>
param(
    [string] $McpExePath = (Join-Path $PSScriptRoot 'knovara-mcp.exe'),
    [string] $TenantId = 'local'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $McpExePath)) {
    Write-Error "knovara-mcp.exe was not found at '$McpExePath'. Pass -McpExePath <path> if Knovara was installed somewhere else."
    exit 1
}
$McpExePath = (Resolve-Path $McpExePath).Path

$claude = Get-Command claude -ErrorAction SilentlyContinue
if (-not $claude) {
    Write-Host "The 'claude' CLI was not found on PATH, so this could not register automatically." -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'Add this to your MCP client config by hand (Claude Desktop: %APPDATA%\Claude\claude_desktop_config.json):'
    Write-Host ''
    Write-Host (@{
        mcpServers = @{ knovara = @{ command = $McpExePath; env = @{ KNOVARA_TENANT_ID = $TenantId } } }
    } | ConvertTo-Json -Depth 5)
    exit 0
}

Write-Host "Registering knovara-mcp with Claude Code ($McpExePath)..."
& claude mcp add knovara --env "KNOVARA_TENANT_ID=$TenantId" -- "$McpExePath"
if ($LASTEXITCODE -ne 0) {
    Write-Host "'claude mcp add' failed; see its message above. You can add it by hand instead - see README.md \"MCP server\"." -ForegroundColor Yellow
    exit $LASTEXITCODE
}

Write-Host 'Done. Restart Claude Code (or Claude Desktop) if it was already running.'
