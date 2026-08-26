$script:AesMagic = [Text.Encoding]::ASCII.GetBytes('PBM212A1')
$script:AesKeySize = 64
$script:AesIvSize = 16
$script:AesTagSize = 32

function Get-AesMasterKey {
    param([Parameter(Mandatory = $true)][string]$KeyPath, [switch]$Create)

    $environmentKey = [Environment]::GetEnvironmentVariable('P_QA_AES_KEY')
    if ([string]::IsNullOrWhiteSpace($environmentKey)) {
        $environmentKey = [Environment]::GetEnvironmentVariable('P_BM212_AES_KEY')
    }
    if (-not [string]::IsNullOrWhiteSpace($environmentKey)) {
        try { $key = [Convert]::FromBase64String($environmentKey.Trim()) }
        catch { throw 'P_QA_AES_KEY is not valid Base64.' }
    }
    elseif (Test-Path -LiteralPath $KeyPath) {
        try { $key = [Convert]::FromBase64String(([IO.File]::ReadAllText($KeyPath, [Text.Encoding]::ASCII)).Trim()) }
        catch { throw "AES key file is not valid Base64: $KeyPath" }
    }
    elseif ($Create) {
        $key = New-Object byte[] $script:AesKeySize
        $random = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $random.GetBytes($key) } finally { $random.Dispose() }
        $keyDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($KeyPath))
        if (-not [string]::IsNullOrWhiteSpace($keyDirectory)) { [IO.Directory]::CreateDirectory($keyDirectory) | Out-Null }
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($KeyPath), [Convert]::ToBase64String($key), [Text.Encoding]::ASCII)
    }
    else { throw "AES key file was not found: $KeyPath" }

    if ($key.Length -ne $script:AesKeySize) {
        [Array]::Clear($key, 0, $key.Length)
        throw 'AES key must contain 64 bytes (AES 32 + HMAC 32).'
    }
    return $key
}

function Test-FixedTimeEqual {
    param([byte[]]$Left, [byte[]]$Right)
    if ($Left.Length -ne $Right.Length) { return $false }
    $difference = 0
    for ($index = 0; $index -lt $Left.Length; $index++) { $difference = $difference -bor ($Left[$index] -bxor $Right[$index]) }
    return $difference -eq 0
}

function Read-AesSettings {
    param([Parameter(Mandatory = $true)][string]$SettingsPath, [Parameter(Mandatory = $true)][string]$KeyPath)

    $key = Get-AesMasterKey -KeyPath $KeyPath
    $envelope = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($SettingsPath))
    try {
        $minimumSize = $script:AesMagic.Length + $script:AesIvSize + 16 + $script:AesTagSize
        if ($envelope.Length -lt $minimumSize) { throw 'AES envelope is too short.' }
        for ($i = 0; $i -lt $script:AesMagic.Length; $i++) {
            if ($envelope[$i] -ne $script:AesMagic[$i]) { throw 'AES envelope format or version is invalid.' }
        }

        $authenticatedLength = $envelope.Length - $script:AesTagSize
        $macKey = New-Object byte[] 32
        $expectedTag = New-Object byte[] $script:AesTagSize
        [Array]::Copy($key, 32, $macKey, 0, 32)
        [Array]::Copy($envelope, $authenticatedLength, $expectedTag, 0, $script:AesTagSize)
        $hmac = New-Object Security.Cryptography.HMACSHA256
        try {
            $hmac.Key = $macKey
            $actualTag = $hmac.ComputeHash($envelope, 0, $authenticatedLength)
        }
        finally { $hmac.Dispose(); [Array]::Clear($macKey, 0, $macKey.Length) }
        try {
            if (-not (Test-FixedTimeEqual $actualTag $expectedTag)) { throw 'AES authentication failed. The key is wrong or the file was modified.' }
        }
        finally { [Array]::Clear($actualTag, 0, $actualTag.Length); [Array]::Clear($expectedTag, 0, $expectedTag.Length) }

        $iv = New-Object byte[] $script:AesIvSize
        $cipherLength = $authenticatedLength - $script:AesMagic.Length - $script:AesIvSize
        $cipher = New-Object byte[] $cipherLength
        [Array]::Copy($envelope, $script:AesMagic.Length, $iv, 0, $script:AesIvSize)
        [Array]::Copy($envelope, $script:AesMagic.Length + $script:AesIvSize, $cipher, 0, $cipherLength)
        $aes = [Security.Cryptography.Aes]::Create()
        $aesKey = New-Object byte[] 32
        try {
            $aes.KeySize = 256; $aes.Mode = 'CBC'; $aes.Padding = 'PKCS7'
            [Array]::Copy($key, 0, $aesKey, 0, 32)
            $aes.Key = $aesKey; $aes.IV = $iv
            $decryptor = $aes.CreateDecryptor()
            try { $clear = $decryptor.TransformFinalBlock($cipher, 0, $cipher.Length) } finally { $decryptor.Dispose() }
            try { return [Text.Encoding]::UTF8.GetString($clear) | ConvertFrom-Json }
            finally { [Array]::Clear($clear, 0, $clear.Length) }
        }
        finally { $aes.Dispose(); [Array]::Clear($aesKey, 0, $aesKey.Length); [Array]::Clear($iv, 0, $iv.Length); [Array]::Clear($cipher, 0, $cipher.Length) }
    }
    finally { [Array]::Clear($key, 0, $key.Length); [Array]::Clear($envelope, 0, $envelope.Length) }
}

