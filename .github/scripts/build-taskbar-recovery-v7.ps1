$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Write-Host 'Building 1Remote 1.2.1 taskbar recovery v7'
Write-Host "SDK: $(dotnet --version)"

$workspace = $env:GITHUB_WORKSPACE
if ([string]::IsNullOrWhiteSpace($workspace)) {
    $workspace = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}

$v6SourcePath = Join-Path $workspace 'Ui\View\Host\TabWindowView.taskbar_recovery_v6.cs'
$v7SourcePath = Join-Path $workspace 'Ui\View\Host\TabWindowView.taskbar_recovery_v7.cs'
if (-not (Test-Path $v6SourcePath)) {
    throw "Taskbar recovery v6 source file was not found: $v6SourcePath"
}
if (-not (Test-Path $v7SourcePath)) {
    throw "Taskbar recovery v7 source file was not found: $v7SourcePath"
}

$v6Source = Get-Content $v6SourcePath -Raw
$v7Source = Get-Content $v7SourcePath -Raw

$requiredV6Source = @(
    'TaskbarRecoveryV6-early-stable-native-takeover',
    'V6_RECOVERY_POST_SC_MINIMIZE',
    'V6_NATIVE_SC_MINIMIZE_AFTER_RECOVERY',
    'V6EarlyTakeoverMilliseconds',
    'SIBJumpView',
    'EntryPoint = "PostMessageW"'
)
foreach ($fragment in $requiredV6Source) {
    if (-not $v6Source.Contains($fragment)) {
        throw "Required recovery-v6 source fragment is missing: $fragment"
    }
}

$requiredV7Source = @(
    'TaskbarRecoveryV7-late-native-minimize-dedup',
    'RegisterTaskbarRecoveryV7',
    'V7_RESTORE_AFTER_SYNTHETIC',
    'V7_LATE_NATIVE_SC_MINIMIZE_SUPPRESSED',
    'V7_SC_MINIMIZE_ALLOWED',
    'V7DuplicateWindowMilliseconds',
    'V7CurrentTaskbarCommandEnvelopeMilliseconds',
    'requiresRestoreAfterSynthetic=true',
    'requiresTaskbarOrZeroForeground=true',
    'handled = true'
)
foreach ($fragment in $requiredV7Source) {
    if (-not $v7Source.Contains($fragment)) {
        throw "Required recovery-v7 source fragment is missing: $fragment"
    }
}

$forbiddenCombined = @(
    'TaskbarRecoveryV3-reactivation-45ms',
    'TaskbarRecoveryV4-silent-foreground-watchdog',
    'TaskbarRecoveryV5-native-foreground-state-machine',
    'SHGetPropertyStoreForWindow',
    'interface ITaskbarList',
    'SetCurrentProcessExplicitAppUserModelID',
    'ShowInTaskbar ='
)
foreach ($fragment in $forbiddenCombined) {
    if ($v6Source.Contains($fragment) -or $v7Source.Contains($fragment)) {
        throw "Forbidden legacy/taskbar mutation fragment is present: $fragment"
    }
}

$handledAssignments = [regex]::Matches($v7Source, 'handled\s*=\s*true\s*;').Count
if ($handledAssignments -ne 1) {
    throw "V7 must contain exactly one targeted handled=true assignment; found $handledAssignments."
}
Write-Host 'Recovery-v7 source validation passed.'

$officialUrl = 'https://github.com/1Remote/1Remote/releases/download/1.2.1/1Remote-1.2.1-net9-x64.zip'
$expectedOfficialSha256 = '825ae0d6d6ed45dbb155ed809b976c2a7532578b124b3b55c0cfc6bfa3267411'
$officialZip = Join-Path $env:RUNNER_TEMP '1Remote-1.2.1-net9-x64.zip'
$officialDir = Join-Path $env:RUNNER_TEMP 'official-1Remote-1.2.1'
$bundleDir = Join-Path $env:RUNNER_TEMP 'official-1Remote-bundle'
$decompiledAssert = Join-Path $env:RUNNER_TEMP 'Assert.decompiled.cs'

