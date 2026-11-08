param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][ValidateSet('win-x64','linux-x64')][string]$Runtime,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$hostWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Set-Location -LiteralPath $workspace
if ($Tag -cnotmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Tag must be vMAJOR.MINOR.PATCH.' }
$version = $Tag.Substring(1)
$project = 'src/UstcCourseAssistant.Route2/UstcCourseAssistant.Route2.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
foreach ($field in @('Version','AssemblyVersion','FileVersion')) {
    $nodes = @($projectXml.SelectNodes("/Project/PropertyGroup/$field"))
    $expected = if ($field -eq 'Version') { $version } else { "$version.0" }
    if ($nodes.Count -ne 1 -or $nodes[0].InnerText -cne $expected) { throw "Tag requires $field=$expected." }
}
$artifacts = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$comparison = if ($hostWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
if (-not $output.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, $comparison)) { throw 'OutputDirectory must be inside artifacts.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$archive = Join-Path $output "USTC_Automatic_Course_Selection_Tool_${version}_${Runtime}.zip"
if (Test-Path -LiteralPath $archive) { throw 'Candidate archive already exists; choose a new output directory.' }
$stage = Join-Path $artifacts ('release-stage/' + [Guid]::NewGuid().ToString('N'))
$root = Join-Path $stage 'package'
$app = Join-Path $root 'app'
New-Item -ItemType Directory -Path $app,(Join-Path $root 'data') -Force | Out-Null
$common = @('--configuration','Release','--runtime',$Runtime,'--self-contained','true','-p:PublishSingleFile=false','-p:IncludeNativeLibrariesForSelfExtract=false','-p:DebugType=None','-p:DebugSymbols=false',"-p:Version=$version","-p:AssemblyVersion=$version.0","-p:FileVersion=$version.0")
if ($Runtime -eq 'win-x64') {
    & $Dotnet publish $project @common --output $app
    if ($LASTEXITCODE -ne 0) { throw 'GUI publish failed.' }
} else {
    & $Dotnet publish 'src/UstcCourseAssistant.Cli/UstcCourseAssistant.Cli.csproj' @common --output $app
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
}
if ($Runtime -eq 'win-x64') {
    $launcher = 'USTC' + [char]0x9009 + [char]0x8BFE + [char]0x52A9 + [char]0x624B + '.cmd'
    Copy-Item -LiteralPath (Join-Path 'tools/release/launchers' $launcher) -Destination $root
} else {
    Copy-Item -LiteralPath 'tools/release/launchers/ustc-assistant' -Destination $root
    Copy-Item -LiteralPath 'service' -Destination $root -Recurse
    if (-not $hostWindows) {
        & chmod 755 (Join-Path $root 'ustc-assistant')
        if ($LASTEXITCODE -ne 0) { throw 'Launcher chmod failed.' }
    }
}
& python tools/release/package_directory.py --root $root --archive $archive --runtime $Runtime --version $version
if ($LASTEXITCODE -ne 0) { throw 'Package creation failed.' }
& python tools/release/verify_archive.py --tag $Tag --runtime $Runtime --archive $archive
if ($LASTEXITCODE -ne 0) { throw 'Package verification failed.' }
$release = Join-Path $workspace 'release'
New-Item -ItemType Directory -Path $release -Force | Out-Null
$destination = Join-Path $release (Split-Path $archive -Leaf)
if (Test-Path -LiteralPath $destination) {
    if ($hostWindows) {
        Add-Type -AssemblyName Microsoft.VisualBasic
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($destination, 'OnlyErrorDialogs', 'SendToRecycleBin')
    } else {
        Remove-Item -LiteralPath $destination -Force
    }
}
Copy-Item -LiteralPath $archive -Destination $destination
foreach ($stale in Get-ChildItem -LiteralPath $release -Filter 'USTC_Automatic_Course_Selection_Tool_*.zip' -File) {
    if ($stale.FullName -eq $destination) { continue }
    if ($hostWindows) {
        Add-Type -AssemblyName Microsoft.VisualBasic
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($stale.FullName, 'OnlyErrorDialogs', 'SendToRecycleBin')
    } else {
        Remove-Item -LiteralPath $stale.FullName -Force
    }
}
Write-Output "Release archive: $destination"
Write-Output "Candidate archive: $archive"
