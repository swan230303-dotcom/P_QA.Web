param(
    [Parameter(Mandatory = $false)][string]$SecureSettingsPath,
    [Parameter(Mandatory = $false)][string]$KeyPath
)

$ErrorActionPreference = 'Stop'
$scriptDirectory = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) { $scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path }
$applicationDirectory = Split-Path -Parent $scriptDirectory
if ([string]::IsNullOrWhiteSpace($SecureSettingsPath)) { $SecureSettingsPath = Join-Path $applicationDirectory 'connections.aes' }
if ([string]::IsNullOrWhiteSpace($KeyPath)) { $KeyPath = Join-Path $applicationDirectory 'connections.key' }
. (Join-Path $scriptDirectory 'AesSettings.ps1')

$settings = Read-AesSettings -SettingsPath $SecureSettingsPath -KeyPath $KeyPath
$credential = Get-Credential -Message 'QA NAS account for \\nasts469\東正舊系統 (example: DOMAIN\account)'
$passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($credential.Password)
try {
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
    $settings | Add-Member -NotePropertyName 'QaFileUser' -NotePropertyValue $credential.UserName -Force
    $settings | Add-Member -NotePropertyName 'QaFilePassword' -NotePropertyValue $password -Force
    Write-AesSettings -Settings $settings -SettingsPath $SecureSettingsPath -KeyPath $KeyPath
}
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer) }
Write-Output "QA NAS credentials updated in AES settings: $([IO.Path]::GetFullPath($SecureSettingsPath))"
