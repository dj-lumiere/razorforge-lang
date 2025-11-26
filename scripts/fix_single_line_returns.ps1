# Simple script to add 'return' to single-line function bodies
# Specifically targets LLVM intrinsic calls and simple expressions

param(
    [string]$FilePath,
    [switch]$DryRun = $false
)

if (-not (Test-Path $FilePath)) {
    Write-Host "Error: File not found: $FilePath" -ForegroundColor Red
    exit 1
}

$content = Get-Content $FilePath -Raw
$originalContent = $content

# Counter
$replacements = 0

# Pattern 1: llvm_intrinsic calls without return
# Match:    llvm_intrinsic("...", ...)
# Replace:  return llvm_intrinsic("...", ...)
$pattern1 = '(?m)^(\s+)(llvm_intrinsic\([^)]+\))\s*$'
$replacement1 = '$1return $2'
$newContent = $content -creplace $pattern1, $replacement1
if ($newContent -ne $content) {
    $count = ([regex]::Matches($content, $pattern1)).Count
    $replacements += $count
    Write-Host "  Added 'return' to $count llvm_intrinsic calls" -ForegroundColor Green
    $content = $newContent
}

# Pattern 2: Simple method calls without return (but skip assignments and side-effects)
# Match lines like:    my.sin() / my.cos()
# But NOT:             me.field = value
$pattern2 = '(?m)^(\s+)(?!return\s+)(?!me\.\w+\s*=)(?!var\s+)(?!let\s+)(?!if\s+)(?!when\s+)(?!while\s+)(?!for\s+)(?!crash)(?!printf)(?!memory_)(?!heap_)(?!stack_)([a-zA-Z_][\w.()/*+\-\s]*[a-zA-Z0-9)])(\s*)$'
$replacement2 = '$1return $2$3'

# Only apply if it looks like an expression (contains operators or method calls)
$lines = $content -split "`n"
$newLines = @()
$inFunction = $false

foreach ($line in $lines) {
    # Track function boundaries
    if ($line -match 'routine\s+.*\)\s*->\s*\S+\s*\{') {
        $inFunction = $true
    }
    if ($line -match '^\}') {
        $inFunction = $false
    }

    # Check if this line needs return
    $needsReturn = $inFunction -and
                   $line -match '^\s+[^r]' -and  # Starts with whitespace, not 'return'
                   $line -notmatch '^\s*#' -and   # Not a comment
                   $line -notmatch '^\s*\}' -and  # Not a closing brace
                   $line -notmatch '^\s*(var|let|if|when|while|for)\s+' -and  # Not control flow
                   $line -notmatch '^\s+me\.\w+\s*=' -and  # Not field assignment
                   $line -notmatch '^\s+(crash|printf|memory_|heap_|stack_)' -and  # Not side-effect
                   ($line -match '[\(\)]' -or $line -match '[+\-*/]' -or $line -match '==|!=|<=|>=|<|>')  # Has operators/calls

    if ($needsReturn -and $line -notmatch '^\s+return\s+') {
        $line = $line -creplace '^(\s+)(.+)$', '$1return $2'
        $replacements++
    }

    $newLines += $line
}

$content = $newLines -join "`n"

# Show results
if ($replacements -gt 0) {
    Write-Host "`nTotal changes: $replacements" -ForegroundColor Cyan

    if (-not $DryRun) {
        $content | Set-Content $FilePath -NoNewline
        Write-Host "✓ Applied changes to: $FilePath" -ForegroundColor Green
    } else {
        Write-Host "! DRY RUN - No changes applied" -ForegroundColor Yellow
    }
} else {
    Write-Host "No changes needed for: $FilePath" -ForegroundColor DarkGray
}

exit 0
