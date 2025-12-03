# Merge all wiki markdown files into one big file

$wikiDir = "wiki"
$outputFile = "MERGED_WIKI.md"

# Get all markdown files from wiki directory
$mdFiles = Get-ChildItem -Path $wikiDir -Filter "*.md" | Sort-Object Name

# Create/clear output file
"# RazorForge Wiki - Merged Documentation`n" | Out-File -FilePath $outputFile -Encoding UTF8
"Generated on: $( Get-Date -Format 'yyyy-MM-dd HH:mm:ss' )`n" | Out-File -FilePath $outputFile -Append -Encoding UTF8
"---`n" | Out-File -FilePath $outputFile -Append -Encoding UTF8

# Counter for progress
$count = 0
$total = $mdFiles.Count

Write-Host "Merging $total markdown files from wiki directory..." -ForegroundColor Green

foreach ($file in $mdFiles)
{
    $count++
    Write-Host "[$count/$total] Processing: $( $file.Name )" -ForegroundColor Cyan

    # Add file separator with filename as heading
    "`n`n" | Out-File -FilePath $outputFile -Append -Encoding UTF8 -NoNewline
    "# ==========================================`n" | Out-File -FilePath $outputFile -Append -Encoding UTF8
    "# FILE: $( $file.Name )`n" | Out-File -FilePath $outputFile -Append -Encoding UTF8
    "# ==========================================`n`n" | Out-File -FilePath $outputFile -Append -Encoding UTF8

    # Read and append file content
    $content = Get-Content -Path $file.FullName -Raw -Encoding UTF8
    $content | Out-File -FilePath $outputFile -Append -Encoding UTF8 -NoNewline

    # Add separator
    "`n`n---`n" | Out-File -FilePath $outputFile -Append -Encoding UTF8
}

Write-Host "`nMerge complete! Output file: $outputFile" -ForegroundColor Green
Write-Host "Total files merged: $total" -ForegroundColor Green
