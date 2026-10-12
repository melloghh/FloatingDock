<#
  Builds FloatingDock for Windows x64 - the canonical release build (run it on Windows 10 1809+ / 11 with the .NET 10 SDK).

    .\packaging\publish.ps1                  # self-contained folder in .\artifacts\FloatingDock
    .\packaging\publish.ps1 -Zip             # + .\artifacts\FloatingDock-Portable-x64.zip   (unzip and run, nothing to install)
    .\packaging\publish.ps1 -Installer       # + .\artifacts\FloatingDock-Setup-x64.exe      (needs ISCC.exe = Inno Setup 6 on PATH)
    .\packaging\publish.ps1 -Zip -Installer -SkipTests

  "Self-contained" = the .NET runtime AND the Windows App SDK runtime are inside the output folder; the user installs nothing.
  The script verifies the result (key files, manifest, no leftover debug output) and writes SHA-256 sums.
#>
param(
  [string]$Configuration = 'Release',
  [string]$Runtime = 'win-x64',
  [switch]$Zip,
  [switch]$Installer,
  [switch]$SkipTests,
  [switch]$SmokeTest
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$artifacts = Join-Path $root 'artifacts'
$out  = Join-Path $artifacts 'FloatingDock'
Set-Location $root
if ([Environment]::OSVersion.Platform -ne 'Win32NT') { throw 'A runnable WinUI release must be built on Windows.' }
if ($Runtime -ne 'win-x64') { throw 'Only win-x64 is supported by this release script and installer.' }

if (-not $SkipTests) {
  dotnet test FloatingDock.sln -c $Configuration -p:Platform=x64 --logger trx --results-directory (Join-Path $artifacts 'test-results')
  if ($LASTEXITCODE) { throw 'Unit tests failed' }
}

if ([IO.Path]::GetFullPath($out) -ne [IO.Path]::GetFullPath((Join-Path $root 'artifacts\FloatingDock'))) { throw 'Unexpected publish directory.' }
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Force $artifacts | Out-Null
dotnet publish src/FloatingDock.App -c $Configuration -r $Runtime --self-contained true -p:WindowsAppSDKSelfContained=true -o $out
if ($LASTEXITCODE) { throw 'Publish failed' }

Get-ChildItem -LiteralPath $out -Recurse -File -Filter '*.pdb' | Remove-Item -Force
& (Join-Path $PSScriptRoot 'verify-publish.ps1') -Directory $out

# ---- portable extras
Copy-Item (Join-Path $root 'packaging\portable\*') $out -Force

if ($SmokeTest) {
  $smokeDir = Join-Path $artifacts ('smoke-' + [Guid]::NewGuid().ToString('N'))
  $process = Start-Process -FilePath (Join-Path $out 'FloatingDock.exe') -ArgumentList ('--smoke-test "' + $smokeDir + '"') -WindowStyle Hidden -PassThru
  if (-not $process.WaitForExit(60000)) { throw "Runtime test timed out. Diagnostic directory: $smokeDir" }
  if ($process.ExitCode -ne 0) { throw "Runtime test failed ($($process.ExitCode)). Diagnostic directory: $smokeDir" }
  $result = Get-Content -LiteralPath (Join-Path $smokeDir 'smoke-result.json') -Raw | ConvertFrom-Json
  if (-not $result.success) { throw 'Runtime test did not report success.' }
  Write-Host "Runtime test passed: $smokeDir"
}

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
Write-Host "Published FloatingDock $version to $out"

if ($Zip) {
  $portableZipPath = Join-Path $artifacts 'FloatingDock-Portable-x64.zip'
  if (Test-Path -LiteralPath $portableZipPath) { Remove-Item -LiteralPath $portableZipPath }
  Compress-Archive -Path "$out\*" -DestinationPath $portableZipPath
  Write-Host "Created $portableZipPath"
}
if ($Installer) {
  $iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
  if (-not $iscc) { $iscc = Get-Item "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" -ErrorAction SilentlyContinue }
  if (-not $iscc) { throw 'ISCC.exe (Inno Setup 6) not found' }
  $isccPath = if ($iscc -is [System.Management.Automation.ApplicationInfo]) { $iscc.Source } else { $iscc.FullName }
  & $isccPath "/DAppVersion=$version" "/DSourceDir=$out" "/O$artifacts" (Join-Path $root 'packaging\installer\FloatingDock.iss')
  if ($LASTEXITCODE) { throw 'Installer build failed' }
  Write-Host "Created $(Join-Path $artifacts 'FloatingDock-Setup-x64.exe')"
}

$releaseFiles = @()
if ($Zip) { $releaseFiles += Join-Path $artifacts 'FloatingDock-Portable-x64.zip' }
if ($Installer) { $releaseFiles += Join-Path $artifacts 'FloatingDock-Setup-x64.exe' }
$releaseFiles | ForEach-Object {
  if (-not (Test-Path -LiteralPath $_ -PathType Leaf)) { throw "Missing release file: $_" }
  "{0}  {1}" -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLower(), [IO.Path]::GetFileName($_)
} | Set-Content (Join-Path $artifacts 'SHA256SUMS.txt')
