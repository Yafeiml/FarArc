$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Write-Host "Building 1Remote 1.2.1 taskbar trace v2"
Write-Host "SDK: $(dotnet --version)"

$workspace = $env:GITHUB_WORKSPACE
if ([string]::IsNullOrWhiteSpace($workspace)) {
    $workspace = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}

$sourcePath = Join-Path $workspace 'Ui\View\Host\TabWindowView.taskbar_trace_v2.cs'
if (-not (Test-Path $sourcePath)) {
    throw "Taskbar trace v2 source file was not found: $sourcePath"
}

$source = Get-Content $sourcePath -Raw
$requiredSource = @(
    'TaskbarTraceV2-',
    'FOCUS_TIMER_REPLACED',
    'FOCUS_CORRECTION_BLOCKED',
    'INACTIVE_ELIGIBLE',
    'INACTIVE_REJECTED',
    'COMPENSATE_BEGIN',
    'WindowState = WindowState.Minimized',
    'EntryPoint = "GetCursorPos"',
    'Timer4CheckForegroundWindowOnElapsed',
    'SafeFocusTimerOnElapsed'
)
foreach ($fragment in $requiredSource) {
    if (-not $source.Contains($fragment)) {
        throw "Required trace-v2 source fragment is missing: $fragment"
    }
}

$forbiddenSource = @(
    'GuardGetCursorPos',
    'SHGetPropertyStoreForWindow',
    'interface ITaskbarList',
    'ShowInTaskbar =',
    'SetCurrentProcessExplicitAppUserModelID'
)
foreach ($fragment in $forbiddenSource) {
    if ($source.Contains($fragment)) {
        throw "Forbidden legacy/taskbar mutation fragment is present: $fragment"
    }
}
Write-Host 'Source validation passed.'

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

$verifyBundle = Join-Path $env:RUNNER_TEMP 'trace-v2-compiled-bundle'
$verifySource = Join-Path $env:RUNNER_TEMP 'TabWindowView.trace-v2.decompiled.cs'
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
    'TaskbarTraceV2-',
    'FOCUS_TIMER_REPLACED',
    'FOCUS_CORRECTION_BLOCKED',
    'INACTIVE_ELIGIBLE',
    'INACTIVE_REJECTED',
    'COMPENSATE_BEGIN',
    'SafeFocusTimerOnElapsed'
)
foreach ($fragment in $requiredCompiled) {
    if (-not $compiled.Contains($fragment)) {
        throw "Compiled EXE is missing trace-v2 fragment: $fragment"
    }
}
if ($compiled.Contains('SHGetPropertyStoreForWindow')) {
    throw 'Compiled EXE contains the removed AppUserModelID experiment.'
}
Remove-Item $verifyBundle -Recurse -Force
Remove-Item $verifySource -Force
Write-Host 'Compiled EXE verification passed.'

$sourceSha = (git -C $workspace rev-parse HEAD).Trim()
$readmePath = Join-Path $publishDir 'README-Taskbar-Trace-V2.txt'
$readmeLines = @(
    '1Remote 1.2.1 - Windows 11 25H2 taskbar trace/fix v2',
    '',
    "Source commit: $sourceSha",
    "Official input SHA-256: $expectedOfficialSha256",
    '',
    'Changes:',
    '- Based directly on the official 1Remote 1.2.1 commit.',
    '- Removes the unsuccessful AppUserModelID experiment.',
    '- Blocks the official 100 ms protocol-focus mutation while the pointer is on a taskbar.',
    '- Retains a broad missed-minimize fallback for Normal and Maximized remote windows.',
    '- Writes an independent AutoFlush trace for every remote session window.',
    '',
    'Trace location:',
    '- Primary: application locality .logs\TaskbarTraceV2-<PID>-<timestamp>.log',
    '- Fallback: %TEMP%\1Remote-TaskbarTraceV2\TaskbarTraceV2-<PID>-<timestamp>.log',
    '',
    'After reproducing, upload the TaskbarTraceV2 log even when the issue appears fixed.',
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

$zipPath = Join-Path $workspace '1Remote-1.2.1-win-x64-Win11-25H2-taskbar-trace-v2.zip'
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Trace-v2 ZIP: $zipPath"
Write-Host "Trace-v2 ZIP SHA-256: $zipHash"
