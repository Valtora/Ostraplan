<#
  sync-memory.ps1 - sync THIS repo's Claude memory with the live keyed store.
  Deployed to <repo>/.claude/sync-memory.ps1. Self-contained; no dependency on claude-config.
    Export (default): live store -> repo (.claude/memory). Used by the pre-commit hook.
    Import:           repo (.claude/memory) -> live store. Used after a fresh clone.
#>
param([ValidateSet('Export','Import')][string]$Mode='Export')
$ErrorActionPreference = 'Stop'
$RepoRoot     = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path.TrimEnd('\')
$repoMem      = Join-Path $PSScriptRoot 'memory'
$projectsRoot = Join-Path $env:USERPROFILE '.claude\projects'
# Claude mangles the cwd into the key by replacing every non-alphanumeric char with '-'
$key          = ($RepoRoot -replace '[^A-Za-z0-9]','-')

# Claude sometimes stores the drive letter in a different case; match case-insensitively.
$liveDir = $null
if(Test-Path $projectsRoot){
  $liveDir = Get-ChildItem $projectsRoot -Directory -ErrorAction SilentlyContinue |
             Where-Object { $_.Name -ieq $key } | Select-Object -First 1
}
$liveMem = if($liveDir){ Join-Path $liveDir.FullName 'memory' } else { Join-Path $projectsRoot ("{0}\memory" -f $key) }

if($Mode -eq 'Export'){
  if(-not (Test-Path $liveMem)){ Write-Output "no live memory for this repo yet"; exit 0 }
  New-Item -ItemType Directory -Force -Path $repoMem | Out-Null
  Get-ChildItem $repoMem -Filter *.md -ErrorAction SilentlyContinue | Remove-Item -Force
  Copy-Item (Join-Path $liveMem '*.md') $repoMem -Force -ErrorAction SilentlyContinue
  Write-Output ("exported {0} file(s) -> .claude/memory" -f (Get-ChildItem $repoMem -Filter *.md -ErrorAction SilentlyContinue).Count)
} else {
  if(-not (Test-Path $repoMem)){ Write-Output "no repo memory to import"; exit 0 }
  New-Item -ItemType Directory -Force -Path $liveMem | Out-Null
  Copy-Item (Join-Path $repoMem '*.md') $liveMem -Force -ErrorAction SilentlyContinue
  Write-Output ("imported -> {0}" -f $liveMem)
}
