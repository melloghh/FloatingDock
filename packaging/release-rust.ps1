<#
  Builds the FloatingDock 2.x release with the Rust engine, Windows x64.

    .\packaging\release-rust.ps1 -Out ..\release-2.0.1 -Ui ..\release-2.0.1\Portable\ui

  The Flutter interface is not built here: -Ui is a folder with a finished build of it (fd_ui.exe, flutter_windows.dll, data\).
  Output in -Out: Portable\ (engine as FloatingDock.exe + ui\ + Assets\), FloatingDock-Portable-x64.zip, FloatingDock-Setup-x64.exe, SHA256SUMS.txt.
  An existing Portable folder, zip and installer in -Out are replaced (the new Portable is assembled in artifacts\ first).
  Needs: Rust (cargo on PATH or in %USERPROFILE%\.cargo\bin) and Inno Setup 6 (ISCC.exe on PATH, in Program Files, or in ..\.build-tools\inno-*).
#>
param(
  [Parameter(Mandatory)][string]$Out,
  [Parameter(Mandatory)][string]$Ui,
  [switch]$SkipTests,
  [switch]$SkipZip,
  [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root
$Out = [IO.Path]::GetFullPath($Out)
$Ui = [IO.Path]::GetFullPath($Ui)
$version = (Select-String -Path (Join-Path $root 'rust\Cargo.toml') -Pattern '^version\s*=\s*"([^"]+)"' | Select-Object -First 1).Matches[0].Groups[1].Value
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { $env:Path = "$env:USERPROFILE\.cargo\bin;$env:Path" }
foreach ($f in 'fd_ui.exe', 'flutter_windows.dll') { if (-not (Test-Path (Join-Path $Ui $f))) { throw "$f is missing in $Ui" } }

Push-Location (Join-Path $root 'rust')
$ErrorActionPreference = 'Continue'   # cargo reports progress on stderr; the exit code decides
try {
  if (-not $SkipTests) { cargo test --workspace 2>&1 | Out-Host; if ($LASTEXITCODE) { throw 'Rust tests failed' } }
  cargo build --release -p fd-engine 2>&1 | Out-Host; if ($LASTEXITCODE) { throw 'Engine build failed' }
} finally { Pop-Location; $ErrorActionPreference = 'Stop' }
$engine = Join-Path $root 'rust\target\release\fd-engine.exe'

# ---- assemble the portable folder in a work folder
$work = Join-Path $root 'artifacts\release-rust'
$staging = Join-Path $work 'Portable'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory $staging -Force | Out-Null
Copy-Item $engine (Join-Path $staging 'FloatingDock.exe')
robocopy $Ui (Join-Path $staging 'ui') /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'copy of the interface failed' }
Get-ChildItem $staging -Recurse -File -Filter '*.pdb' | Remove-Item -Force
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory (Join-Path $staging 'Assets') | Out-Null
foreach ($a in 'AppIcon.png', 'Explorer.png') { Copy-Item (Join-Path $assets $a) (Join-Path $staging "Assets\$a") }
Copy-Item (Join-Path $root 'packaging\portable\*') $staging -Force
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $staging 'LICENSE.txt') -Force
$readme = Join-Path $staging 'README-Portable.txt'
$text = (Get-Content $readme -Raw -Encoding UTF8) -replace 'the \.NET runtime is inside this folder', 'the engine is a single native program, no runtime is needed'
Set-Content $readme $text -Encoding UTF8

# ---- checks
foreach ($f in 'FloatingDock.exe', 'ui\fd_ui.exe', 'ui\flutter_windows.dll', 'Assets\Explorer.png', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt', 'Restore-Windows-Taskbar.cmd') {
  $p = Join-Path $staging $f
  if (-not (Test-Path $p -PathType Leaf) -or (Get-Item $p).Length -eq 0) { throw "Missing release file: $f" }
}
foreach ($n in 'UserIcons', 'Logs', 'config.json', 'pinned.json', 'HANDOFF.md', '.validation') {
  if (Test-Path (Join-Path $staging $n)) { throw "Private material in the release: $n" }
}
$fileVersion = (Get-Item (Join-Path $staging 'FloatingDock.exe')).VersionInfo.ProductVersion
if ($fileVersion.Split('+')[0] -ne $version) { throw "Engine version $fileVersion differs from the workspace version $version" }

# ---- replace the release
New-Item -ItemType Directory $Out -Force | Out-Null
$portable = Join-Path $Out 'Portable'
if (Test-Path $portable) { Remove-Item $portable -Recurse -Force }
Move-Item $staging $portable
foreach ($old in 'Portable-Rust') { if (Test-Path (Join-Path $Out $old)) { Remove-Item (Join-Path $Out $old) -Recurse -Force } }

$files = @()
if (-not $SkipZip) {
  $zip = Join-Path $Out 'FloatingDock-Portable-x64.zip'
  if (Test-Path $zip) { Remove-Item $zip -Force }
  Compress-Archive -Path "$portable\*" -DestinationPath $zip
  $files += $zip
}
if (-not $SkipInstaller) {
  $iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
  $path = $null
  if ($iscc) { $path = $iscc.Source }
  else {
    $candidates = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe")
    $candidates += @(Get-ChildItem (Join-Path $root '..\.build-tools\inno-*\ISCC.exe') -ErrorAction SilentlyContinue | ForEach-Object FullName)
    $path = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
  }
  if (-not $path) { throw 'ISCC.exe (Inno Setup 6) not found' }
  & $path "/DAppVersion=$version" "/DSourceDir=$portable" "/O$Out" (Join-Path $root 'packaging\installer\FloatingDock.iss')
  if ($LASTEXITCODE) { throw 'Installer build failed' }
  $files += Join-Path $Out 'FloatingDock-Setup-x64.exe'
}
$files | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_ -Algorithm SHA256).Hash.ToLower(), [IO.Path]::GetFileName($_) } | Set-Content (Join-Path $Out 'SHA256SUMS.txt')
Write-Host "FloatingDock $version (Rust engine) released to $Out"
