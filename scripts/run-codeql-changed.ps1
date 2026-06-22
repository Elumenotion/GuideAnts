[CmdletBinding()]
param(
    [Parameter()]
    [string]$BaseRef = "origin/main",

    [Parameter()]
    [string]$CodeqlPath = "",

    [Parameter()]
    [switch]$IncludeWorkingTree
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-MergeBase {
    param([string]$Ref)

    $base = (& git merge-base $Ref HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($base)) {
        throw "Unable to resolve merge-base against '$Ref'. Pass -BaseRef with a valid ref."
    }

    return ([string]$base).Trim()
}

function Normalize-PathForMatch {
    param([string]$Path)

    $value = [string]$Path
    if ([string]::IsNullOrWhiteSpace($value)) {
        return ""
    }

    $value = $value -replace '\\', '/'
    $value = $value -replace '^file:/+', ''
    $value = $value -replace '^[A-Za-z]:/', ''
    $value = $value.Trim()
    $value = $value.TrimStart('.')
    $value = $value.TrimStart('/')
    return $value
}

function Get-ChangedFiles {
    param(
        [string]$Ref,
        [switch]$WithWorkingTree
    )

    $mergeBase = Get-MergeBase -Ref $Ref
    $files = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)

    $commitDiff = & git diff --name-only --diff-filter=ACMR "$mergeBase...HEAD"
    if ($LASTEXITCODE -ne 0) {
        throw "git diff failed while computing changed files."
    }
    foreach ($line in $commitDiff) {
        $item = ([string]$line).Trim()
        if (-not [string]::IsNullOrWhiteSpace($item)) {
            $files.Add($item) | Out-Null
        }
    }

    if ($WithWorkingTree) {
        $unstaged = & git diff --name-only --diff-filter=ACMR
        if ($LASTEXITCODE -ne 0) {
            throw "git diff (unstaged) failed."
        }
        foreach ($line in $unstaged) {
            $item = ([string]$line).Trim()
            if (-not [string]::IsNullOrWhiteSpace($item)) {
                $files.Add($item) | Out-Null
            }
        }

        $staged = & git diff --name-only --cached --diff-filter=ACMR
        if ($LASTEXITCODE -ne 0) {
            throw "git diff (staged) failed."
        }
        foreach ($line in $staged) {
            $item = ([string]$line).Trim()
            if (-not [string]::IsNullOrWhiteSpace($item)) {
                $files.Add($item) | Out-Null
            }
        }
    }

    return @($files)
}

function Get-LanguageSetForFiles {
    param([string[]]$Files)

    $langs = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($path in $Files) {
        $normalized = ($path -replace '\\', '/')
        $ext = [System.IO.Path]::GetExtension($normalized).ToLowerInvariant()

        switch ($ext) {
            ".cs" { $langs.Add("csharp") | Out-Null; continue }
            ".py" { $langs.Add("python") | Out-Null; continue }
            ".js" { $langs.Add("javascript") | Out-Null; continue }
            ".jsx" { $langs.Add("javascript") | Out-Null; continue }
            ".ts" { $langs.Add("javascript") | Out-Null; continue }
            ".tsx" { $langs.Add("javascript") | Out-Null; continue }
            ".mjs" { $langs.Add("javascript") | Out-Null; continue }
            ".cjs" { $langs.Add("javascript") | Out-Null; continue }
        }

        if ($normalized -like "src/server/*.csproj" -or
            $normalized -like "src/server/*.sln" -or
            $normalized -like "src/server/**/*.csproj" -or
            $normalized -like "src/server/**/*.props" -or
            $normalized -like "src/server/**/*.targets" -or
            $normalized -like "src/server/**/Directory.Build.*") {
            $langs.Add("csharp") | Out-Null
            continue
        }

        if ($normalized -like "src/client/**/package*.json" -or
            $normalized -like "src/client/**/tsconfig*.json" -or
            $normalized -like "src/client/**/vite.config.*" -or
            $normalized -like "src/client/**/vitest.config.*") {
            $langs.Add("javascript") | Out-Null
            continue
        }
    }

    return @($langs | Sort-Object)
}

function Get-ChangedPathCandidates {
    param([string[]]$ChangedFiles)

    $set = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $ChangedFiles) {
        $n = Normalize-PathForMatch -Path $path
        if ([string]::IsNullOrWhiteSpace($n)) {
            continue
        }

        $set.Add($n) | Out-Null

        if ($n.StartsWith("src/client/", [System.StringComparison]::OrdinalIgnoreCase)) {
            $set.Add($n.Substring("src/client/".Length)) | Out-Null
        }
        if ($n.StartsWith("src/server/", [System.StringComparison]::OrdinalIgnoreCase)) {
            $set.Add($n.Substring("src/server/".Length)) | Out-Null
        }
    }

    return @($set)
}

