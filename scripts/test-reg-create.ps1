$ErrorActionPreference = 'Continue'
$au = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'

Write-Host "Before Test-Path AU: $(Test-Path $au)"

try {
    $result = New-Item -Path $au -Force -ErrorAction Stop
    Write-Host "New-Item succeeded: $($result | Out-String)"
} catch {
    Write-Host "New-Item FAILED: $($_.Exception.Message)"
}

Write-Host "After Test-Path AU: $(Test-Path $au)"

try {
    New-ItemProperty -Path $au -Name NoAutoUpdate -Value 1 -PropertyType DWord -Force -ErrorAction Stop
    Write-Host "New-ItemProperty succeeded"
} catch {
    Write-Host "New-ItemProperty FAILED: $($_.Exception.Message)"
}
