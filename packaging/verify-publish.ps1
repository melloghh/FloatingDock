param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$required = @('FloatingDock.exe', 'FloatingDock.dll', 'FloatingDock.pri', 'FloatingDock.deps.json', 'FloatingDock.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll', 'Microsoft.WindowsAppRuntime.dll', 'Microsoft.UI.Xaml.dll', 'Microsoft.UI.pri', 'Microsoft.UI.Xaml.Controls.pri', 'Microsoft.WindowsAppRuntime.pri', 'Assets\AppIcon.ico', 'Assets\AppIcon.png')
foreach ($file in $required) {
  $path = Join-Path $Directory $file
  if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw "Missing publish file: $file" }
}
if (Test-Path -LiteralPath (Join-Path $Directory 'IconPacks')) { throw 'Bundled icon packs must not be distributed.' }
$config = Get-Content -LiteralPath (Join-Path $Directory 'FloatingDock.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($config.runtimeOptions.framework -or $config.runtimeOptions.frameworks) { throw 'The publication is framework-dependent.' }
if (-not ($config.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' -and $_.version -like '10.0.*' })) { throw 'The .NET 10 runtime is not included.' }
$deps = Get-Content -LiteralPath (Join-Path $Directory 'FloatingDock.deps.json') -Raw | ConvertFrom-Json
if ($deps.runtimeTarget.name -notlike '*/win-x64') { throw 'The dependency graph is not win-x64.' }
if (Get-ChildItem -LiteralPath $Directory -Recurse -File -Filter '*.pdb') { throw 'Debug symbols must not be distributed.' }
$stream = [IO.File]::OpenRead((Join-Path $Directory 'FloatingDock.exe'))
try {
  $reader = [IO.BinaryReader]::new($stream)
  $stream.Position = 0x3c
  $offset = $reader.ReadInt32()
  $stream.Position = $offset
  if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'FloatingDock.exe is not a Windows x64 PE.' }
} finally { $stream.Dispose() }
Write-Host 'Verified Windows x64 publication, .NET 10, WinUI resources and release files.'