function Test-RowMatchesChangedFile {
    param(
        [string]$SarifFilePath,
        [string[]]$Candidates
    )

    $rowPath = Normalize-PathForMatch -Path $SarifFilePath
    if ([string]::IsNullOrWhiteSpace($rowPath)) {
        return $false
    }

    foreach ($candidate in $Candidates) {
        if ($rowPath.Equals($candidate, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
        if ($rowPath.EndsWith("/$candidate", [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

$repoRoot = (Get-Location).ProviderPath
$runScript = Join-Path $repoRoot "scripts/run-codeql-sln-triage.ps1"
$triageScript = Join-Path $repoRoot "scripts/triage-codeql-sarif.ps1"

if (-not (Test-Path -LiteralPath $runScript)) {
    throw "Missing script: $runScript"
}
if (-not (Test-Path -LiteralPath $triageScript)) {
    throw "Missing script: $triageScript"
}

$changedFiles = Get-ChangedFiles -Ref $BaseRef -WithWorkingTree:$IncludeWorkingTree

if (@($changedFiles).Count -eq 0) {
    Write-Host "No changed files vs $BaseRef. Skipping CodeQL."
    exit 0
}

$languages = Get-LanguageSetForFiles -Files $changedFiles
if (@($languages).Count -eq 0) {
    Write-Host "No C#/Python/JS file changes detected. Skipping CodeQL."
    exit 0
}

Write-Host "Changed files: $(@($changedFiles).Count)"
Write-Host "Detected languages: $($languages -join ', ')"

foreach ($lang in $languages) {
    Write-Host ""
    Write-Host "=== Running CodeQL for changed language: $lang ==="
    $runArgs = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $runScript
    )
    if (-not [string]::IsNullOrWhiteSpace($CodeqlPath)) {
        $runArgs += @("-CodeqlPath", $CodeqlPath)
    }
    $runArgs += @(
        "-Languages", $lang,
        "-AllowPartialLanguages",
        "-SkipGitHubParityCheck",
        "-CleanCodeqlOutputs",
        "-CsvPath", ".codeql/triage-$lang.csv",
        "-ManifestPath", ".codeql/run-manifest-$lang.json"
    )

    & powershell @runArgs

    if ($LASTEXITCODE -ne 0) {
        throw "CodeQL run failed for language: $lang"
    }
}

$allRows = New-Object System.Collections.Generic.List[object]
$index = 1

foreach ($lang in $languages) {
    $sarif = Join-Path $repoRoot ".codeql/results-$lang.sarif"
    if (-not (Test-Path -LiteralPath $sarif)) {
        Write-Warning "Missing SARIF for ${lang}: $sarif"
        continue
    }

    $rows = & powershell -NoProfile -ExecutionPolicy Bypass -File $triageScript `
        -SarifPath $sarif `
        -Language $lang `
        -PassThru

    foreach ($row in @($rows)) {
        $allRows.Add([PSCustomObject]@{
                Index            = $index
                Language         = [string]$row.Language
                RuleId           = [string]$row.RuleId
                Level            = [string]$row.Level
                SecuritySeverity = [string]$row.SecuritySeverity
                Precision        = [string]$row.Precision
                File             = [string]$row.File
                Line             = [int]$row.Line
                Message          = [string]$row.Message
            }) | Out-Null
        $index++
    }
}

$allOutput = Join-Path $repoRoot ".codeql/triage-changed-languages.csv"
$allRows | Export-Csv -LiteralPath $allOutput -NoTypeInformation -Encoding utf8

$candidates = Get-ChangedPathCandidates -ChangedFiles $changedFiles
$changedRows = @(
    $allRows | Where-Object {
        Test-RowMatchesChangedFile -SarifFilePath ([string]$_.File) -Candidates $candidates
    }
)

$changedOutput = Join-Path $repoRoot ".codeql/triage-changed-files.csv"
$changedRows | Export-Csv -LiteralPath $changedOutput -NoTypeInformation -Encoding utf8

Write-Host ""
Write-Host "=== Changed-scope summary ==="
Write-Host "All findings (changed languages): $($allRows.Count)"
Write-Host "Findings touching changed files: $($changedRows.Count)"
Write-Host "Output (changed languages): $allOutput"
Write-Host "Output (changed files): $changedOutput"

if ($changedRows.Count -gt 0) {
    Write-Host ""
    Write-Host "Top changed-file findings by rule:"
    $changedRows | Group-Object RuleId | Sort-Object Count -Descending |
        Select-Object -First 10 |
        ForEach-Object { Write-Host ("- {0}: {1}" -f $_.Name, $_.Count) }
}
