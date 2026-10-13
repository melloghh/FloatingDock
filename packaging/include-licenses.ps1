param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$nugetRoot = $env:NUGET_PACKAGES
if (-not $nugetRoot) {
    $locals = & dotnet nuget locals global-packages --list
    if ($LASTEXITCODE -ne 0) { throw 'Cannot locate restored NuGet packages.' }
    $nugetRoot = ($locals -replace '^global-packages:\s*', '').Trim()
}
$runtime = Get-Content (Join-Path $Directory 'FloatingDock.runtimeconfig.json') -Raw | ConvertFrom-Json
$runtimeVersion = ($runtime.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
[xml]$project = Get-Content (Join-Path $root 'src/FloatingDock.App/FloatingDock.App.csproj')
$sdkVersion = ($project.Project.ItemGroup.PackageReference | Where-Object Include -eq 'Microsoft.WindowsAppSDK').Version
$licenses = Join-Path $Directory 'Licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $Directory 'LICENSE.txt') -Force
foreach ($entry in @(
    @("microsoft.netcore.app.runtime.win-x64/$runtimeVersion/LICENSE.TXT", 'DotNet-LICENSE.txt'),
    @("microsoft.netcore.app.runtime.win-x64/$runtimeVersion/THIRD-PARTY-NOTICES.TXT", 'DotNet-THIRD-PARTY-NOTICES.txt'),
    @("microsoft.windowsappsdk/$sdkVersion/license.txt", 'WindowsAppSDK-LICENSE.txt'),
    @("microsoft.windowsappsdk/$sdkVersion/NOTICE.txt", 'WindowsAppSDK-NOTICE.txt')
)) {
    $source = Join-Path $nugetRoot $entry[0]
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing dependency license: $($entry[0])" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $licenses $entry[1]) -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'portable/THIRD-PARTY-NOTICES.txt') -Destination $Directory -Force
