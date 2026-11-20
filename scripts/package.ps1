[CmdletBinding()]
param([switch]$NoRestore)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/NvlogDetector/NvlogDetector.csproj'
[xml]$projectXml = Get-Content -Raw -LiteralPath $project
$version = [string]$projectXml.Project.PropertyGroup.Version
$output = Join-Path $repoRoot "artifacts/release/nvlog-detector-$version-win-x64"
New-Item -ItemType Directory -Path $output -Force | Out-Null

$publishArguments = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $output, '-p:DebugType=None', '-p:DebugSymbols=false')
if ($NoRestore) { $publishArguments += '--no-restore' }
dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw 'Application publishing failed.' }
foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $output
}

$licenses = Join-Path $output 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
foreach ($name in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    $source = Join-Path $dotnetRoot $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $licenses "dotnet-$name") }
}
$assets = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'src/NvlogDetector/obj/project.assets.json') | ConvertFrom-Json
$packageRoot = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    $package = Join-Path $packageRoot $library.Value.path
    foreach ($notice in (Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match '^(LICENSE|ThirdPartyNotices)' })) {
        $prefix = $library.Name.Replace('/', '-')
        Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $licenses "$prefix-$($notice.Name)")
    }
}

$archive = "$output.zip"
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
Write-Output $archive
Write-Output "$archive.sha256"
