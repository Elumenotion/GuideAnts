Set-Location (Join-Path $PSScriptRoot '..')
$merged = New-Object System.Collections.Generic.List[object]
$index = 1
foreach ($lang in @('csharp', 'python', 'javascript')) {
    $sarif = ".codeql/results-$lang.sarif"
    if (-not (Test-Path -LiteralPath $sarif)) {
        Write-Warning "Missing $sarif"
        continue
    }
    $tmp = Join-Path $env:TEMP "codeql-merge-$lang-$PID.csv"
    & powershell -NoProfile -ExecutionPolicy Bypass -File scripts/triage-codeql-sarif.ps1 `
        -SarifPath $sarif -Language $lang -ExportCsv $tmp | Out-Null
    foreach ($row in Import-Csv -LiteralPath $tmp) {
        $merged.Add([PSCustomObject]@{
                Index            = $index
                Language         = $lang
                RuleId           = $row.RuleId
                Level            = $row.Level
                SecuritySeverity = $row.SecuritySeverity
                Precision        = $row.Precision
                File             = $row.File
                Line             = $row.Line
                Message          = $row.Message
            })
        $index++
    }
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
}
$merged | Export-Csv -LiteralPath '.codeql/triage-merged.csv' -NoTypeInformation -Encoding utf8
Write-Host "=== triage.csv by language ==="
$merged | Group-Object Language | Sort-Object Name | Format-Table Name, Count -AutoSize
Write-Host "total rows: $($merged.Count)"
