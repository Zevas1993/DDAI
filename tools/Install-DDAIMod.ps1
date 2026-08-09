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

    $sourceFiles = @(Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File)
    $targetFiles = @(Get-ChildItem -LiteralPath $TargetDirectory -Recurse -File)
    if ($sourceFiles.Count -ne $targetFiles.Count) {
        return $false
    }

    foreach ($sourceFile in $sourceFiles) {
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

function Copy-SourceToNewTarget {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$TargetDirectory
    )

    New-Item -ItemType Directory -Path $TargetDirectory -Force | Out-Null
    foreach ($sourceFile in Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File) {
        $relative = $sourceFile.FullName.Substring($SourceDirectory.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
        $targetFile = Join-Path $TargetDirectory $relative
        New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($targetFile)) -Force | Out-Null
        Copy-Item -LiteralPath $sourceFile.FullName -Destination $targetFile -Force
    }
}

function Install-DDAIStatusBridge {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$TargetDirectory,
        [Parameter(Mandatory = $true)][string]$BackupRoot
    )

    if (Test-Path -LiteralPath $TargetDirectory) {
        # A uniquely named target may only be updated when it is demonstrably ours.
        Get-ValidatedManifest -ModDirectory $TargetDirectory | Out-Null
        if (Test-SourceMatchesTarget -SourceDirectory $SourceDirectory -TargetDirectory $TargetDirectory) {
            return @{ State = 'already_current'; Backup = $null }
        }
    }

    $stageDirectory = Join-Path ([System.IO.Path]::GetDirectoryName($TargetDirectory)) ('.DDAI-stage-' + [Guid]::NewGuid().ToString('N'))
    Copy-SourceToNewTarget -SourceDirectory $SourceDirectory -TargetDirectory $stageDirectory
    if (-not (Test-Path -LiteralPath $TargetDirectory)) {
        Move-Item -LiteralPath $stageDirectory -Destination $TargetDirectory
        return @{ State = 'installed'; Backup = $null }
    }

    New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
    $backupDirectory = Join-Path $BackupRoot ('DDAI-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffffff') + '-' + [Guid]::NewGuid().ToString('N'))
    Move-Item -LiteralPath $TargetDirectory -Destination $backupDirectory
    try {
        Move-Item -LiteralPath $stageDirectory -Destination $TargetDirectory
    }
    catch {
        Move-Item -LiteralPath $backupDirectory -Destination $TargetDirectory
        throw
    }

    return @{ State = 'repaired'; Backup = $backupDirectory }
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
    $heartbeatRoot = Join-Path $UserDirectory 'ddai\runtime-heartbeats'
    $heartbeatFiles = @()
    if (Test-Path -LiteralPath $heartbeatRoot -PathType Container) {
        $heartbeatFiles = @(Get-ChildItem -LiteralPath $heartbeatRoot -Recurse -File -Filter '*.json')
    }
    if ($heartbeatFiles.Count -eq 0) {
        return @{
            state = 'installed_not_observed'
            code = 'runtime_heartbeat_missing'
            runtime_receipt_present = $false
            target = $TargetDirectory
            message = 'The mod is installed but has not written a runtime heartbeat. It may be disabled, or Dungeondraft has not loaded/reloaded it yet.'
        }
    }

    $freshest = $null
    foreach ($heartbeatFile in $heartbeatFiles) {
        try { $heartbeat = Get-Content -LiteralPath $heartbeatFile.FullName -Raw | ConvertFrom-Json; $when = [DateTimeOffset]::Parse($heartbeat.timestamp) } catch { continue }
        if ($heartbeat.session_id -and $heartbeat.mod_version -eq $manifest.version -and ($null -eq $freshest -or $when -gt $freshest.When)) {
            $freshest = @{ When = $when; SessionId = $heartbeat.session_id }
        }
    }

    if ($null -eq $freshest) {
        return @{ state = 'installed_not_observed'; code = 'runtime_heartbeat_malformed_or_mismatched'; runtime_receipt_present = $false; target = $TargetDirectory }
    }

    if ($freshest.When -lt [DateTimeOffset]::UtcNow.AddSeconds(-30)) {
        return @{ state = 'installed_not_observed'; code = 'runtime_heartbeat_stale'; runtime_receipt_present = $false; target = $TargetDirectory; session_id = $freshest.SessionId }
    }

    return @{ state = 'running'; code = 'runtime_heartbeat_fresh'; runtime_receipt_present = $false; target = $TargetDirectory; session_id = $freshest.SessionId }
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

    $install = Install-DDAIStatusBridge -SourceDirectory $sourceDirectory -TargetDirectory $targetDirectory -BackupRoot (Join-Path $UserDataDirectory 'ddai\mod-backups')
    Write-Result @{ state = $install.State; target = $targetDirectory; backup = $install.Backup; unique_id = 'org.ddai.status_bridge'; changed_only = 'DDAI-owned mod files' }
    exit 0
}
catch {
    Write-Result @{ state = 'error'; code = 'installation_failed'; message = $_.Exception.Message; location = $_.InvocationInfo.PositionMessage; stack = $_.ScriptStackTrace }
    exit 1
}
