[CmdletBinding()]
param(
    [Parameter()]
    [string]$RepoRoot = (Get-Location).Path,

    [Parameter()]
    [switch]$DryRun,

    [Parameter()]
    [string]$LogPath,

    [Parameter()]
    [bool]$FailOnCollision = $true,

    [Parameter()]
    [bool]$FailOnResidual = $true
    ,
    [Parameter()]
    [string[]]$ExcludeDirectoryNames = @(
        '.git', '.vs', 'obj', 'bin', 'node_modules', 'dist', 'dist-browser',
        'coverage', 'artifacts', '.tmp', 'TestResults', 'volumes'
    )
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ProtectedToken = 'InvocationWaterfall'
$script:ProtectedPlaceholder = "__INVOCATION_WATERFALL__{0}__" -f ([Guid]::NewGuid().ToString('N'))
$script:ReplacementPairs = @(
    @{ From = 'Waterfall'; To = 'GuideAnts' },
    @{ From = 'waterfall'; To = 'guideants' },
    @{ From = 'WATERFALL'; To = 'GUIDEANTS' }
)

$script:BinaryExtensions = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
@(
    '.7z', '.a', '.apk', '.avif', '.bin', '.bmp', '.bz2', '.cab', '.class', '.cur',
    '.dat', '.db', '.dll', '.dmg', '.doc', '.docm', '.docx', '.dylib', '.eot', '.exe',
    '.flac', '.gif', '.gz', '.heic', '.heif', '.icns', '.ico', '.iso', '.jar', '.jpeg',
    '.jpg', '.lib', '.lock', '.m4a', '.mov', '.mp3', '.mp4', '.msi', '.o', '.obj', '.otf',
    '.pdf', '.png', '.ppt', '.pptm', '.pptx', '.psd', '.rar', '.so', '.sqlite', '.svgz',
    '.tar', '.tif', '.tiff', '.ttf', '.wav', '.webm', '.webp', '.woff', '.woff2', '.xls',
    '.xlsb', '.xlsm', '.xlsx', '.zip'
) | ForEach-Object { [void]$script:BinaryExtensions.Add($_) }

$script:RepoRootResolved = $null
$script:GitPath = $null
$script:GitPrefix = $null
$script:LogPathResolved = $null
$script:SelfPath = $null
$script:ExcludedDirectoryNameSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($name in $ExcludeDirectoryNames) {
    if (-not [string]::IsNullOrWhiteSpace($name)) {
        [void]$script:ExcludedDirectoryNameSet.Add($name.Trim())
    }
}

if ($MyInvocation.MyCommand.Path) {
    $script:SelfPath = [System.IO.Path]::GetFullPath($MyInvocation.MyCommand.Path)
}

function Convert-ToSingleQuotedLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return "'" + $Value.Replace("'", "''") + "'"
}

function Normalize-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path)
}

function Write-Log {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [Parameter()][ValidateSet('INFO', 'WARN', 'ERROR')][string]$Level = 'INFO'
    )

    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'
    $line = "[{0}][{1}] {2}" -f $timestamp, $Level, $Message
    Write-Host $line
    if ($script:LogPathResolved) {
        Add-Content -LiteralPath $script:LogPathResolved -Value $line
    }
}