Invoke-WebRequest -Uri $officialUrl -OutFile $officialZip -UseBasicParsing
$actualOfficialSha256 = (Get-FileHash $officialZip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualOfficialSha256 -ne $expectedOfficialSha256) {
    throw "Official 1Remote 1.2.1 hash mismatch. Expected $expectedOfficialSha256, got $actualOfficialSha256."
}

Expand-Archive -Path $officialZip -DestinationPath $officialDir -Force
$officialExe = Get-ChildItem -Path $officialDir -Recurse -File -Filter '1Remote.exe' | Select-Object -First 1
if ($null -eq $officialExe) {
    throw '1Remote.exe was not found in the verified official release package.'
}

dotnet tool install --global sfextract
if ($LASTEXITCODE -ne 0) { throw 'Failed to install sfextract.' }
dotnet tool install --global ilspycmd --version 9.1.0.7988
if ($LASTEXITCODE -ne 0) { throw 'Failed to install ilspycmd.' }
$env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"

New-Item -ItemType Directory -Path $bundleDir -Force | Out-Null
& sfextract $officialExe.FullName -o $bundleDir
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to extract the verified official .NET single-file bundle.'
}

$officialAssembly = Get-ChildItem -Path $bundleDir -Recurse -File -Filter '1Remote.dll' | Select-Object -First 1
if ($null -eq $officialAssembly) {
    throw 'The managed 1Remote.dll entry was not found in the verified official bundle.'
}

& ilspycmd -t '_1RM.Assert' $officialAssembly.FullName | Set-Content -Path $decompiledAssert -Encoding UTF8
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to inspect the managed assembly from the verified official bundle.'
}

$decompiledText = Get-Content $decompiledAssert -Raw
$match = [regex]::Match($decompiledText, 'STRING_SALT\s*=\s*"((?:\\.|[^"\\])*)"\s*;')
if (-not $match.Success) {
    throw 'The database-compatible value was not found in the verified official assembly.'
}

$saltLiteral = $match.Groups[1].Value
if ([string]::IsNullOrWhiteSpace($saltLiteral) -or $saltLiteral.Contains('REPLACE_ME_WITH_SALT')) {
    throw 'The recovered database-compatible value is invalid.'
}
Write-Output "::add-mask::$saltLiteral"

$assertPath = Join-Path $workspace 'Ui\Assert.cs'
$assertText = Get-Content $assertPath -Raw
$placeholder = '"===REPLACE_ME_WITH_SALT==="'
$replacement = '"' + $saltLiteral + '"'
if (-not $assertText.Contains($placeholder)) {
    throw 'The source encryption placeholder was not found.'
}
Set-Content -Path $assertPath -Value $assertText.Replace($placeholder, $replacement) -Encoding UTF8
if ((Get-Content $assertPath -Raw).Contains('===REPLACE_ME_WITH_SALT===')) {
    throw 'The database-compatible value was not injected into the source.'
}

Remove-Item $officialZip -Force
Remove-Item $officialDir -Recurse -Force
Remove-Item $bundleDir -Recurse -Force
Remove-Item $decompiledAssert -Force
Write-Host 'Verified official database compatibility value injected.'

Push-Location $workspace
try {
    dotnet restore .\Ui\Ui.csproj -r win-x64 -p:Configuration=Release
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

    dotnet publish .\Ui\Ui.csproj -p:PublishProfile=.\Ui\Properties\PublishProfiles\x64-single.file.application.pubxml --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
}
finally {
    Pop-Location
}

$publishDir = Join-Path $workspace 'Ui\bin\Release\net9.0-windows\publish\win-x64'
$exePath = Join-Path $publishDir '1Remote.exe'
if (-not (Test-Path $exePath)) {
    throw "Published 1Remote.exe was not found: $exePath"
}
if ((Get-Item $exePath).Length -lt 10000000) {
    throw 'Published 1Remote.exe is unexpectedly small.'
}

