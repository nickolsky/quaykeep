# Builds the release packages of Quaykeep <Version> (Directory.Build.props) into artifacts\release:
#   Windows  Quaykeep-win-x64-Setup.exe (per user, no admin rights), Quaykeep-win-x64-Portable.zip,
#            Quaykeep-<version>-win-x64.zip + .sha256 (what copies up to 1.0.x update from: keep the name)
#   Linux    Quaykeep-linux-x64.AppImage (cross-built on Windows too)
#   macOS    Quaykeep-osx-arm64.pkg, Quaykeep-osx-x64.pkg (only on macOS: vpk packs a Mac app only there)
# plus Velopack's update feed for each channel (releases.<channel>.json and the .nupkg packages), which the installed
# app reads from the GitHub release. The download page links to the stable names above (releases/latest/download/...).
#
#   pwsh ./release.ps1                          Windows + Linux on Windows/Linux, macOS on a Mac
#   pwsh ./release.ps1 -Platforms osx           only the macOS packages (the release workflow runs this on a Mac)
#   pwsh ./release.ps1 -Publish                 also create the GitHub release v<version> from everything in
#                                               artifacts\release (needs gh, a clean pushed main, and the macOS files)
# Normally the "release" GitHub workflow does all of it (Actions → release → Run workflow).
#
# Optional signing (environment): QK_WIN_SIGN_PARAMS (signtool parameters for vpk --signParams);
# QK_MAC_APP_IDENTITY, QK_MAC_INSTALL_IDENTITY, QK_MAC_NOTARY_PROFILE (Developer ID identities and a notarytool
# keychain profile). Without them the packages are unsigned: SmartScreen and Gatekeeper ask once (see the guide).
param(
    [string[]]$Platforms,
    [switch]$Publish,
    [string]$NotesFile,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

[xml]$props = Get-Content Directory.Build.props
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "No major.minor.patch <Version> in Directory.Build.props" }
$packId = 'QuaykeepApp'   # Velopack id and Windows install folder (%LOCALAPPDATA%\QuaykeepApp); must match AppPaths.PackId
$repoUrl = 'https://github.com/nickolsky/quaykeep'
$assets = 'src/Quaykeep/Assets'
$out = Join-Path $PSScriptRoot 'artifacts/release'
if (-not $Platforms) { $Platforms = if ($IsMacOS) { @('osx') } else { @('win', 'linux') } }
foreach ($p in $Platforms) { if ($p -notin 'win', 'linux', 'osx') { throw "Unknown platform $p (win, linux, osx)" } }
if ('osx' -in $Platforms -and -not $IsMacOS) { throw 'vpk packs a macOS release only on macOS: run -Platforms osx on a Mac (or the release workflow).' }

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE) { throw "$what failed (exit code $LASTEXITCODE)" }
}

# runtime, Velopack channel (one feed each), vpk directive, icon, main program, public file name
$targets = @(
    @{ Platform = 'win'; Rid = 'win-x64'; Channel = 'win'; Directive = '[win]'; Icon = "$assets/app.ico"; Exe = 'Quaykeep.exe' }
    @{ Platform = 'linux'; Rid = 'linux-x64'; Channel = 'linux'; Directive = '[linux]'; Icon = "$assets/app.png"; Exe = 'Quaykeep' }
    @{ Platform = 'osx'; Rid = 'osx-arm64'; Channel = 'osx'; Directive = '[osx]'; Icon = "$assets/app.icns"; Exe = 'Quaykeep' }
    @{ Platform = 'osx'; Rid = 'osx-x64'; Channel = 'osx-x64'; Directive = '[osx]'; Icon = "$assets/app.icns"; Exe = 'Quaykeep' }
) | Where-Object { $_.Platform -in $Platforms }

New-Item -ItemType Directory -Force $out | Out-Null
Invoke-Checked 'dotnet tool restore' { dotnet tool restore --verbosity quiet }
if (-not $SkipTests) { Invoke-Checked 'tests' { dotnet test tests/Quaykeep.Tests -c Release --nologo --verbosity quiet } }