function Is-UnderGitPath {
    param([Parameter(Mandatory = $true)][string]$FullPath)
    if ($FullPath.Equals($script:GitPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }
    return $FullPath.StartsWith($script:GitPrefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Is-OperationalExclusionPath {
    param([Parameter(Mandatory = $true)][string]$FullPath)

    if (Is-UnderGitPath -FullPath $FullPath) {
        return $true
    }

    if ($script:SelfPath -and $FullPath.Equals($script:SelfPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    if ($script:LogPathResolved -and $FullPath.Equals($script:LogPathResolved, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    return $false
}

function Is-ExcludedDirectoryName {
    param([Parameter(Mandatory = $true)][string]$Name)
    return $script:ExcludedDirectoryNameSet.Contains($Name)
}

function Count-ExactProtectedToken {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)
    return [System.Text.RegularExpressions.Regex]::Matches(
        $Text,
        [System.Text.RegularExpressions.Regex]::Escape($script:ProtectedToken)
    ).Count
}

function Get-WaterfallCountsExcludingProtected {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)

    $allowed = Count-ExactProtectedToken -Text $Text
    $withoutProtected = if ($allowed -gt 0) { $Text.Replace($script:ProtectedToken, '') } else { $Text }
    $remaining = [System.Text.RegularExpressions.Regex]::Matches(
        $withoutProtected,
        'waterfall',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    ).Count

    return [pscustomobject]@{
        Allowed = $allowed
        Remaining = $remaining
    }
}

function Apply-ProtectedAwareReplacement {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)

    $protectedCount = Count-ExactProtectedToken -Text $Text
    $working = if ($protectedCount -gt 0) {
        $Text.Replace($script:ProtectedToken, $script:ProtectedPlaceholder)
    } else {
        $Text
    }

    foreach ($pair in $script:ReplacementPairs) {
        $working = $working.Replace($pair.From, $pair.To)
    }

    if ($protectedCount -gt 0) {
        $working = $working.Replace($script:ProtectedPlaceholder, $script:ProtectedToken)
    }

    return [pscustomobject]@{
        Text = $working
        ProtectedCount = $protectedCount
    }
}

function Get-RenamedSegment {
    param([Parameter(Mandatory = $true)][string]$Segment)

    if ($Segment.Contains($script:ProtectedToken)) {
        return [pscustomobject]@{
            NewSegment = $Segment
            Changed = $false
            ProtectedCount = Count-ExactProtectedToken -Text $Segment
        }
    }

    $renamed = $Segment
    foreach ($pair in $script:ReplacementPairs) {
        $renamed = $renamed.Replace($pair.From, $pair.To)
    }

    return [pscustomobject]@{
        NewSegment = $renamed
        Changed = -not $renamed.Equals($Segment, [System.StringComparison]::Ordinal)
        ProtectedCount = 0
    }
}

function Test-IsBinaryFile {
    param([Parameter(Mandatory = $true)][string]$FullPath)

    $extension = [System.IO.Path]::GetExtension($FullPath)
    if ($extension -and $script:BinaryExtensions.Contains($extension)) {
        return $true
    }

    $stream = [System.IO.File]::OpenRead($FullPath)
    try {
        $sampleLength = [int][Math]::Min(8192L, $stream.Length)
        if ($sampleLength -le 0) {
            return $false
        }

        $buffer = New-Object byte[] $sampleLength
        [void]$stream.Read($buffer, 0, $sampleLength)

        if (
            ($sampleLength -ge 2 -and $buffer[0] -eq 0xFF -and $buffer[1] -eq 0xFE) -or
            ($sampleLength -ge 2 -and $buffer[0] -eq 0xFE -and $buffer[1] -eq 0xFF) -or
            ($sampleLength -ge 4 -and $buffer[0] -eq 0xFF -and $buffer[1] -eq 0xFE -and $buffer[2] -eq 0x00 -and $buffer[3] -eq 0x00) -or
            ($sampleLength -ge 4 -and $buffer[0] -eq 0x00 -and $buffer[1] -eq 0x00 -and $buffer[2] -eq 0xFE -and $buffer[3] -eq 0xFF)
        ) {
            return $false
        }

        foreach ($byte in $buffer) {
            if ($byte -eq 0x00) {
                return $true
            }
        }

        return $false
    }
    finally {
        $stream.Dispose()
    }
}

function Read-TextWithEncoding {
    param([Parameter(Mandatory = $true)][string]$FullPath)

    $bytes = [System.IO.File]::ReadAllBytes($FullPath)
    $length = $bytes.Length
    $encoding = $null
    $hasBom = $false
    $bomLength = 0

    if ($length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $encoding = [System.Text.Encoding]::UTF8
        $hasBom = $true
        $bomLength = 3
    }
    elseif ($length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        $encoding = [System.Text.Encoding]::Unicode
        $hasBom = $true
        $bomLength = 2
    }
    elseif ($length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) {
        $encoding = [System.Text.Encoding]::BigEndianUnicode
        $hasBom = $true
        $bomLength = 2
    }
    elseif ($length -ge 4 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE -and $bytes[2] -eq 0x00 -and $bytes[3] -eq 0x00) {
        $encoding = [System.Text.Encoding]::UTF32
        $hasBom = $true
        $bomLength = 4
    }
    elseif ($length -ge 4 -and $bytes[0] -eq 0x00 -and $bytes[1] -eq 0x00 -and $bytes[2] -eq 0xFE -and $bytes[3] -eq 0xFF) {
        $encoding = [System.Text.Encoding]::GetEncoding('utf-32BE')
        $hasBom = $true
        $bomLength = 4
    }
    else {
        $utf8Strict = [System.Text.UTF8Encoding]::new($false, $true)
        try {
            [void]$utf8Strict.GetString($bytes)
            $encoding = [System.Text.UTF8Encoding]::new($false)
        }
        catch {
            $encoding = [System.Text.Encoding]::Default
        }
    }

    $text = $encoding.GetString($bytes, $bomLength, $length - $bomLength)
    return [pscustomobject]@{
        Text = $text
        Encoding = $encoding
        HasBom = $hasBom
    }
}

function Write-TextWithEncoding {
    param(
        [Parameter(Mandatory = $true)][string]$FullPath,
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][System.Text.Encoding]$Encoding,
        [Parameter(Mandatory = $true)][bool]$HasBom
    )

    $contentBytes = $Encoding.GetBytes($Text)

    if ($HasBom) {
        $preamble = $Encoding.GetPreamble()
        if ($preamble.Length -gt 0) {
            $combined = New-Object byte[] ($preamble.Length + $contentBytes.Length)
            [System.Array]::Copy($preamble, 0, $combined, 0, $preamble.Length)
            [System.Array]::Copy($contentBytes, 0, $combined, $preamble.Length, $contentBytes.Length)
            $contentBytes = $combined
        }
    }

    [System.IO.File]::WriteAllBytes($FullPath, $contentBytes)
}

function Get-PathDepth {
    param([Parameter(Mandatory = $true)][string]$FullPath)
    return ($FullPath -split '[\\/]').Count
}

function Get-RepoInventory {
    param([Parameter(Mandatory = $true)][string]$RootPath)

    $files = New-Object System.Collections.Generic.List[System.IO.FileInfo]
    $dirs = New-Object System.Collections.Generic.List[System.IO.DirectoryInfo]
    $stack = [System.Collections.Generic.Stack[string]]::new()
    $stack.Push($RootPath)

    while ($stack.Count -gt 0) {
        $current = $stack.Pop()
        $entries = [System.IO.Directory]::EnumerateFileSystemEntries($current)
        foreach ($entry in $entries) {
            $entryFullPath = Normalize-FullPath -Path $entry

            if ([System.IO.Directory]::Exists($entryFullPath)) {
                if (Is-ExcludedDirectoryName -Name ([System.IO.Path]::GetFileName($entryFullPath))) {
                    continue
                }
                if (Is-OperationalExclusionPath -FullPath $entryFullPath) {
                    continue
                }
                $dirs.Add([System.IO.DirectoryInfo]::new($entryFullPath))
                $stack.Push($entryFullPath)
            }
            else {
                if (Is-OperationalExclusionPath -FullPath $entryFullPath) {
                    continue
                }
                $files.Add([System.IO.FileInfo]::new($entryFullPath))
            }
        }
    }

    return [pscustomobject]@{
        Files = $files
        Dirs = $dirs
    }
}

function Is-MissingPathException {
    param([Parameter(Mandatory = $true)][System.Exception]$Exception)

    $cursor = $Exception
    while ($cursor) {
        if (
            $cursor -is [System.IO.FileNotFoundException] -or
            $cursor -is [System.IO.DirectoryNotFoundException]
        ) {
            return $true
        }
        $cursor = $cursor.InnerException
    }

    return $false
}

function Is-SharingViolationException {
    param([Parameter(Mandatory = $true)][System.Exception]$Exception)

    $sharingViolation = -2147024864 # 0x80070020
    $lockViolation = -2147024863    # 0x80070021

    $cursor = $Exception
    while ($cursor) {
        if ($cursor -is [System.IO.IOException]) {
            if ($cursor.HResult -eq $sharingViolation -or $cursor.HResult -eq $lockViolation) {
                return $true
            }
        }
        $cursor = $cursor.InnerException
    }

    return $false
}

function Build-RenamePlan {
    param(
        [Parameter(Mandatory = $true)][System.Collections.IEnumerable]$Items,
        [Parameter(Mandatory = $true)][ValidateSet('File', 'Directory')][string]$Kind
    )

    $actions = New-Object System.Collections.Generic.List[object]
    $collisions = New-Object System.Collections.Generic.List[string]
    $collisionSources = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($item in $Items) {
        $segmentResult = Get-RenamedSegment -Segment $item.Name
        if (-not $segmentResult.Changed) {
            continue
        }

        $parentPath = [System.IO.Path]::GetDirectoryName($item.FullName)
        if ([string]::IsNullOrWhiteSpace($parentPath)) {
            throw "Unable to resolve parent path for rename candidate: $($item.FullName)"
        }
        $targetPath = Join-Path $parentPath $segmentResult.NewSegment
        $actions.Add([pscustomobject]@{
            Kind = $Kind
            Source = $item.FullName
            Target = $targetPath
            TargetName = $segmentResult.NewSegment
            Depth = Get-PathDepth -FullPath $item.FullName
        })
    }

    $targetMap = @{}
    foreach ($action in $actions) {
        $targetKey = $action.Target.ToLowerInvariant()
        if (-not $targetMap.ContainsKey($targetKey)) {
            $targetMap[$targetKey] = New-Object System.Collections.Generic.List[object]
        }
        $targetMap[$targetKey].Add($action)
    }

    foreach ($entry in $targetMap.GetEnumerator()) {
        if ($entry.Value.Count -gt 1) {
            $sources = ($entry.Value | ForEach-Object { $_.Source }) -join '; '
            $collisions.Add("Duplicate target [$($entry.Value[0].Target)] from sources: $sources")
            foreach ($a in $entry.Value) { [void]$collisionSources.Add($a.Source) }
        }
    }

    $sourceSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($action in $actions) {
        [void]$sourceSet.Add($action.Source)
    }

    foreach ($action in $actions) {
        if ($action.Target.Equals($action.Source, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        if (Test-Path -LiteralPath $action.Target) {
            if (-not $sourceSet.Contains($action.Target)) {
                $collisions.Add("Target already exists for $($action.Source) -> $($action.Target)")
                [void]$collisionSources.Add($action.Source)
            }
            else {
                $collisions.Add("Target currently exists and is also a rename source for $($action.Source) -> $($action.Target)")
                [void]$collisionSources.Add($action.Source)
            }
        }
    }

    $filteredActions = if ($FailOnCollision) {
        $actions
    }
    else {
        $actions | Where-Object { -not $collisionSources.Contains($_.Source) }
    }

    return [pscustomobject]@{
        Actions = $filteredActions
        Collisions = $collisions
    }
}

try {
    $script:RepoRootResolved = Normalize-FullPath -Path (Resolve-Path -LiteralPath $RepoRoot).Path
    $script:GitPath = Normalize-FullPath -Path (Join-Path $script:RepoRootResolved '.git')
    $script:GitPrefix = $script:GitPath.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar

    if (-not (Test-Path -LiteralPath $script:RepoRootResolved -PathType Container)) {
        throw "Preflight failed: Repo root does not exist: $RepoRoot"
    }

    if (-not (Test-Path -LiteralPath $script:GitPath -PathType Container)) {
        throw "Preflight failed: .git folder not found at $script:GitPath"
    }

    if ([string]::IsNullOrWhiteSpace($LogPath)) {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $LogPath = Join-Path $script:RepoRootResolved ("rename-guideants-{0}.log" -f $stamp)
    }
    elseif (-not [System.IO.Path]::IsPathRooted($LogPath)) {
        $LogPath = Join-Path $script:RepoRootResolved $LogPath
    }

    $script:LogPathResolved = Normalize-FullPath -Path $LogPath
    $logDir = Split-Path -Parent $script:LogPathResolved
    if ($logDir -and -not (Test-Path -LiteralPath $logDir)) {
        New-Item -Path $logDir -ItemType Directory -Force | Out-Null
    }

    New-Item -Path $script:LogPathResolved -ItemType File -Force | Out-Null

    Write-Log -Message "Starting rename process. DryRun=$DryRun, RepoRoot=$script:RepoRootResolved, LogPath=$script:LogPathResolved"
    Write-Log -Message "Preflight passed: .git confirmed."

    Write-Log -Message 'Building repository inventory (excluding .git and operational files)...'
    $inventory = Get-RepoInventory -RootPath $script:RepoRootResolved
    $allFiles = @($inventory.Files)
    $allDirs = @($inventory.Dirs)
    Write-Log -Message ("Inventory complete: Files={0}, Directories={1}" -f $allFiles.Count, $allDirs.Count)

    Write-Log -Message 'Planning file rename actions...'
    $fileRenamePlan = Build-RenamePlan -Items $allFiles -Kind 'File'
    Write-Log -Message ("Planned file renames: {0}" -f @($fileRenamePlan.Actions).Count)

    Write-Log -Message 'Planning directory rename actions...'
    $dirRenamePlan = Build-RenamePlan -Items $allDirs -Kind 'Directory'
    Write-Log -Message ("Planned directory renames: {0}" -f @($dirRenamePlan.Actions).Count)

    $allCollisions = @($fileRenamePlan.Collisions) + @($dirRenamePlan.Collisions)
    if ($allCollisions.Count -gt 0) {
        foreach ($collision in $allCollisions) {
            Write-Log -Message $collision -Level 'ERROR'
        }

        if ($FailOnCollision) {
            throw "COLLISION: Detected $($allCollisions.Count) rename collisions."
        }

        Write-Log -Message "Collisions detected but continuing because FailOnCollision=false. Colliding rename actions were removed." -Level 'WARN'
    }

    $rewrittenTextFiles = 0
    $skippedBinaryFiles = 0
    $skippedInaccessibleFiles = 0

    Write-Log -Message 'Starting content pass (text replacements, binary content skipped)...'
    foreach ($file in $allFiles) {
        try {
            if (Test-IsBinaryFile -FullPath $file.FullName) {
                $skippedBinaryFiles++
                continue
            }

            $textData = Read-TextWithEncoding -FullPath $file.FullName
            $replacementResult = Apply-ProtectedAwareReplacement -Text $textData.Text

            if (-not $replacementResult.Text.Equals($textData.Text, [System.StringComparison]::Ordinal)) {
                $rewrittenTextFiles++
                if ($DryRun) {
                    Write-Log -Message ("[DryRun] Would rewrite file: {0}" -f $file.FullName)
                }
                else {
                    Write-TextWithEncoding -FullPath $file.FullName -Text $replacementResult.Text -Encoding $textData.Encoding -HasBom $textData.HasBom
                    Write-Log -Message ("Rewrote file: {0}" -f $file.FullName)
                }
            }
        }
        catch {
            if (Is-MissingPathException -Exception $_.Exception) {
                $skippedInaccessibleFiles++
                Write-Log -Message ("Skipping file that no longer exists: {0}" -f $file.FullName) -Level 'WARN'
                continue
            }
            if (Is-SharingViolationException -Exception $_.Exception) {
                $skippedInaccessibleFiles++
                Write-Log -Message ("Skipping locked file (in use by another process): {0}" -f $file.FullName) -Level 'WARN'
                continue
            }
            throw
        }
    }

    $renamedFiles = 0
    $fileActionsOrdered = @($fileRenamePlan.Actions | Sort-Object -Property @{ Expression = { $_.Depth }; Descending = $true }, @{ Expression = { $_.Source }; Descending = $true })
    Write-Log -Message "Starting file rename pass (deepest-first)..."
    foreach ($action in $fileActionsOrdered) {
        try {
            if ($DryRun) {
                Write-Log -Message ("[DryRun] Would rename file: {0} -> {1}" -f $action.Source, $action.Target)
                $renamedFiles++
                continue
            }

            Rename-Item -LiteralPath $action.Source -NewName $action.TargetName
            $renamedFiles++
            Write-Log -Message ("Renamed file: {0} -> {1}" -f $action.Source, $action.Target)
        }
        catch {
            if (Is-MissingPathException -Exception $_.Exception) {
                $skippedInaccessibleFiles++
                Write-Log -Message ("Skipping file rename because source no longer exists: {0}" -f $action.Source) -Level 'WARN'
                continue
            }
            if (Is-SharingViolationException -Exception $_.Exception) {
                $skippedInaccessibleFiles++
                Write-Log -Message ("Skipping locked file rename (in use by another process): {0}" -f $action.Source) -Level 'WARN'
                continue
            }
            throw
        }
    }

    $renamedDirs = 0
    $dirActionsOrdered = @($dirRenamePlan.Actions | Sort-Object -Property @{ Expression = { $_.Depth }; Descending = $true }, @{ Expression = { $_.Source }; Descending = $true })
    Write-Log -Message "Starting directory rename pass (deepest-first)..."
    foreach ($action in $dirActionsOrdered) {
        try {
            if ($DryRun) {
                Write-Log -Message ("[DryRun] Would rename directory: {0} -> {1}" -f $action.Source, $action.Target)
                $renamedDirs++
                continue
            }

            Rename-Item -LiteralPath $action.Source -NewName $action.TargetName
            $renamedDirs++
            Write-Log -Message ("Renamed directory: {0} -> {1}" -f $action.Source, $action.Target)
        }
        catch {
            if (Is-MissingPathException -Exception $_.Exception) {
                $skippedInaccessibleFiles++
                Write-Log -Message ("Skipping directory rename because source no longer exists: {0}" -f $action.Source) -Level 'WARN'
                continue
            }
            if (Is-SharingViolationException -Exception $_.Exception) {
                $skippedInaccessibleFiles++
                Write-Log -Message ("Skipping locked directory rename (in use by another process): {0}" -f $action.Source) -Level 'WARN'
                continue
            }
            throw
        }
    }

    $preservedInvocationWaterfallOccurrences = 0
    $remainingNonAllowedResiduals = 0

    if ($DryRun) {
        Write-Log -Message 'Dry run: verification pass skipped (no mutations applied).'
    }
    else {
        Write-Log -Message 'Starting verification pass...'
        $postInventory = Get-RepoInventory -RootPath $script:RepoRootResolved
        $postFiles = @($postInventory.Files)
        $postDirs = @($postInventory.Dirs)

        foreach ($dir in $postDirs) {
            $nameCounts = Get-WaterfallCountsExcludingProtected -Text $dir.Name
            $preservedInvocationWaterfallOccurrences += $nameCounts.Allowed
            if ($nameCounts.Remaining -gt 0) {
                $remainingNonAllowedResiduals += $nameCounts.Remaining
                Write-Log -Message ("Residual in directory name: {0}" -f $dir.FullName) -Level 'ERROR'
            }
        }

        foreach ($file in $postFiles) {
            $nameCounts = Get-WaterfallCountsExcludingProtected -Text $file.Name
            $preservedInvocationWaterfallOccurrences += $nameCounts.Allowed
            if ($nameCounts.Remaining -gt 0) {
                $remainingNonAllowedResiduals += $nameCounts.Remaining
                Write-Log -Message ("Residual in file name: {0}" -f $file.FullName) -Level 'ERROR'
            }

            try {
                if (Test-IsBinaryFile -FullPath $file.FullName) {
                    continue
                }

                $textData = Read-TextWithEncoding -FullPath $file.FullName
                $contentCounts = Get-WaterfallCountsExcludingProtected -Text $textData.Text
                $preservedInvocationWaterfallOccurrences += $contentCounts.Allowed
                if ($contentCounts.Remaining -gt 0) {
                    $remainingNonAllowedResiduals += $contentCounts.Remaining
                    Write-Log -Message ("Residual in file content: {0}" -f $file.FullName) -Level 'ERROR'
                }
            }
            catch {
                if (Is-MissingPathException -Exception $_.Exception) {
                    $skippedInaccessibleFiles++
                    Write-Log -Message ("Skipping verification for file that no longer exists: {0}" -f $file.FullName) -Level 'WARN'
                    continue
                }
                if (Is-SharingViolationException -Exception $_.Exception) {
                    $skippedInaccessibleFiles++
                    Write-Log -Message ("Skipping verification for locked file (in use by another process): {0}" -f $file.FullName) -Level 'WARN'
                    continue
                }
                throw
            }
        }

        if ($remainingNonAllowedResiduals -gt 0 -and $FailOnResidual) {
            throw "RESIDUAL: Found $remainingNonAllowedResiduals non-allowed residual waterfall matches after rename."
        }
    }

    Write-Log -Message '---------------- Summary ----------------'
    Write-Log -Message ("Rewritten text files: {0}" -f $rewrittenTextFiles)
    Write-Log -Message ("Renamed files: {0}" -f $renamedFiles)
    Write-Log -Message ("Renamed directories: {0}" -f $renamedDirs)
    Write-Log -Message ("Skipped binary files (content pass): {0}" -f $skippedBinaryFiles)
    Write-Log -Message ("Skipped inaccessible files (missing/locked): {0}" -f $skippedInaccessibleFiles)
    Write-Log -Message ("Preserved InvocationWaterfall occurrences: {0}" -f $preservedInvocationWaterfallOccurrences)
    if ($DryRun) {
        Write-Log -Message "Remaining non-allowed residuals: n/a (verification skipped in dry run)"
    }
    else {
        Write-Log -Message ("Remaining non-allowed residuals: {0}" -f $remainingNonAllowedResiduals)
    }

    $repoName = [System.IO.Path]::GetFileName($script:RepoRootResolved)
    $repoNameReplacement = Get-RenamedSegment -Segment $repoName
    $newRootName = $repoNameReplacement.NewSegment
    $parentDir = [System.IO.Path]::GetDirectoryName($script:RepoRootResolved)

    Write-Log -Message 'Manual root-folder rename command (run from parent directory):'
    Write-Host ("Set-Location -LiteralPath {0}" -f (Convert-ToSingleQuotedLiteral -Value $parentDir))
    Write-Host ("Rename-Item -LiteralPath {0} -NewName {1}" -f (Convert-ToSingleQuotedLiteral -Value $script:RepoRootResolved), (Convert-ToSingleQuotedLiteral -Value $newRootName))

    Write-Log -Message 'Completed successfully.'
    exit 0
}
catch {
    $message = $_.Exception.Message
    if ($script:LogPathResolved) {
        Write-Log -Message $message -Level 'ERROR'
    }
    else {
        Write-Error $message
    }

    if ($message.StartsWith('COLLISION:', [System.StringComparison]::Ordinal)) {
        exit 2
    }

    if ($message.StartsWith('RESIDUAL:', [System.StringComparison]::Ordinal)) {
        exit 3
    }

    exit 1
}
