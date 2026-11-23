<#
  Builds the FloatingDock 2.x release (C# engine + Flutter interface), Windows x64.

    .\packaging\release-flutter.ps1 -Out ..\release-2.0.1     # Portable folder + zip + installer + SHA256SUMS in the Out folder

  Needs: the .NET 10 SDK, Flutter (flutter on PATH or in C:\dev\flutter\bin), Inno Setup 6 (ISCC.exe on PATH, in Program Files, or in ..\.build-tools\inno-*).
  The engine is published self-contained; FloatingDock.Engine.exe is renamed to FloatingDock.exe so that shortcuts, the autostart entry and the
  installer of 1.x keep working. The old WinUI application is not part of this release.
#>
param(
  [Parameter(Mandatory)][string]$Out,
  [switch]$SkipTests,
  [switch]$SkipZip,
  [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root
$Out = [IO.Path]::GetFullPath($Out)
$work = Join-Path $root 'artifacts\release-flutter'
$engine = Join-Path $work 'engine'
$portable = Join-Path $Out 'Portable'
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version

if (-not (Get-Command flutter -ErrorAction SilentlyContinue) -and (Test-Path 'C:\dev\flutter\bin')) { $env:Path = "C:\dev\flutter\bin;$env:Path" }
if (Test-Path $Out) { throw "$Out already exists; choose a new folder for a new release." }

if (-not $SkipTests) {
  dotnet test tests\FloatingDock.Ipc.Tests -c Release; if ($LASTEXITCODE) { throw 'IPC tests failed' }
  dotnet test tests\FloatingDock.Core.Tests -c Release; if ($LASTEXITCODE) { throw 'Core tests failed' }
  Push-Location ui_flutter\app
  try { flutter test; if ($LASTEXITCODE) { throw 'Flutter tests failed' } } finally { Pop-Location }
}

# ---- engine (self-contained) and interface
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
dotnet publish src\FloatingDock.Engine\FloatingDock.Engine.csproj -c Release -r win-x64 --self-contained true -o $engine
if ($LASTEXITCODE) { throw 'Engine publish failed' }
Push-Location ui_flutter\app
try { flutter build windows --release --no-tree-shake-icons; if ($LASTEXITCODE) { throw 'Flutter build failed' } } finally { Pop-Location }
$ui = Join-Path $root 'ui_flutter\app\build\windows\x64\runner\Release'
if (-not (Test-Path (Join-Path $ui 'fd_ui.exe'))) { throw 'fd_ui.exe is missing' }

# ---- assemble the portable folder
New-Item -ItemType Directory $portable -Force | Out-Null
robocopy $engine $portable /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'copy of the engine failed' }
robocopy $ui (Join-Path $portable 'ui') /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'copy of the interface failed' }
Get-ChildItem $portable -Recurse -File -Filter '*.pdb' | Remove-Item -Force
Move-Item (Join-Path $portable 'FloatingDock.Engine.exe') (Join-Path $portable 'FloatingDock.exe')
Copy-Item (Join-Path $root 'packaging\portable\*') $portable -Force
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $portable 'LICENSE.txt') -Force

# .NET license files from the restored runtime pack
$nuget = $env:NUGET_PACKAGES
if (-not $nuget) { $nuget = ((& dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', '').Trim() }
$config = Get-Content (Join-Path $portable 'FloatingDock.Engine.runtimeconfig.json') -Raw | ConvertFrom-Json
$runtimeVersion = ($config.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
if (-not $runtimeVersion) { throw 'The .NET runtime is not included (the publication is framework-dependent).' }
$licenses = Join-Path $portable 'Licenses'
New-Item -ItemType Directory $licenses -Force | Out-Null
foreach ($pair in @(@('LICENSE.TXT', 'DotNet-LICENSE.txt'), @('THIRD-PARTY-NOTICES.TXT', 'DotNet-THIRD-PARTY-NOTICES.txt'))) {
  $src = Join-Path $nuget "microsoft.netcore.app.runtime.win-x64\$runtimeVersion\$($pair[0])"
  if (-not (Test-Path $src)) { throw "Missing .NET license file: $src" }
  Copy-Item $src (Join-Path $licenses $pair[1])
}

# ---- checks
foreach ($f in 'FloatingDock.exe', 'FloatingDock.Engine.dll', 'FloatingDock.Engine.runtimeconfig.json', 'coreclr.dll', 'ui\fd_ui.exe', 'ui\flutter_windows.dll', 'ui\data\app.so', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt', 'Restore-Windows-Taskbar.cmd', 'Licenses\DotNet-LICENSE.txt') {
  $p = Join-Path $portable $f
  if (-not (Test-Path $p -PathType Leaf) -or (Get-Item $p).Length -eq 0) { throw "Missing release file: $f" }
}
if ($config.runtimeOptions.framework -or $config.runtimeOptions.frameworks) { throw 'The engine publication is framework-dependent.' }
foreach ($n in 'UserIcons', 'Logs', 'config.json', 'pinned.json', 'HANDOFF.md', '.validation') {
  if (Test-Path (Join-Path $portable $n)) { throw "Private material in the release: $n" }
}
$fileVersion = (Get-Item (Join-Path $portable 'FloatingDock.exe')).VersionInfo.ProductVersion
if ($fileVersion.Split('+')[0] -ne $version) { throw "Engine version $fileVersion differs from the project version $version" }

$files = @()
if (-not $SkipZip) {
  $zip = Join-Path $Out 'FloatingDock-Portable-x64.zip'
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
Write-Host "FloatingDock $version released to $Out"