foreach ($t in $targets) {
    $publishDir = Join-Path $PSScriptRoot "artifacts/publish/$($t.Rid)"
    Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
    # the app and the qk helper, self-contained, side by side (qk is the terminal helper, SSH_ASKPASS and the MCP bridge)
    foreach ($project in 'src/Quaykeep', 'src/qk') {
        Invoke-Checked "publish $project $($t.Rid)" {
            dotnet publish $project -c Release -r $t.Rid --self-contained -p:PublishSingleFile=false -o $publishDir --nologo --verbosity quiet
        }
    }
    Copy-Item LICENSE, THIRD-PARTY-NOTICES.md -Destination $publishDir
    Get-ChildItem $publishDir -Recurse -Include *.pdb, instance.txt | Remove-Item -Force

    # the previous release of the channel, for a delta package (none on the first release of a channel)
    if (-not (Get-ChildItem $out -Filter "releases.$($t.Channel).json" -ErrorAction SilentlyContinue)) {
        $token = if ($env:GITHUB_TOKEN) { @('--token', $env:GITHUB_TOKEN) } else { @() }
        dotnet vpk $t.Directive download github --repoUrl $repoUrl -c $t.Channel -o $out @token 2>&1 | Out-Null
        if ($LASTEXITCODE) { Write-Warning "No previous $($t.Channel) release on GitHub: this one has no delta package." }
    }

    $common = @('-u', $packId, '-v', $version, '-r', $t.Rid, '-c', $t.Channel, '-p', $publishDir, '-e', $t.Exe, '-o', $out,
        '--packTitle', 'Quaykeep', '--packAuthors', 'Quaykeep', '-i', $t.Icon)
    if ($NotesFile) { $common += @('--releaseNotes', $NotesFile) }
    switch ($t.Platform) {
        'win' {
            $sign = if ($env:QK_WIN_SIGN_PARAMS) { @('--signParams', $env:QK_WIN_SIGN_PARAMS) } else { @() }
            Invoke-Checked 'vpk pack win' { dotnet vpk $t.Directive pack @common --shortcuts StartMenuRoot @sign }
        }
        'linux' { Invoke-Checked 'vpk pack linux' { dotnet vpk $t.Directive pack @common --categories 'Network;RemoteAccess' } }
        'osx' {
            $sign = @()
            if ($env:QK_MAC_APP_IDENTITY) { $sign += @('--signAppIdentity', $env:QK_MAC_APP_IDENTITY) }
            if ($env:QK_MAC_INSTALL_IDENTITY) { $sign += @('--signInstallIdentity', $env:QK_MAC_INSTALL_IDENTITY) }
            if ($env:QK_MAC_NOTARY_PROFILE) { $sign += @('--notaryProfile', $env:QK_MAC_NOTARY_PROFILE) }
            Invoke-Checked "vpk pack $($t.Rid)" { dotnet vpk $t.Directive pack @common --bundleId app.quaykeep @sign }
        }
    }

    # stable public names for the download page; Velopack's feed only refers to the .nupkg files
    $installer = switch ($t.Platform) {
        'win' { @{ From = "$packId-$($t.Channel)-Setup.exe"; To = 'Quaykeep-win-x64-Setup.exe' }, @{ From = "$packId-$($t.Channel)-Portable.zip"; To = 'Quaykeep-win-x64-Portable.zip' } }
        'linux' { @{ From = "$packId*.AppImage"; To = 'Quaykeep-linux-x64.AppImage' } }
        'osx' { @{ From = "$packId-$($t.Channel)-Setup.pkg"; To = "Quaykeep-$($t.Rid).pkg" } }
    }
    foreach ($i in $installer) {
        $file = Get-ChildItem $out -Filter $i.From -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $file) { throw "vpk made no $($i.From) in $out" }
        Move-Item $file.FullName (Join-Path $out $i.To) -Force
    }
    Get-ChildItem $out -Filter "$packId-$($t.Channel)-Portable.zip" -File | Remove-Item -Force   # osx: the .pkg is enough

    if ($t.Platform -eq 'win') {
        # the package that copies up to 1.0.x download and unpack over themselves (UpdateService)
        $name = "Quaykeep-$version-win-x64"
        $staging = Join-Path $PSScriptRoot "artifacts/zip/$name"
        Remove-Item (Split-Path $staging) -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force $staging | Out-Null
        Copy-Item $publishDir (Join-Path $staging 'Quaykeep') -Recurse
        Copy-Item README.md (Join-Path $staging 'Quaykeep')
        $zip = Join-Path $out "$name.zip"
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
        Compress-Archive -Path (Join-Path $staging 'Quaykeep') -DestinationPath $zip -CompressionLevel Optimal
        $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -Path "$zip.sha256" -Value "$hash  $name.zip" -Encoding ascii -NoNewline
    }
}

Write-Host "`nQuaykeep $version ($($Platforms -join ', ')) is in $out" -ForegroundColor Green
Get-ChildItem $out -File | Sort-Object Name | ForEach-Object { Write-Host ("  {0,-52} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) }

if ($Publish) {
    if (git status --porcelain) { throw 'Commit your changes first' }
    foreach ($need in 'Quaykeep-win-x64-Setup.exe', 'Quaykeep-linux-x64.AppImage', 'Quaykeep-osx-arm64.pkg') {
        if (-not (Test-Path (Join-Path $out $need))) { throw "$need is missing: build every platform first (the macOS ones on a Mac)" }
    }
    $notes = if ($NotesFile) { @('--notes-file', $NotesFile) } else { @('--generate-notes') }
    # packages of older versions (downloaded for the deltas) stay on their own releases
    $files = Get-ChildItem $out -File | Where-Object { $_.Extension -ne '.nupkg' -or $_.Name -like "*-$version-*" } | ForEach-Object { $_.FullName }
    gh release create "v$version" @files --title "Quaykeep $version" @notes
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
