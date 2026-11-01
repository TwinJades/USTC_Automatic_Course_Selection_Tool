param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Dotnet = "dotnet"
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location -LiteralPath $workspace
if ($Tag -cnotmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "Release tag must use vMAJOR.MINOR.PATCH: $Tag"
}
$version = $Tag.Substring(1)
$project = "src/UstcCourseAssistant.Route2/UstcCourseAssistant.Route2.csproj"
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
foreach ($field in @("Version", "AssemblyVersion", "FileVersion")) {
    $nodes = @($projectXml.SelectNodes("/Project/PropertyGroup/$field"))
    $expected = if ($field -eq "Version") { $version } else { "$version.0" }
    if ($nodes.Count -ne 1 -or $nodes[0].InnerText -cne $expected) {
        throw "Tag $Tag requires exactly one ${field}=$expected in $project"
    }
}

$stage = Join-Path $workspace ("artifacts/release/" + [Guid]::NewGuid().ToString("N"))
$publish = Join-Path $stage "publish"
New-Item -ItemType Directory -Path $stage | Out-Null
$notes = Join-Path $stage "release-notes.md"
& python tools/release/release_notes.py --tag $Tag --output $notes
if ($LASTEXITCODE -ne 0) { throw "Release notes generation failed ($LASTEXITCODE)." }
& $Dotnet restore UstcCourseAssistant.sln
if ($LASTEXITCODE -ne 0) { throw "Solution restore failed ($LASTEXITCODE)." }
& $Dotnet build UstcCourseAssistant.sln --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "Solution build failed ($LASTEXITCODE)." }
& $Dotnet run --project tests/UstcCourseAssistant.Route2.EngineChecks --configuration Release --no-build -- --skip-ui
if ($LASTEXITCODE -ne 0) { throw "Offline checks failed ($LASTEXITCODE)." }
& $Dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --output $publish `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Windows x64 single-file publish failed ($LASTEXITCODE)." }

$executableName = "USTC_Automatic_Course_Selection_Tool_$version.exe"
$executable = Join-Path $stage $executableName
Move-Item -LiteralPath (Join-Path $publish "UstcCourseAssistant.Route2.exe") -Destination $executable
$archiveName = "USTC_Automatic_Course_Selection_Tool_$version.zip"
$archive = Join-Path $stage $archiveName
Compress-Archive -LiteralPath $executable -DestinationPath $archive -CompressionLevel Optimal
& python tools/release/verify_archive.py --tag $Tag --archive $archive
if ($LASTEXITCODE -ne 0) { throw "Release archive validation failed ($LASTEXITCODE)." }

Add-Type -AssemblyName Microsoft.VisualBasic
$release = Join-Path $workspace "release"
New-Item -ItemType Directory -Path $release -Force | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $release -Force) {
    if ($item.PSIsContainer) {
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($item.FullName, 'OnlyErrorDialogs', 'SendToRecycleBin')
    }
    else {
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($item.FullName, 'OnlyErrorDialogs', 'SendToRecycleBin')
    }
}
$destination = Join-Path $release $archiveName
Move-Item -LiteralPath $archive -Destination $destination
Copy-Item -LiteralPath $notes -Destination (Join-Path $workspace "artifacts/release-notes.md") -Force
Write-Output "Release archive: $destination"
Write-Output "Release notes: artifacts/release-notes.md"
