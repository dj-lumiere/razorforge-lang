# PowerShell script to add explicit return statements to RazorForge .rf files
# This fixes the implicit return issue by making all returns explicit

param(
    [string]$Path = "..\stdlib",
    [switch]$DryRun = $false,
    [switch]$Verbose = $false
)

$ErrorActionPreference = "Stop"

# Statistics
$script:filesProcessed = 0
$script:functionsFixed = 0
$script:linesChanged = 0

function Write-Log {
    param([string]$Message, [string]$Color = "White")
    if ($Verbose) {
        Write-Host $Message -ForegroundColor $Color
    }
}

function Should-Add-Return {
    param(
        [string]$Line,
        [string]$NextLine,
        [ref]$InFunction
    )

    # Track if we're in a function
    if ($Line -match 'recipe\s+\S+.*\)\s*->\s*\S+\s*\{') {
        $InFunction.Value = $true
    }

    # Skip if not in a function
    if (-not $InFunction.Value) {
        return $false
    }

    # Reset when we hit a closing brace at function level
    if ($Line -match '^\}' -or $Line -match '^recipe\s+') {
        $InFunction.Value = $false
        return $false
    }

    # Skip if line already starts with 'return'
    if ($Line -match '^\s+return\s+') {
        return $false
    }

    # Skip if it's a comment
    if ($Line -match '^\s*#') {
        return $false
    }

    # Skip if it's empty
    if ($Line -match '^\s*$') {
        return $false
    }

    # Skip if it's a closing brace
    if ($Line -match '^\s*\}') {
        return $false
    }

    # Skip if next line is NOT a closing brace (not the last statement)
    if ($NextLine -notmatch '^\s*\}') {
        return $false
    }

    # Skip if it's a variable declaration (var/let without immediate return)
    if ($Line -match '^\s+(var|let)\s+\w+:') {
        return $false
    }

    # Skip if it's a control flow statement opening
    if ($Line -match '^\s+(if|when|while|for|loop)\s+.*\{') {
        return $false
    }

    # Skip if it's a when pattern arm (contains =>)
    if ($Line -match '^\s+.+\s*=>\s*') {
        return $false
    }

    # Skip if it's a field/property access assignment (me.field = value)
    if ($Line -match '^\s+me\.\w+\s*[+\-*/]?=\s+') {
        return $false
    }

    # Skip if it's a simple increment/decrement
    if ($Line -match '^\s+\w+\s*[+\-]=\s*\d+\s*$') {
        return $false
    }

    # Skip function calls that are clearly side-effects (crash!, printf, etc.)
    if ($Line -match '^\s+(crash|printf|show|display|memory_copy|memory_fill|memory_zero|heap_free)!?\(') {
        return $false
    }

    # If we got here, it's likely a return value
    return $true
}

function Process-File {
    param([string]$FilePath)

    Write-Log "Processing: $FilePath" -Color Cyan

    $lines = Get-Content $FilePath -Encoding UTF8
    $newLines = @()
    $fileChanged = $false
    $functionsFixedInFile = 0
    $inFunction = $false

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $currentLine = $lines[$i]
        $nextLine = if ($i + 1 -lt $lines.Count) { $lines[$i + 1] } else { "" }

        # Check if we should add return to this line
        if (Should-Add-Return -Line $currentLine -NextLine $nextLine -InFunction ([ref]$inFunction)) {
            # Extract indentation
            if ($currentLine -match '^(\s+)(.+)$') {
                $indent = $Matches[1]
                $code = $Matches[2]

                # Add return statement
                $newLine = "${indent}return $code"
                $newLines += $newLine

                Write-Log "  Line $($i+1): Added 'return'" -Color Green
                Write-Log "    Before: $currentLine" -Color DarkGray
                Write-Log "    After:  $newLine" -Color DarkGray

                $fileChanged = $true
                $functionsFixedInFile++
                $script:linesChanged++
            } else {
                $newLines += $currentLine
            }
        } else {
            $newLines += $currentLine
        }
    }

    # Write changes if not dry run
    if ($fileChanged) {
        if (-not $DryRun) {
            $newLines | Set-Content $FilePath -Encoding UTF8
            Write-Host "✓ Fixed $functionsFixedInFile functions in: $FilePath" -ForegroundColor Green
        } else {
            Write-Host "! Would fix $functionsFixedInFile returns in: $FilePath (DRY RUN)" -ForegroundColor Yellow
        }

        $script:functionsFixed += $functionsFixedInFile
        $script:filesProcessed++
    } else {
        Write-Log "  No changes needed" -Color DarkGray
    }
}

function Process-Directory {
    param([string]$DirPath)

    if (-not (Test-Path $DirPath)) {
        Write-Host "Error: Path not found: $DirPath" -ForegroundColor Red
        return
    }

    Write-Host "`n========================================" -ForegroundColor Magenta
    Write-Host "  RazorForge Explicit Return Fixer" -ForegroundColor Magenta
    Write-Host "========================================`n" -ForegroundColor Magenta

    if ($DryRun) {
        Write-Host "DRY RUN MODE - No files will be modified`n" -ForegroundColor Yellow
    }

    # Find all .rf files recursively
    $rfFiles = Get-ChildItem -Path $DirPath -Filter "*.rf" -Recurse -File

    Write-Host "Found $($rfFiles.Count) .rf files to process`n" -ForegroundColor Cyan

    foreach ($file in $rfFiles) {
        Process-File -FilePath $file.FullName
    }

    # Print summary
    Write-Host "`n========================================" -ForegroundColor Magenta
    Write-Host "  Summary" -ForegroundColor Magenta
    Write-Host "========================================" -ForegroundColor Magenta
    Write-Host "Files processed:    $script:filesProcessed" -ForegroundColor White
    Write-Host "Returns added:      $script:functionsFixed" -ForegroundColor Green
    Write-Host "Lines changed:      $script:linesChanged" -ForegroundColor Green

    if ($DryRun) {
        Write-Host "`nRun without -DryRun to apply changes" -ForegroundColor Yellow
    } else {
        Write-Host "`nAll changes applied successfully!" -ForegroundColor Green
    }
}

# Main execution
try {
    $fullPath = Join-Path $PSScriptRoot $Path | Resolve-Path
    Process-Directory -DirPath $fullPath
} catch {
    Write-Host "`nError: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor Red
    exit 1
}
