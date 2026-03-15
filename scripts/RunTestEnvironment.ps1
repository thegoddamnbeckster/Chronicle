<#
.SYNOPSIS
    Starts the Chronicle development environment (API + Web frontend).

.DESCRIPTION
    Delegates to the RunTestEnvironment.ps1 in the currently active development
    worktree. Update $ActiveWorktree below when switching worktrees.

.PARAMETER ApiOnly
    Start only the API, not the frontend.

.PARAMETER WebOnly
    Start only the frontend, not the API.
#>
param(
    [switch]$ApiOnly,
    [switch]$WebOnly
)

# ── Active worktree ───────────────────────────────────────────────────────────
# Update this path when switching to a new worktree.
$ActiveWorktree = "W:\Scripts\Chronicle\.claude\worktrees\frosty-allen"

$Script = Join-Path $ActiveWorktree "scripts\RunTestEnvironment.ps1"

if (-not (Test-Path $Script)) {
    Write-Error "Worktree script not found at: $Script`nUpdate `$ActiveWorktree in this file."
    exit 1
}

& $Script -ApiOnly:$ApiOnly -WebOnly:$WebOnly
