# Builds SwbsJailbreak (.NET 10 / CounterStrikeSharp 1.0.376) and produces the drop-in server package (Windows PowerShell / pwsh):
#   artifacts/SwbsJailbreak-<version>-net10.zip -> contains addons/counterstrikesharp/plugins/SwbsJailbreak/...
# Unzip it into game/csgo/. Requires the .NET 10 SDK.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$version = (Select-String -Path src/SwbsJailbreak/JailbreakPlugin.cs -Pattern 'PluginVersion = "([^"]+)"').Matches[0].Groups[1].Value
if (Test-Path artifacts) { Remove-Item artifacts -Recurse -Force }
dotnet build src/SwbsJailbreak/SwbsJailbreak.csproj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
$out = "artifacts/net10"
$pluginDir = "$out/addons/counterstrikesharp/plugins/SwbsJailbreak"
$bin = "src/SwbsJailbreak/bin/$Configuration/net10.0"
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item "$bin/SwbsJailbreak.dll" $pluginDir
if (Test-Path "$bin/SwbsJailbreak.pdb") { Copy-Item "$bin/SwbsJailbreak.pdb" $pluginDir }
if (Test-Path "$bin/SwbsJailbreak.deps.json") { Copy-Item "$bin/SwbsJailbreak.deps.json" $pluginDir }
Copy-Item "$bin/MySqlConnector.dll" $pluginDir # shared points / store items on MySQL (configs/swbs_shared.json)
Copy-Item "$bin/lang" $pluginDir -Recurse
if (Test-Path package) { Copy-Item "package/*" $out -Recurse -Force }
Compress-Archive -Path "$out/addons" -DestinationPath "artifacts/SwbsJailbreak-$version-net10.zip" -Force
Write-Host "Package: artifacts/SwbsJailbreak-$version-net10.zip"