$verifyBundle = Join-Path $env:RUNNER_TEMP 'recovery-v7-compiled-bundle'
$verifySource = Join-Path $env:RUNNER_TEMP 'TabWindowView.recovery-v7.decompiled.cs'
New-Item -ItemType Directory -Path $verifyBundle -Force | Out-Null
& sfextract $exePath -o $verifyBundle
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to extract the compiled single-file EXE.'
}
$compiledAssembly = Get-ChildItem -Path $verifyBundle -Recurse -File -Filter '1Remote.dll' | Select-Object -First 1
if ($null -eq $compiledAssembly) {
    throw 'The compiled 1Remote.dll entry was not found.'
}
& ilspycmd -t '_1RM.View.Host.TabWindowView' $compiledAssembly.FullName | Set-Content -Path $verifySource -Encoding UTF8
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to decompile the compiled TabWindowView type.'
}

$compiled = Get-Content $verifySource -Raw
$requiredCompiled = @(
    'TaskbarRecoveryV6-early-stable-native-takeover',
    'V6_RECOVERY_POST_SC_MINIMIZE',
    'TaskbarRecoveryV7-late-native-minimize-dedup',
    'V7_RESTORE_AFTER_SYNTHETIC',
    'V7_LATE_NATIVE_SC_MINIMIZE_SUPPRESSED',
    'V7_SC_MINIMIZE_ALLOWED',
    'ShouldSuppressTaskbarRecoveryV7LateMinimize'
)
foreach ($fragment in $requiredCompiled) {
    if (-not $compiled.Contains($fragment)) {
        throw "Compiled EXE is missing recovery-v7 fragment: $fragment"
    }
}

$compiledHandledAssignments = [regex]::Matches($compiled, 'handled\s*=\s*true\s*;').Count
if ($compiledHandledAssignments -lt 1) {
    throw 'Compiled EXE does not contain the targeted V7 message-consumption assignment.'
}

$forbiddenCompiled = @(
    'TaskbarRecoveryV3-reactivation-45ms',
    'TaskbarRecoveryV4-silent-foreground-watchdog',
    'TaskbarRecoveryV5-native-foreground-state-machine',
    'TaskbarTraceV2-',
    'SHGetPropertyStoreForWindow',
    'TaskbarWindowRepair'
)
foreach ($fragment in $forbiddenCompiled) {
    if ($compiled.Contains($fragment)) {
        throw "Compiled EXE contains a removed legacy experiment: $fragment"
    }
}
Remove-Item $verifyBundle -Recurse -Force
Remove-Item $verifySource -Force
Write-Host 'Compiled EXE recovery-v7 verification passed.'

$sourceSha = (git -C $workspace rev-parse HEAD).Trim()
$readmePath = Join-Path $publishDir 'README-Taskbar-Recovery-V7.txt'
$readmeLines = @(
    '1Remote 1.2.1 - Windows 11 25H2 taskbar recovery v7',
    '',
    "Source commit: $sourceSha",
    'Official baseline commit: a2a81be532f7da9016b77657009ccfe09574be9f',
    "Official input SHA-256: $expectedOfficialSha256",
    '',
    'What V7 changes:',
    '- Retains V6 early native recovery at approximately 170-190 ms.',
    '- Suppresses only a proven delayed duplicate SC_MINIMIZE after synthetic recovery and a subsequent real restore.',
    '- Requires the restored HWND to be non-iconic and foreground to remain 0 / taskbar / StartAllBack.',
    '- Always allows a command tied to a new taskbar WA_INACTIVE within 165 ms.',
    '- Always allows title-bar/keyboard minimization when the session itself is foreground.',
    '- The duplicate window is bounded to 1500 ms; measured duplicates were 260.7 ms and 978.5 ms.',
    '- Normal native SC_MINIMIZE, legitimate SC_RESTORE, AppUserModelID, ITaskbarList, ShowInTaskbar, owner and styles remain untouched.',
    '',
    'Trace:',
    '- Primary: application locality .logs\TaskbarRecoveryV6-<PID>-<timestamp>.log',
    '- V7 events are written into the same file with a V7_ prefix.',
    '- Fallback: %TEMP%\1Remote-TaskbarRecoveryV6',
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

$zipPath = Join-Path $workspace '1Remote-1.2.1-win-x64-Win11-25H2-taskbar-recovery-v7.zip'
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Recovery-v7 ZIP: $zipPath"
Write-Host "Recovery-v7 ZIP SHA-256: $zipHash"
