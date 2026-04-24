param(
    [string]$SourceRoot = "D:\Elumenotion\repos\waterfall-major-refactor",
    [string]$TargetRoot = "D:\repos\GuideAnts",
    [string]$OutputPath = "D:\repos\GuideAnts\waterfall-major-refactor-files-not-in-guideants.txt"
)

$ErrorActionPreference = "Stop"

$excludeDirectories = @(
    ".git",
    ".vs",
    "node_modules",
    "bin",
    "obj",
    "dist"
)

function Get-RelativeFileSet {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath
    )

    $resolvedRoot = (Resolve-Path -LiteralPath $RootPath).ProviderPath.TrimEnd('\')

    Get-ChildItem -LiteralPath $resolvedRoot -Recurse -File |
        Where-Object {
            $segments = $_.FullName.Substring($resolvedRoot.Length).TrimStart('\').Split('\')
            -not ($segments | Where-Object { $excludeDirectories -contains $_ })
        } |
        ForEach-Object {
            $_.FullName.Substring($resolvedRoot.Length).TrimStart('\').Replace('\', '/')
        } |
        Sort-Object -Unique
}

$sourceFiles = Get-RelativeFileSet -RootPath $SourceRoot
$targetFiles = Get-RelativeFileSet -RootPath $TargetRoot
$targetLookup = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

foreach ($file in $targetFiles) {
    [void]$targetLookup.Add($file)
}

$missingFromTarget = foreach ($file in $sourceFiles) {
    if (-not $targetLookup.Contains($file)) {
        $file
    }
}

$lines = @(
    "Files present in source but missing from target",
    "Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')",
    "Source: $SourceRoot",
    "Target: $TargetRoot",
    "Excluded directories: $($excludeDirectories -join ', ')",
    "Missing file count: $($missingFromTarget.Count)",
    ""
)

$lines += $missingFromTarget

$outputDirectory = Split-Path -Path $OutputPath -Parent
if (-not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

Set-Content -LiteralPath $OutputPath -Value $lines -Encoding UTF8

Write-Host "Wrote $($missingFromTarget.Count) missing file path(s) to $OutputPath"
