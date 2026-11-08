param([Parameter(Mandatory=$true)][string]$WorkDirectory, [Parameter(Mandatory=$true)][string]$Dotnet)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$work = [IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path -LiteralPath $work) { throw 'WorkDirectory must not exist.' }
New-Item -ItemType Directory -Path $work | Out-Null
if ((Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).'USTC Course Assistant Route2') { throw 'Disable the existing Route2 startup registration before running this smoke test; desktop startup rewrites it.' }
if (Get-Process -Name 'UstcCourseAssistant.Route2' -ErrorAction SilentlyContinue) { throw 'Close existing Route2 processes before running this smoke test; the application has a global single-instance mutex.' }
function Wait-FixtureProcess($process, [string]$description) {
    $null = $process.Handle
    if (-not $process.WaitForExit(120000)) { throw "$description did not exit in 120 seconds; process left running for inspection." }
    $process.Refresh()
    if ($null -eq $process.ExitCode) { throw "$description exit code unavailable." }
}
function Stop-SmokeDesktop([string]$root) {
    $exe = [IO.Path]::GetFullPath((Join-Path $root 'app/UstcCourseAssistant.Route2.exe'))
    Get-CimInstance Win32_Process -Filter "Name='UstcCourseAssistant.Route2.exe'" | Where-Object { $_.ExecutablePath -eq $exe } | ForEach-Object { Stop-Process -Id $_.ProcessId; Wait-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
}
function Initialize-SmokeData([string]$root) {
    # A normal desktop startup otherwise migrates and recycles the user's legacy data.
    $source = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'USTC Course Assistant Route2'
    $state = @{ SourceRoot = $source; Phase = 4; Files = @() } | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText((Join-Path $root 'data/migration-state.json'), $state)
    $preferences = @{ OnboardingCompleted = $true; AutoLoginEnabled = $false } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $root 'data/preferences.json'), $preferences)
}
$packages = @{}
foreach ($version in @('2.2.1','2.2.2')) {
    Write-Output "Publishing application $version"
    $root = Join-Path $work $version
    New-Item -ItemType Directory -Path (Join-Path $root 'app'),(Join-Path $root 'data') -Force | Out-Null
    & $Dotnet publish (Join-Path $repo 'src/UstcCourseAssistant.Route2/UstcCourseAssistant.Route2.csproj') -c Release -r win-x64 --self-contained true -p:Version=$version -p:AssemblyVersion="$version.0" -p:FileVersion="$version.0" -o (Join-Path $root 'app')
    if ($LASTEXITCODE) { throw 'Application publish failed.' }
    & $Dotnet publish (Join-Path $PSScriptRoot 'UstcCourseAssistant.UpdateSmoke.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $work "fixture-$version")
    if ($LASTEXITCODE) { throw 'Fixture publish failed.' }
    Write-Output "Preparing launcher $version"
    $launcher = 'USTC' + [char]0x9009 + [char]0x8BFE + [char]0x52A9 + [char]0x624B + '.cmd'
    Copy-Item -LiteralPath (Join-Path (Join-Path $repo 'tools/release/launchers') $launcher) -Destination $root
    $zip = Join-Path $work "USTC_Automatic_Course_Selection_Tool_${version}_win-x64.zip"
    Write-Output "Creating archive $version"
    & python (Join-Path $repo 'tools/release/package_directory.py') --root $root --archive $zip --runtime win-x64 --version $version
    if ($LASTEXITCODE) { throw 'Package generation failed.' }
    Write-Output "Archive created $version"
    $packages[$version] = $zip
}
$root = Join-Path $work '2.2.1'
Initialize-SmokeData $root
[IO.File]::WriteAllText((Join-Path $root 'data/sentinel'), 'unchanged')
$hash = (Get-FileHash -LiteralPath $packages['2.2.2'] -Algorithm SHA256).Hash
Expand-Archive -LiteralPath $packages['2.2.1'] -DestinationPath (Join-Path $work 'old-extracted')
Copy-Item -LiteralPath (Join-Path $work 'old-extracted/package-manifest.json') -Destination $root
$stdout = Join-Path $work 'process-stdout.log'
$stderr = Join-Path $work 'process-stderr.log'
$proc = Start-Process -FilePath (Join-Path $work 'fixture-2.2.1/UstcCourseAssistant.Route2.exe') -ArgumentList @('--package-root', ('"'+$root+'"'), '--prepare', '--archive', ('"'+$packages['2.2.2']+'"'), '--sha256', $hash) -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
Wait-FixtureProcess $proc 'Preparing update'
if ($proc.ExitCode -ne 0) { throw ("Preparing update failed (exit $($proc.ExitCode)): " + (Get-Content -Raw -ErrorAction SilentlyContinue $stderr)) }
$deadline = [DateTime]::UtcNow.AddSeconds(90)
while ((Test-Path -LiteralPath (Join-Path $root 'data/updates/active')) -or (Get-Item (Join-Path $root 'app/UstcCourseAssistant.Route2.exe') -ErrorAction SilentlyContinue).VersionInfo.FileVersion -ne '2.2.2.0') {
    $errorLog = Join-Path $root 'data/smoke-error.log'
    if (Test-Path -LiteralPath $errorLog) { throw ('Fixture failed: ' + (Get-Content -Raw -LiteralPath $errorLog)) }
    if ([DateTime]::UtcNow -gt $deadline) { throw ('Update did not complete. stdout: ' + (Get-Content -Raw -ErrorAction SilentlyContinue $stdout) + ' stderr: ' + (Get-Content -Raw -ErrorAction SilentlyContinue $stderr)) }
    Start-Sleep -Milliseconds 250
}
Stop-SmokeDesktop $root
if ((Get-Content -Raw -LiteralPath (Join-Path $root 'data/sentinel')) -ne 'unchanged') { throw 'Business data changed.' }
if (Test-Path -LiteralPath (Join-Path $root 'data/updates/active')) { throw 'Active update remains.' }
foreach ($fault in @('downloading','missing-journal','old-app','new-app','ready','committed')) {
    Write-Output "Testing recovery: $fault"
    $faultRoot = Join-Path $work "O'Brien-fault-$fault"
    Expand-Archive -LiteralPath $packages['2.2.1'] -DestinationPath $faultRoot
    Initialize-SmokeData $faultRoot
    [IO.File]::WriteAllText((Join-Path $faultRoot 'data/sentinel'), 'unchanged')
    $fixtureFault = if ($fault -eq 'missing-journal') { 'downloading' } else { $fault }
    $fixture = Start-Process -FilePath (Join-Path $work 'fixture-2.2.1/UstcCourseAssistant.Route2.exe') -ArgumentList @('--package-root', ('"'+$faultRoot+'"'), '--fault', $fixtureFault, '--archive', ('"'+$packages['2.2.2']+'"'), '--sha256', $hash) -PassThru
    Wait-FixtureProcess $fixture "Fault fixture $fault"
    if ($null -eq $fixture.ExitCode -or $fixture.ExitCode -ne 0) {
        $failure = Join-Path $faultRoot 'data/smoke-error.log'
        throw "Fault fixture failed: $fault (exit $($fixture.ExitCode)): $(Get-Content -Raw -ErrorAction SilentlyContinue $failure)"
    }
    $active = Join-Path $faultRoot 'data/updates/active'
    $backup = Join-Path $active 'backup'
    $payload = Join-Path $active 'payload'
    if ($fault -eq 'missing-journal') {
        Add-Type -AssemblyName Microsoft.VisualBasic
        $journalPath = Join-Path $active 'journal.json'
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($journalPath, 'OnlyErrorDialogs', 'SendToRecycleBin')
    } elseif ($fault -ne 'downloading') {
        New-Item -ItemType Directory -Path $backup | Out-Null
        $journalPath = Join-Path $active 'journal.json'
        $journal = Get-Content -Raw -LiteralPath $journalPath | ConvertFrom-Json
        $journal.Stage = 'Switching'
        [IO.File]::WriteAllText($journalPath, ($journal | ConvertTo-Json -Depth 10))
        Move-Item -LiteralPath (Join-Path $faultRoot 'app') -Destination (Join-Path $backup 'app')
        if ($fault -ne 'old-app') { Move-Item -LiteralPath (Join-Path $payload 'app') -Destination (Join-Path $faultRoot 'app') }
    }
    if ($fault -in @('ready','committed')) {
        Move-Item -LiteralPath (Join-Path $faultRoot 'package-manifest.json') -Destination (Join-Path $backup 'package-manifest.json')
        Move-Item -LiteralPath (Join-Path $payload 'package-manifest.json') -Destination (Join-Path $faultRoot 'package-manifest.json')
        $journal.Stage = if ($fault -eq 'ready') { 'PendingReady' } else { 'Committed' }
        [IO.File]::WriteAllText($journalPath, ($journal | ConvertTo-Json -Depth 10))
    }
    $recoveryOut = Join-Path $work "recovery-$fault.stdout.log"
    $recoveryErr = Join-Path $work "recovery-$fault.stderr.log"
    $launcherPath = Join-Path $faultRoot $launcher
    $recovery = Start-Process -FilePath 'cmd.exe' -ArgumentList @('/d', '/c', ('""'+$launcherPath+'""')) -RedirectStandardOutput $recoveryOut -RedirectStandardError $recoveryErr -PassThru
    Wait-FixtureProcess $recovery "Recovery $fault"
    if ($recovery.ExitCode -ne 0) { throw "Recovery failed: $fault (exit $($recovery.ExitCode)): $(Get-Content -Raw -ErrorAction SilentlyContinue $recoveryErr)" }
    $cleanupDeadline = [DateTime]::UtcNow.AddSeconds(60)
    while (Test-Path -LiteralPath $active) {
        if ([DateTime]::UtcNow -gt $cleanupDeadline) { throw "Recovery cleanup timed out: $fault" }
        Start-Sleep -Milliseconds 250
    }
    Stop-SmokeDesktop $faultRoot
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $faultRoot 'package-manifest.json') | ConvertFrom-Json
    $expected = if ($fault -eq 'committed') { '2.2.2' } else { '2.2.1' }
    if ($manifest.Version -ne $expected -or (Get-Item (Join-Path $faultRoot 'app/UstcCourseAssistant.Route2.exe')).VersionInfo.FileVersion -ne "$expected.0" -or (Get-Content -Raw (Join-Path $faultRoot 'data/sentinel')) -ne 'unchanged') { throw "Recovery verification failed: $fault" }
}
Write-Output 'A to B update committed; data preserved; active transaction recycled.'
