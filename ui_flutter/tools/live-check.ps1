# The one live pass: starts the real engine + Flutter UI from the dist folder on a COPY of the user's profile (a diagnostic profile:
# no tray icon, no hotkeys, the Windows taskbar and autostart untouched), takes screenshots of each part, measures the idle cost,
# and stops everything again. Screenshots go to $Shots.
param(
    [string]$Dist = (Join-Path $PSScriptRoot '..\..\..\.validation\dist1'),
    [string]$Profile = 'C:\dev\fd-live',
    [Parameter(Mandatory)][string]$Shots,
    [switch]$Quick   # rest, the launchpad (after the Start pins loaded) and the idle cost only
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text; using System.Collections.Generic;
public static class L {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern bool EnumWindows(P cb, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
  public delegate bool P(IntPtr h, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
  public static List<string> Windows(uint pid) {
    var res = new List<string>();
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { R r; GetWindowRect(h, out r); var sb = new StringBuilder(80); GetWindowText(h, sb, 80);
      int ex = GetWindowLong(h, -20); res.Add(string.Format("{0,-26} {1,5},{2,5} {3,5}x{4,-5} topmost={5} noactivate={6}", sb, r.L, r.T, r.Rt - r.L, r.B - r.T, (ex & 8) != 0, (ex & 0x8000000) != 0)); } return true; }, IntPtr.Zero);
    return res; }
}
"@
[L]::SetProcessDPIAware() | Out-Null
$dart = 'C:\dev\flutter\bin\cache\dart-sdk\bin\dart.exe'
$tool = Join-Path $PSScriptRoot '..\fd_ipc'
$engineExe = Join-Path (Resolve-Path $Dist).Path 'FloatingDock.exe'
New-Item -ItemType Directory -Force $Shots | Out-Null
Get-Process FloatingDock, fd_ui -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path $Profile) { Remove-Item $Profile -Recurse -Force }
robocopy "$env:LOCALAPPDATA\FloatingDock" $Profile /E /XD Logs Cache /XF engine.endpoint engine.sock* /NFL /NDL /NJH /NJS /NP | Out-Null
$sockFile = Join-Path $Profile 'engine.endpoint'   # the engine writes tcp:<port>:<token> here once it listens
$sock = ''
Add-Type -AssemblyName System.Windows.Forms
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
"screen $($screen.Width)x$($screen.Height)"

function Shot($name) {
    $bmp = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($screen.Left, $screen.Top, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $Shots "$name.png")); $g.Dispose(); $bmp.Dispose()
}
function Send($command, $arg) { Push-Location $tool; if ($arg) { & $dart run tool/send_command.dart $sock $command $arg | Out-Null } else { & $dart run tool/send_command.dart $sock $command | Out-Null }; Pop-Location }

$engine = Start-Process $engineExe -ArgumentList '--diagnostic-profile', $Profile -PassThru
for ($i = 0; $i -lt 100 -and -not (Test-Path $sockFile); $i++) { Start-Sleep -Milliseconds 100 }
if (-not (Test-Path $sockFile)) { throw 'engine did not start' }
$sock = (Get-Content $sockFile -Raw).Trim()
Start-Sleep -Seconds 9
$ui = Get-Process fd_ui -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $ui) { Get-Content (Join-Path $Profile 'Logs\engine.log'); throw 'the UI did not start' }
"UI pid $($ui.Id); windows:"
[L]::Windows([uint32]$ui.Id)
Shot '01-rest'

if (-not $Quick) {
# --- dock hover: magnification, label, preview
$dockY = $screen.Height - 80
[L]::SetCursorPos([int]($screen.Width / 2), $dockY) | Out-Null; Start-Sleep -Milliseconds 300
[L]::SetCursorPos([int]($screen.Width / 2) + 4, $dockY) | Out-Null; Start-Sleep -Milliseconds 1200
Shot '02-dock-hover'
Start-Sleep -Milliseconds 1500
Shot '03-dock-preview'
[L]::SetCursorPos(10, 300) | Out-Null; Start-Sleep -Milliseconds 800

# --- palettes and menus
foreach ($step in @(@('toggleSearch', $null, '04-search'), @('toggleLaunchpad', $null, '05-launchpad'))) {
    Send $step[0] $step[1]; Start-Sleep -Milliseconds 1300; Shot $step[2]; Send $step[0] $step[1]; Start-Sleep -Milliseconds 700
}
foreach ($kind in 'main', 'media', 'sound', 'network', 'calendar') {
    Send 'menu' $kind; Start-Sleep -Milliseconds 1300; Shot "06-menu-$kind"; Send 'menu' $kind; Start-Sleep -Milliseconds 600
}
Send 'settings' 'Appearance'; Start-Sleep -Milliseconds 2200; Shot '07-settings'
Send 'settings'; Start-Sleep -Milliseconds 300
} else {
    Send 'toggleLaunchpad' $null; Start-Sleep -Seconds 6; Shot '05-launchpad'; Send 'toggleLaunchpad' $null; Start-Sleep -Milliseconds 800
    Send 'menu' 'media'; Start-Sleep -Milliseconds 1300; Shot '06-menu-media'; Send 'menu' 'media'; Start-Sleep -Milliseconds 800
}

# --- idle cost with nothing animating
[L]::SetCursorPos(10, 300) | Out-Null
Start-Sleep -Seconds 4
$u = Get-Process fd_ui; $e = Get-Process FloatingDock
$c0u = $u.TotalProcessorTime; $c0e = $e.TotalProcessorTime; $t0 = Get-Date
Start-Sleep -Seconds 10
$u.Refresh(); $e.Refresh(); $dt = ((Get-Date) - $t0).TotalSeconds
"idle UI:     {0:N2}% CPU, working set {1:N0} MB, private {2:N0} MB" -f ((($u.TotalProcessorTime - $c0u).TotalSeconds / $dt) / [Environment]::ProcessorCount * 100), ($u.WorkingSet64 / 1MB), ($u.PrivateMemorySize64 / 1MB)
"idle engine: {0:N2}% CPU, working set {1:N0} MB" -f ((($e.TotalProcessorTime - $c0e).TotalSeconds / $dt) / [Environment]::ProcessorCount * 100), ($e.WorkingSet64 / 1MB)

# --- stop
& $engineExe --diagnostic-profile $Profile --quit
Start-Sleep -Seconds 3
"after --quit: engine running=$([bool](Get-Process FloatingDock -ErrorAction SilentlyContinue)), ui running=$([bool](Get-Process fd_ui -ErrorAction SilentlyContinue)), endpoint file exists=$(Test-Path $sockFile)"
Get-Process FloatingDock, fd_ui -ErrorAction SilentlyContinue | Stop-Process -Force
"--- engine.log"
Get-Content (Join-Path $Profile 'Logs\engine.log')
