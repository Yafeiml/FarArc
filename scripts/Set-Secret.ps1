[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [string]$filePath,
    [Parameter(Mandatory = $true)]
    [string]$Pattern,
    [string]$Secret,
    [string]$localSecretFilePath,
    [switch]$isRevert
)

if ([string]::IsNullOrWhiteSpace($Pattern)) {
    throw "Pattern cannot be empty."
}

if ([string]::IsNullOrWhiteSpace($Secret)) {
    if ([string]::IsNullOrWhiteSpace($localSecretFilePath)) {
        throw "Secret and localSecretFilePath cannot both be empty."
    }
    if (!(Test-Path -LiteralPath $localSecretFilePath -PathType Leaf)) {
        throw "Secret file does not exist: $localSecretFilePath"
    }
    $Secret = [System.IO.File]::ReadAllText($localSecretFilePath).Trim()
}

if ([string]::IsNullOrWhiteSpace($Secret)) {
    throw "Secret cannot be empty."
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$resolvedFilePath = if ([System.IO.Path]::IsPathRooted($filePath)) {
    [System.IO.Path]::GetFullPath($filePath)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $filePath))
}

if (!(Test-Path -LiteralPath $resolvedFilePath -PathType Leaf)) {
    throw "Target file does not exist: $resolvedFilePath"
}

$quotedPattern = '"' + $Pattern + '";'
$quotedSecret = '"' + $Secret + '";'
$target = if ($isRevert) { $quotedSecret } else { $quotedPattern }
$replacement = if ($isRevert) { $quotedPattern } else { $quotedSecret }
$bytes = [System.IO.File]::ReadAllBytes($resolvedFilePath)
$hasUtf8Bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
$encoding = [System.Text.UTF8Encoding]::new($hasUtf8Bom)
$content = $encoding.GetString($bytes, $(if ($hasUtf8Bom) { 3 } else { 0 }), $bytes.Length - $(if ($hasUtf8Bom) { 3 } else { 0 }))

if ($content.IndexOf($target, [System.StringComparison]::Ordinal) -lt 0) {
    if ($isRevert -and $content.IndexOf($quotedPattern, [System.StringComparison]::Ordinal) -ge 0) {
        Write-Warning "Target file is already restored: $resolvedFilePath"
        return
    }
    throw "Expected placeholder or secret was not found in $resolvedFilePath"
}

$updatedContent = $content.Replace($target, $replacement)
[System.IO.File]::WriteAllText($resolvedFilePath, $updatedContent, $encoding)
