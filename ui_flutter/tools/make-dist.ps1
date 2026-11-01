# Assembles the new FloatingDock folder from the three builds:
#   <Out>\FloatingDock.Engine.exe + runtime   the program (engine)
#   <Out>\ui\fd_ui.exe + Flutter files        the new interface
#   <Out>\classic\FloatingDock.exe + files    the WinUI application (fallback, see Settings > About > Interface)
# Builds nothing itself: run it after
#   dotnet build src\FloatingDock.Engine\FloatingDock.Engine.csproj -c Release -o <Engine>
#   flutter build windows --release            (in ui_flutter\app)
#   dotnet build src\FloatingDock.App\FloatingDock.App.csproj -c Release -p:Platform=x64 -o <Classic>
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\..\..\.validation\dist1'),
    [string]$Engine = (Join-Path $PSScriptRoot '..\..\..\.validation\engine1'),
    [string]$Classic = (Join-Path $PSScriptRoot '..\..\..\.validation\classic-build'),
    [switch]$SkipClassic
)
$ErrorActionPreference = 'Stop'
$ui = Join-Path $PSScriptRoot '..\app\build\windows\x64\runner\Release'
foreach ($p in $Engine, $ui) { if (-not (Test-Path $p)) { throw "missing build: $p" } }
$Out = [IO.Path]::GetFullPath($Out)
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory $Out | Out-Null
function Copy-Tree($from, $to) { robocopy $from $to /E /NFL /NDL /NJH /NJS /NP | Out-Null; if ($LASTEXITCODE -ge 8) { throw "robocopy failed: $from" } }
Copy-Tree (Resolve-Path $Engine).Path $Out
Copy-Tree (Resolve-Path $ui).Path (Join-Path $Out 'ui')
if (-not $SkipClassic -and (Test-Path $Classic)) { Copy-Tree (Resolve-Path $Classic).Path (Join-Path $Out 'classic') }
$size = (Get-ChildItem $Out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
"dist ready: $Out ({0:N0} MB)" -f $size
Get-ChildItem $Out -Directory | % { '  {0}  {1:N0} MB' -f $_.Name, ((Get-ChildItem $_.FullName -Recurse -File | Measure-Object Length -Sum).Sum / 1MB) }
