[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$SourceModDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ModsDirectory,

    [ValidateNotNullOrEmpty()]
    [string]$UserDataDirectory = (Join-Path $env:APPDATA 'Dungeondraft'),

    [switch]$Diagnose
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Result {
    param([Parameter(Mandatory = $true)][hashtable]$Result)

    [Console]::Out.WriteLine(($Result | ConvertTo-Json -Compress -Depth 8))
}

function Get-ValidatedManifest {
    param([Parameter(Mandatory = $true)][string]$ModDirectory)

    $manifestPath = Join-Path $ModDirectory 'ddai_bridge.ddmod'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "DDAI manifest is missing: $manifestPath"
    }

    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "DDAI manifest is not valid JSON: $manifestPath"
    }

    if ($manifest.unique_id -ne 'org.ddai.status_bridge' -or $manifest.dd_version -ne '1.2.0.1') {
        throw "DDAI manifest does not identify the supported status bridge: $manifestPath"
    }

    return $manifest
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $hash = [System.Security.Cryptography.SHA256]::Create().ComputeHash($stream)
        return [System.BitConverter]::ToString($hash).Replace('-', '')
    }
    finally {
        $stream.Dispose()
    }
}

function Test-SourceMatchesTarget {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$TargetDirectory
    )

    foreach ($sourceFile in Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File) {
        $relative = $sourceFile.FullName.Substring($SourceDirectory.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
        $targetFile = Join-Path $TargetDirectory $relative
        if (-not (Test-Path -LiteralPath $targetFile -PathType Leaf)) {
            return $false
        }
        if ((Get-Sha256 -Path $sourceFile.FullName) -ne (Get-Sha256 -Path $targetFile)) {
            return $false
        }
    }

    return $true
}

function Install-DDAIStatusBridge {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$TargetDirectory
    )

    if (Test-Path -LiteralPath $TargetDirectory) {
        # A uniquely named target may only be updated when it is demonstrably ours.
        Get-ValidatedManifest -ModDirectory $TargetDirectory | Out-Null
        if (Test-SourceMatchesTarget -SourceDirectory $SourceDirectory -TargetDirectory $TargetDirectory) {
            return 'already_current'
        }
    }

    foreach ($sourceFile in Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File) {
        $relative = $sourceFile.FullName.Substring($SourceDirectory.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
        $targetFile = Join-Path $TargetDirectory $relative
        $targetParent = [System.IO.Path]::GetDirectoryName($targetFile)
        New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
        Copy-Item -LiteralPath $sourceFile.FullName -Destination $targetFile -Force
    }

    return 'installed'
}

function Get-Diagnosis {
    param(
        [Parameter(Mandatory = $true)][string]$TargetDirectory,
        [Parameter(Mandatory = $true)][string]$UserDirectory
    )

    if (-not (Test-Path -LiteralPath $TargetDirectory -PathType Container)) {
        return @{ state = 'missing'; code = 'mod_missing'; runtime_receipt_present = $false; target = $TargetDirectory }
    }

    $manifest = Get-ValidatedManifest -ModDirectory $TargetDirectory
    $receiptPath = Join-Path $UserDirectory 'ddai\runtime-receipt.json'
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        return @{
            state = 'installed_not_observed'
            code = 'runtime_receipt_missing'
            runtime_receipt_present = $false
            target = $TargetDirectory
            message = 'The mod is installed but has not written a runtime receipt. It may be disabled, or Dungeondraft has not loaded/reloaded it yet.'
        }
    }

    try {
        $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    }
    catch {
        return @{ state = 'installed_not_observed'; code = 'runtime_receipt_malformed'; runtime_receipt_present = $true; target = $TargetDirectory }
    }

    if ($receipt.mod_version -ne $manifest.version) {
        return @{ state = 'installed_not_observed'; code = 'runtime_receipt_version_mismatch'; runtime_receipt_present = $true; target = $TargetDirectory }
    }

    return @{ state = 'running'; code = 'runtime_receipt_present'; runtime_receipt_present = $true; target = $TargetDirectory }
}

try {
    $sourceDirectory = (Resolve-Path -LiteralPath $SourceModDirectory -ErrorAction Stop).Path
    Get-ValidatedManifest -ModDirectory $sourceDirectory | Out-Null
    $modsDirectory = [System.IO.Path]::GetFullPath($ModsDirectory)
    New-Item -ItemType Directory -Path $modsDirectory -Force | Out-Null
    $targetDirectory = Join-Path $modsDirectory 'DDAI'

    if ($Diagnose) {
        Write-Result (Get-Diagnosis -TargetDirectory $targetDirectory -UserDirectory $UserDataDirectory)
        exit 0
    }

    $state = Install-DDAIStatusBridge -SourceDirectory $sourceDirectory -TargetDirectory $targetDirectory
    Write-Result @{ state = $state; target = $targetDirectory; unique_id = 'org.ddai.status_bridge'; changed_only = 'DDAI-owned mod files' }
    exit 0
}
catch {
    Write-Result @{ state = 'error'; code = 'installation_failed'; message = $_.Exception.Message; location = $_.InvocationInfo.PositionMessage; stack = $_.ScriptStackTrace }
    exit 1
}
