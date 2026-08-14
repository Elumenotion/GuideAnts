# Content-addressed hash of files, keyed by repo-relative paths with forward slashes.
# Absolute checkout paths must not affect the digest: git worktrees and relocated
# clones have to share Docker image tags for the same inputs.

function Get-StableRepoRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$RelativeTo
    )

    $full = [System.IO.Path]::GetFullPath($Path).Replace('\', '/')
    $root = [System.IO.Path]::GetFullPath($RelativeTo).Replace('\', '/').TrimEnd('/')

    if ([string]::Equals($full, $root, [StringComparison]::OrdinalIgnoreCase)) {
        return '.'
    }

    $prefix = $root + '/'
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Hash input '$Path' is not under repo root '$RelativeTo'"
    }

    return $full.Substring($prefix.Length)
}

function Get-CombinedHash {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Paths,

        [Parameter(Mandatory = $true)]
        [string]$RelativeTo
    )

    if ($Paths.Count -eq 0) {
        throw "Hash input file list is empty"
    }

    $lineList = [System.Collections.Generic.List[string]]::new()
    foreach ($path in $Paths) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Hash input file not found: $path"
        }

        $full = (Resolve-Path -LiteralPath $path).Path
        $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
        $relative = Get-StableRepoRelativePath -Path $full -RelativeTo $RelativeTo
        $lineList.Add("$relative|$hash")
    }

    $arr = $lineList.ToArray()
    [Array]::Sort($arr, [StringComparer]::Ordinal)
    $joined = [string]::Join("`n", $arr)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($joined)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $digest = $sha.ComputeHash($bytes)
    }
    finally {
        $sha.Dispose()
    }

    -join ($digest | ForEach-Object { $_.ToString('x2') })
}
