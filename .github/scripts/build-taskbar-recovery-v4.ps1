$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Write-Host "Building 1Remote 1.2.1 taskbar recovery v4"

$workspace = $env:GITHUB_WORKSPACE
if ([string]::IsNullOrWhiteSpace($workspace)) {
    $workspace = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}

$v4SourcePath = Join-Path $workspace 'Ui\View\Host\TabWindowView.taskbar_recovery_v4.cs'
if (-not (Test-Path $v4SourcePath)) {
    throw "Taskbar recovery v4 source file was not found: $v4SourcePath"
}

$v4Source = Get-Content $v4SourcePath -Raw
$requiredV4Source = @(
    'TaskbarRecoveryV4-silent-foreground-watchdog',
    'RegisterTaskbarRecoveryV4',
    'V4_SILENT_FOREGROUND_MISMATCH_SAMPLE',
    'V4_RECOVERY_SEND_SC_MINIMIZE',
    'foreground == hwnd && !IsActive',
    'RecoveryV4RequiredMismatchSamples',
    'RecoveryV4CandidateLifetimeMilliseconds'
)
foreach ($fragment in $requiredV4Source) {
    if (-not $v4Source.Contains($fragment)) {
        throw "Required recovery-v4 source fragment is missing: $fragment"
    }
}

$forbiddenV4Source = @(
    'SHGetPropertyStoreForWindow',
    'interface ITaskbarList',
    'ShowInTaskbar =',
    'SetCurrentProcessExplicitAppUserModelID',
    'handled = true'
)
foreach ($fragment in $forbiddenV4Source) {
    if ($v4Source.Contains($fragment)) {
        throw "Forbidden taskbar mutation fragment is present in v4: $fragment"
    }
}
Write-Host 'Recovery-v4 source validation passed.'

# Reuse the already verified official-1.2.1 build pipeline from v3.  The
# current branch contains the v4 supplement, so the resulting EXE contains
# both the explicit WA_ACTIVE recovery and the silent-foreground watchdog.
$v3BuildScript = Join-Path $workspace '.github\scripts\build-taskbar-recovery-v3.ps1'
& $v3BuildScript
if ($LASTEXITCODE -ne 0) {
    throw 'The verified v3 baseline build pipeline failed.'
}

$env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"
$publishDir = Join-Path $workspace 'Ui\bin\Release\net9.0-windows\publish\win-x64'
$exePath = Join-Path $publishDir '1Remote.exe'
if (-not (Test-Path $exePath)) {
    throw "Published 1Remote.exe was not found: $exePath"
}

$verifyBundle = Join-Path $env:RUNNER_TEMP 'recovery-v4-compiled-bundle'
$verifySource = Join-Path $env:RUNNER_TEMP 'TabWindowView.recovery-v4.decompiled.cs'
$verifyBootstrap = Join-Path $env:RUNNER_TEMP 'TaskbarRecoveryV4Bootstrap.decompiled.cs'
if (Test-Path $verifyBundle) {
    Remove-Item $verifyBundle -Recurse -Force
}
New-Item -ItemType Directory -Path $verifyBundle -Force | Out-Null

& sfextract $exePath -o $verifyBundle
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to extract the compiled v4 single-file EXE.'
}

$compiledAssembly = Get-ChildItem -Path $verifyBundle -Recurse -File -Filter '1Remote.dll' | Select-Object -First 1
if ($null -eq $compiledAssembly) {
    throw 'The compiled 1Remote.dll entry was not found for v4 verification.'
}

& ilspycmd -t '_1RM.View.Host.TabWindowView' $compiledAssembly.FullName | Set-Content -Path $verifySource -Encoding UTF8
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to decompile the compiled TabWindowView type for v4 verification.'
}

& ilspycmd -t '_1RM.View.Host.TaskbarRecoveryV4Bootstrap' $compiledAssembly.FullName | Set-Content -Path $verifyBootstrap -Encoding UTF8
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to decompile the compiled v4 bootstrap type.'
}

