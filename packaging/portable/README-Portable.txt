FloatingDock - portable build (Windows 10 1809+ / Windows 11, 64-bit)

1. Unzip the whole folder anywhere (not into a folder that needs administrator rights).
2. Run FloatingDock.exe. Nothing has to be installed: the .NET runtime and the Windows App SDK runtime are inside this folder.

Settings, pinned apps and icon packs are stored in %LOCALAPPDATA%\FloatingDock (not in this folder), so you can delete or replace
this folder to update or remove the program without losing your settings.

If FloatingDock ever leaves the Windows taskbar hidden (crash, killed process), run Restore-Windows-Taskbar.cmd,
or press Ctrl+Alt+Shift+T while FloatingDock is running. The next start of FloatingDock also restores it automatically.
