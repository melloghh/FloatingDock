# Stage 1 verification, one run: builds nothing. Starts the engine on a throw-away profile, runs the Dart link check
# against it, then confirms the engine process is gone. Never touches %LOCALAPPDATA%\FloatingDock.
param(
    [string]$EngineExe = (Join-Path $PSScriptRoot '..\..\..\.validation\engine1\FloatingDock.Engine.exe'),
    [string]$Profile = 'C:\dev\fd-check1'
)
$ErrorActionPreference = 'Stop'
$dart = 'C:\dev\flutter\bin\cache\dart-sdk\bin\dart.exe'
$check = Join-Path $PSScriptRoot '..\fd_ipc'
$EngineExe = (Resolve-Path $EngineExe).Path
if (Test-Path $Profile) { Remove-Item $Profile -Recurse -Force }
New-Item -ItemType Directory $Profile | Out-Null
$sockFile = Join-Path $Profile 'engine.endpoint'   # the engine writes tcp:<port>:<token> here once it listens
$sock = ''

$engine = Start-Process $EngineExe -ArgumentList '--diagnostic-profile', $Profile -PassThru
for ($i = 0; $i -lt 50 -and -not (Test-Path $sockFile); $i++) { Start-Sleep -Milliseconds 100 }
if (-not (Test-Path $sockFile)) { Stop-Process -Id $engine.Id -Force; throw "engine did not publish $sockFile; see $Profile\Logs\engine.log" }
$sock = (Get-Content $sockFile -Raw).Trim()
"engine pid $($engine.Id) up in profile $Profile"

# Idle cost of the engine itself (no client connected): 6 s after start, over 6 s.
Start-Sleep -Seconds 6
$engine.Refresh(); $c0 = $engine.TotalProcessorTime; $t0 = Get-Date
Start-Sleep -Seconds 6
$engine.Refresh(); $cpu = ($engine.TotalProcessorTime - $c0).TotalSeconds / ((Get-Date) - $t0).TotalSeconds / [Environment]::ProcessorCount * 100
"engine idle: {0:N3}% CPU of machine, working set {1:N1} MB, private {2:N1} MB, {3} threads" -f $cpu, ($engine.WorkingSet64 / 1MB), ($engine.PrivateMemorySize64 / 1MB), $engine.Threads.Count

Push-Location $check
try {
    & $dart pub get | Out-Null
    & $dart run tool/ipc_check.dart $sock $Profile
    $code = $LASTEXITCODE
} finally { Pop-Location }

$exited = $engine.WaitForExit(8000)
"engine exited after quit: $exited"
if (-not $exited) { Stop-Process -Id $engine.Id -Force }
"--- engine.log"
Get-Content (Join-Path $Profile 'Logs\engine.log')
if ($code -ne 0 -or -not $exited) { exit 1 }