$compiled = Get-Content $verifySource -Raw
$requiredCompiledV4 = @(
    'TaskbarRecoveryV4-silent-foreground-watchdog',
    'V4_SILENT_FOREGROUND_MISMATCH_SAMPLE',
    'V4_RECOVERY_SEND_SC_MINIMIZE',
    'TaskbarRecoveryV4TimerOnTick',
    'RecoverSilentForegroundMismatchV4',
    'InitializeTaskbarRecoveryV4'
)
foreach ($fragment in $requiredCompiledV4) {
    if (-not $compiled.Contains($fragment)) {
        throw "Compiled EXE is missing recovery-v4 fragment: $fragment"
    }
}

$compiledBootstrap = Get-Content $verifyBootstrap -Raw
$requiredBootstrapFragments = @(
    'RegisterTaskbarRecoveryV4',
    'RegisterClassHandler',
    'OnTabWindowLoaded'
)
foreach ($fragment in $requiredBootstrapFragments) {
    if (-not $compiledBootstrap.Contains($fragment)) {
        throw "Compiled EXE is missing recovery-v4 bootstrap fragment: $fragment"
    }
}

Remove-Item $verifyBundle -Recurse -Force
Remove-Item $verifySource -Force
Remove-Item $verifyBootstrap -Force
Write-Host 'Compiled EXE recovery-v4 verification passed.'

$sourceSha = (git -C $workspace rev-parse HEAD).Trim()
$officialInputSha256 = '825ae0d6d6ed45dbb155ed809b976c2a7532578b124b3b55c0cfc6bfa3267411'
$readmePath = Join-Path $publishDir 'README-Taskbar-Recovery-V4.txt'
$readmeLines = @(
    '1Remote 1.2.1 - Windows 11 25H2 taskbar recovery v4',
    '',
    "Source commit: $sourceSha",
    'Official baseline commit: a2a81be532f7da9016b77657009ccfe09574be9f',
    "Official input SHA-256: $officialInputSha256",
    '',
    'What v4 adds:',
    '- Keeps v3 recovery for the explicit WA_ACTIVE-without-SC_MINIMIZE path.',
    '- Adds an independent watchdog immediately after an eligible taskbar WA_INACTIVE.',
    '- Detects the newly captured contradiction: this HWND is foreground while WPF IsActive remains false and the window is non-iconic.',
    '- Requires that contradiction on two consecutive 35 ms samples before sending native SC_MINIMIZE.',
    '- Keeps the candidate for 2500 ms because affected-machine failures persisted for roughly 0.8-1.8 seconds.',
    '- Normal native SC_MINIMIZE and every SC_RESTORE remain untouched.',
    '- No AppUserModelID, ITaskbarList, ShowInTaskbar, owner, or style mutation.',
    '',
    'Trace:',
    '- The cumulative log remains .logs\TaskbarRecoveryV3-<PID>-<timestamp>.log.',
    '- v4 events are prefixed V4_, especially V4_SILENT_FOREGROUND_MISMATCH_SAMPLE and V4_RECOVERY_RESULT.',
    '',
    'Install into a new directory after fully exiting 1Remote. Back up existing data first.'
)
Set-Content -Path $readmePath -Value $readmeLines -Encoding UTF8

$hashPath = Join-Path $publishDir 'SHA256SUMS.txt'
Get-ChildItem -Path $publishDir -File |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    Sort-Object Name |
    ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    } | Set-Content -Path $hashPath -Encoding ASCII

$zipPath = Join-Path $workspace '1Remote-1.2.1-win-x64-Win11-25H2-taskbar-recovery-v4.zip'
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$exeHash = (Get-FileHash $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Recovery-v4 ZIP: $zipPath"
Write-Host "Recovery-v4 ZIP SHA-256: $zipHash"
Write-Host "Recovery-v4 EXE SHA-256: $exeHash"
