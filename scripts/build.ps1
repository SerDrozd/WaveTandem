[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$DotNet = 'dotnet',
    [string]$CMake,
    [ValidateSet('Visual Studio 17 2022', 'Visual Studio 18 2026')][string]$Generator = 'Visual Studio 17 2022',
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
Push-Location $root
try {
    function Invoke-Checked([string]$Program, [string[]]$ArgumentList) {
        & $Program @ArgumentList
        if ($LASTEXITCODE -ne 0) { throw "$Program failed (exit $LASTEXITCODE)." }
    }

    if (!$CMake) {
        $command = Get-Command cmake -ErrorAction SilentlyContinue
        if ($command) {
            $CMake = $command.Source
        } else {
            $vsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
            $installation = & $vsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.CMake.Project -property installationPath
            if (!$installation) { throw 'CMake 3.24 or later is required.' }
            $CMake = Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
        }
    }
    $ctest = Join-Path (Split-Path (Get-Command $CMake).Source) 'ctest.exe'
    Invoke-Checked $CMake @('-S', 'native', '-B', 'out/native', '-G', $Generator, '-A', 'x64')
    Invoke-Checked $CMake @('--build', 'out/native', '--config', $Configuration, '--parallel')
    Invoke-Checked $ctest @('--test-dir', 'out/native', '-C', $Configuration, '--output-on-failure')

    $native = Join-Path $root "out/native/$Configuration/WaveTandemAudio.dll"
    $config = Join-Path $root 'NuGet.Config'
    $app = 'src/WaveTandem.App/WaveTandem.App.csproj'
    $tests = 'tests/WaveTandem.Core.Tests/WaveTandem.Core.Tests.csproj'
    Invoke-Checked $DotNet @('restore', $tests, '--configfile', $config, '--locked-mode')
    Invoke-Checked $DotNet @('run', '--project', $tests, '-c', $Configuration, '--no-restore')
    Invoke-Checked $DotNet @('restore', $app, '--configfile', $config)
    Invoke-Checked $DotNet @('build', $app, '-c', $Configuration, '--no-restore', "-p:NativeAudioPath=$native")

    if ($Package) {
        $publish = Join-Path $root ('out/publish-' + [guid]::NewGuid().ToString('N'))
        $distribution = Join-Path $root 'out/dist'
        New-Item -ItemType Directory -Path $distribution -Force | Out-Null
        Invoke-Checked $DotNet @('publish', $app, '-c', $Configuration, '-r', 'win-x64', '--self-contained',
            '-o', $publish, "-p:NativeAudioPath=$native", "-p:RestoreConfigFile=$config")
        Copy-Item -LiteralPath 'LICENSE','README.md','THIRD_PARTY_NOTICES.md','CONTRIBUTING.md','SECURITY.md' -Destination $publish
        Copy-Item -LiteralPath 'docs' -Destination $publish -Recurse
        $manifest = Get-Content -LiteralPath (Join-Path $publish 'WaveTandem.deps.json') -Raw | ConvertFrom-Json -AsHashtable
        $assets = Get-Content -LiteralPath 'src/WaveTandem.App/obj/project.assets.json' -Raw | ConvertFrom-Json -AsHashtable
        foreach ($runtime in ($manifest.libraries.Keys | Where-Object { $_ -like 'runtimepack.*' })) {
            $relative = $runtime.Substring('runtimepack.'.Length).ToLowerInvariant()
            $pack = $assets.packageFolders.Keys | ForEach-Object { Join-Path $_ $relative } |
                Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
            if (!$pack) { throw "Runtime pack $relative is missing from the restored packages." }
            $notices = Get-ChildItem -LiteralPath $pack -File | Where-Object Name -Match '^(LICENSE(\.TXT)?|THIRD-PARTY-NOTICES\.TXT)$'
            if (!$notices) { throw "No notices found in $relative." }
            $noticeFolder = Join-Path $publish ('notices/' + $relative.Replace('/', '_'))
            New-Item -ItemType Directory -Path $noticeFolder -Force | Out-Null
            $notices | Copy-Item -Destination $noticeFolder
        }
        $archive = Join-Path $distribution 'WaveTandem-win-x64.zip'
        Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive -CompressionLevel Optimal -Force
        $sha = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        "$sha  $([IO.Path]::GetFileName($archive))" | Set-Content "$archive.sha256" -Encoding ascii
        Write-Output $archive
    }
} finally {
    Pop-Location
}
