# Builds SwbsJailbreak and produces a drop-in server package (Windows PowerShell / pwsh).
# Requires the .NET 10 SDK (CounterStrikeSharp 1.0.376 targets net10.0).
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$out = "artifacts/SwbsJailbreak"
$pluginDir = "$out/addons/counterstrikesharp/plugins/SwbsJailbreak"
if (Test-Path artifacts) { Remove-Item artifacts -Recurse -Force }
dotnet build src/SwbsJailbreak/SwbsJailbreak.csproj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
$bin = "src/SwbsJailbreak/bin/$Configuration/net10.0"
Copy-Item "$bin/SwbsJailbreak.dll" $pluginDir
if (Test-Path "$bin/SwbsJailbreak.pdb") { Copy-Item "$bin/SwbsJailbreak.pdb" $pluginDir }
if (Test-Path "$bin/SwbsJailbreak.deps.json") { Copy-Item "$bin/SwbsJailbreak.deps.json" $pluginDir }
Copy-Item "$bin/lang" $pluginDir -Recurse
if (Test-Path package) { Copy-Item "package/*" $out -Recurse -Force }
$version = (Select-String -Path src/SwbsJailbreak/JailbreakPlugin.cs -Pattern 'PluginVersion = "([^"]+)"').Matches[0].Groups[1].Value
Compress-Archive -Path $out -DestinationPath "artifacts/SwbsJailbreak-$version.zip" -Force
Write-Host "Package: artifacts/SwbsJailbreak-$version.zip"
