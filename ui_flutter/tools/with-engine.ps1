# Starts the engine on a throw-away diagnostic profile (no tray icon, no hotkeys, taskbar untouched), runs each given
# Dart script from fd_ipc/tool with the socket path and profile as arguments, stops the engine, prints its log.
#   with-engine.ps1 ipc_check2.dart dump_fixtures.dart
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Scripts)
$EngineExe = if ($env:FD_ENGINE_EXE) { $env:FD_ENGINE_EXE } else { Join-Path $PSScriptRoot '..\..\..\.validation\engine1\FloatingDock.Engine.exe' }
$Profile = 'C:\dev\fd-check'
$ErrorActionPreference = 'Stop'
$dart = 'C:\dev\flutter\bin\cache\dart-sdk\bin\dart.exe'
$tool = Join-Path $PSScriptRoot '..\fd_ipc'
$EngineExe = (Resolve-Path $EngineExe).Path
Get-Process FloatingDock.Engine -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $EngineExe } | Stop-Process -Force
if (Test-Path $Profile) { Remove-Item $Profile -Recurse -Force }
New-Item -ItemType Directory $Profile | Out-Null
$sockFile = Join-Path $Profile 'engine.endpoint'   # the engine writes tcp:<port>:<token> here once it listens
$sock = ''

$engine = Start-Process $EngineExe -ArgumentList '--diagnostic-profile', $Profile -PassThru
for ($i = 0; $i -lt 80 -and -not (Test-Path $sockFile); $i++) { Start-Sleep -Milliseconds 100 }
if (-not (Test-Path $sockFile)) { Stop-Process -Id $engine.Id -Force; throw "engine did not publish $sockFile; see $Profile\Logs\engine.log" }
$sock = (Get-Content $sockFile -Raw).Trim()
$code = 0
Push-Location $tool
try {
    & $dart pub get | Out-Null
    foreach ($s in $Scripts) {
        "=== $s"
        & $dart run "tool/$s" $sock $Profile
        if ($LASTEXITCODE -ne 0) { $code = 1 }
    }
} finally { Pop-Location }
$exited = $engine.WaitForExit(8000)
if (-not $exited) { Stop-Process -Id $engine.Id -Force; $code = 1; 'engine did not exit after quit' }
"--- engine.log"
Get-Content (Join-Path $Profile 'Logs\engine.log')
exit $code
