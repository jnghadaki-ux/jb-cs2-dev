# Builds SwbsJailbreak (.NET 8 / CounterStrikeSharp 1.0.368) and produces the drop-in server package (Windows PowerShell / pwsh):
#   artifacts/SwbsJailbreak-<version>-net8.zip -> contains addons/counterstrikesharp/plugins/SwbsJailbreak/...
# Unzip it into game/csgo/. Requires the .NET 8 SDK (a newer SDK also works; the target stays net8.0).
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$version = (Select-String -Path src/SwbsJailbreak/JailbreakPlugin.cs -Pattern 'PluginVersion = "([^"]+)"').Matches[0].Groups[1].Value
if (Test-Path artifacts) { Remove-Item artifacts -Recurse -Force }
dotnet build src/SwbsJailbreak/SwbsJailbreak.csproj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
$out = "artifacts/net8"
$pluginDir = "$out/addons/counterstrikesharp/plugins/SwbsJailbreak"
$bin = "src/SwbsJailbreak/bin/$Configuration/net8.0"
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item "$bin/SwbsJailbreak.dll" $pluginDir
if (Test-Path "$bin/SwbsJailbreak.pdb") { Copy-Item "$bin/SwbsJailbreak.pdb" $pluginDir }
if (Test-Path "$bin/SwbsJailbreak.deps.json") { Copy-Item "$bin/SwbsJailbreak.deps.json" $pluginDir }
Copy-Item "$bin/lang" $pluginDir -Recurse
if (Test-Path package) { Copy-Item "package/*" $out -Recurse -Force }
Compress-Archive -Path "$out/addons" -DestinationPath "artifacts/SwbsJailbreak-$version-net8.zip" -Force
Write-Host "Package: artifacts/SwbsJailbreak-$version-net8.zip"
