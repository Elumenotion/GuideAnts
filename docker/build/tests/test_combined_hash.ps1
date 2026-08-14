$ErrorActionPreference = 'Stop'

$lib = Join-Path $PSScriptRoot '..\lib\combined-hash.ps1'
. $lib

function Assert-Equal {
    param($Actual, $Expected, $Message)
    if ($Actual -ne $Expected) {
        throw "$Message`nExpected: $Expected`nActual:   $Actual"
    }
}

function Assert-True {
    param($Condition, $Message)
    if (-not $Condition) {
        throw $Message
    }
}

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("ga-hash-" + [guid]::NewGuid().ToString('n'))
$worktreeA = Join-Path $scratch 'GuideAnts'
$worktreeB = Join-Path $scratch 'GuideAnts-qwen38-27b-gguf'

try {
    foreach ($root in @($worktreeA, $worktreeB)) {
        $dir = Join-Path $root 'docker\build\guideants-ai'
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $dir 'asr-requirements.txt'), "numpy==2.0.0`n")
        [System.IO.File]::WriteAllText((Join-Path $dir 'emb-requirements.txt'), "tokenizers==0.21.0`n")
    }

    $hashA = Get-CombinedHash -RelativeTo $worktreeA -Paths @(
        (Join-Path $worktreeA 'docker\build\guideants-ai\emb-requirements.txt'),
        (Join-Path $worktreeA 'docker\build\guideants-ai\asr-requirements.txt')
    )
    $hashB = Get-CombinedHash -RelativeTo $worktreeB -Paths @(
        (Join-Path $worktreeB 'docker\build\guideants-ai\asr-requirements.txt'),
        (Join-Path $worktreeB 'docker\build\guideants-ai\emb-requirements.txt')
    )

    $relA = Get-StableRepoRelativePath -Path (Join-Path $worktreeA 'docker\build\guideants-ai\asr-requirements.txt') -RelativeTo $worktreeA
    $relB = Get-StableRepoRelativePath -Path (Join-Path $worktreeB 'docker\build\guideants-ai\asr-requirements.txt') -RelativeTo $worktreeB
    Assert-Equal $relA 'docker/build/guideants-ai/asr-requirements.txt' 'Repo-relative keys must use forward slashes and omit the checkout root'
    Assert-Equal $relA $relB 'The same file in two worktrees must use the same relative key'
    Assert-Equal $hashA $hashB 'Identical inputs in two worktrees must produce the same deps hash'

    [System.IO.File]::WriteAllText((Join-Path $worktreeB 'docker\build\guideants-ai\asr-requirements.txt'), "numpy==2.0.1`n")
    $hashBChanged = Get-CombinedHash -RelativeTo $worktreeB -Paths @(
        (Join-Path $worktreeB 'docker\build\guideants-ai\asr-requirements.txt'),
        (Join-Path $worktreeB 'docker\build\guideants-ai\emb-requirements.txt')
    )
    Assert-True ($hashA -ne $hashBChanged) 'Content change must change the combined hash'

    $outside = Join-Path $scratch 'outside.txt'
    [System.IO.File]::WriteAllText($outside, 'nope')
    $threw = $false
    try {
        Get-CombinedHash -RelativeTo $worktreeA -Paths @($outside) | Out-Null
    }
    catch {
        $threw = $true
    }
    Assert-True $threw 'Files outside the repo root must not be hashed'

    Write-Host 'test_combined_hash.ps1: passed'
}
finally {
    if (Test-Path $scratch) {
        Remove-Item -Path $scratch -Recurse -Force
    }
}
