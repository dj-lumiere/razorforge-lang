# Simple script to add 'return' ONLY to llvm_intrinsic calls that are alone on one line
# Very conservative - only targets obvious cases

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

    # Pattern: Match lines that are ONLY llvm_intrinsic(...) with no return
    # Must have whitespace at start, then llvm_intrinsic, then end of line
    # Must NOT already have 'return' before it
    $pattern = '(?m)^(\s+)(?!return\s+)(llvm_intrinsic\([^)]+\))\s*$'
    $replacement = '$1return $2'

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
