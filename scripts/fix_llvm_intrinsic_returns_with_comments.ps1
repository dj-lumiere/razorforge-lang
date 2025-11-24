# Script to add 'return' to llvm_intrinsic calls that have comments after them
# Targets pattern: llvm_intrinsic(...) # comment

param(
    [string]$Path = "..\stdlib",
    [switch]$DryRun = $false
)

if (-not (Test-Path $Path)) {
    Write-Host "Error: Path not found: $Path" -ForegroundColor Red
    exit 1
}

$rfFiles = Get-ChildItem -Path $Path -Filter "*.rf" -Recurse
$totalFiles = $rfFiles.Count
$filesChanged = 0
$totalReplacements = 0

Write-Host "Found $totalFiles .rf files to process`n" -ForegroundColor Cyan

foreach ($file in $rfFiles) {
    $content = Get-Content $file.FullName -Raw
    $originalContent = $content

    # Pattern: Match lines with llvm_intrinsic(...) followed by optional comment
    # Must have whitespace at start, then llvm_intrinsic, then either end of line or comment
    # Must NOT already have 'return' before it
    $pattern = '(?m)^(\s+)(?!return\s+)(llvm_intrinsic\([^)]+\))(\s*(?:#.*)?)$'
    $replacement = '$1return $2$3'

    $newContent = $content -creplace $pattern, $replacement

    if ($newContent -ne $content) {
        $count = ([regex]::Matches($content, $pattern)).Count
        $filesChanged++
        $totalReplacements += $count

        Write-Host "  $($file.Name): " -NoNewline -ForegroundColor Yellow
        Write-Host "$count changes" -ForegroundColor Green

        if (-not $DryRun) {
            $newContent | Set-Content $file.FullName -NoNewline
        }
    }
}

Write-Host "`n==================================" -ForegroundColor Cyan
Write-Host "Summary:" -ForegroundColor Cyan
Write-Host "  Files processed: $totalFiles" -ForegroundColor White
Write-Host "  Files changed: $filesChanged" -ForegroundColor Yellow
Write-Host "  Total replacements: $totalReplacements" -ForegroundColor Green

if ($DryRun) {
    Write-Host "`n! DRY RUN - No changes applied" -ForegroundColor Yellow
    Write-Host "  Run without -DryRun to apply changes" -ForegroundColor DarkGray
} else {
    Write-Host "`n✓ Changes applied" -ForegroundColor Green
}

exit 0