function Write-AesSettings {
    param([Parameter(Mandatory = $true)]$Settings, [Parameter(Mandatory = $true)][string]$SettingsPath, [Parameter(Mandatory = $true)][string]$KeyPath)

    $key = Get-AesMasterKey -KeyPath $KeyPath -Create
    $clear = [Text.Encoding]::UTF8.GetBytes(($Settings | ConvertTo-Json -Compress))
    $aesKey = New-Object byte[] 32
    $cipher = $null
    try {
        $aes = [Security.Cryptography.Aes]::Create()
        try {
            $aes.KeySize = 256; $aes.Mode = 'CBC'; $aes.Padding = 'PKCS7'; $aes.GenerateIV()
            [Array]::Copy($key, 0, $aesKey, 0, 32)
            $aes.Key = $aesKey
            $encryptor = $aes.CreateEncryptor()
            try { $cipher = $encryptor.TransformFinalBlock($clear, 0, $clear.Length) } finally { $encryptor.Dispose() }
            $authenticatedLength = $script:AesMagic.Length + $script:AesIvSize + $cipher.Length
            $envelope = New-Object byte[] ($authenticatedLength + $script:AesTagSize)
            [Array]::Copy($script:AesMagic, 0, $envelope, 0, $script:AesMagic.Length)
            [Array]::Copy($aes.IV, 0, $envelope, $script:AesMagic.Length, $script:AesIvSize)
            [Array]::Copy($cipher, 0, $envelope, $script:AesMagic.Length + $script:AesIvSize, $cipher.Length)
            $macKey = New-Object byte[] 32; [Array]::Copy($key, 32, $macKey, 0, 32)
            $hmac = New-Object Security.Cryptography.HMACSHA256
            try { $hmac.Key = $macKey; $tag = $hmac.ComputeHash($envelope, 0, $authenticatedLength) }
            finally { $hmac.Dispose(); [Array]::Clear($macKey, 0, $macKey.Length) }
            try { [Array]::Copy($tag, 0, $envelope, $authenticatedLength, $script:AesTagSize); [IO.File]::WriteAllBytes([IO.Path]::GetFullPath($SettingsPath), $envelope) }
            finally { [Array]::Clear($tag, 0, $tag.Length); [Array]::Clear($envelope, 0, $envelope.Length) }
        }
        finally { $aes.Dispose() }
    }
    finally {
        [Array]::Clear($aesKey, 0, $aesKey.Length)
        if ($null -ne $cipher) { [Array]::Clear($cipher, 0, $cipher.Length) }
        [Array]::Clear($clear, 0, $clear.Length); [Array]::Clear($key, 0, $key.Length)
    }
}
