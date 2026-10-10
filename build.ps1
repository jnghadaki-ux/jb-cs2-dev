# Builds SwbsJailbreak for both supported CounterStrikeSharp lines (Windows PowerShell / pwsh):
#   artifacts/SwbsJailbreak-<version>-net10.zip -> CounterStrikeSharp 1.0.370 .. 1.0.376 (.NET 10)  [recommended]
#   artifacts/SwbsJailbreak-<version>-net8.zip  -> CounterStrikeSharp <= 1.0.368 (.NET 8)
# Requires the .NET 10 SDK.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$version = (Select-String -Path src/SwbsJailbreak/JailbreakPlugin.cs -Pattern 'PluginVersion = "([^"]+)"').Matches[0].Groups[1].Value
if (Test-Path artifacts) { Remove-Item artifacts -Recurse -Force }
dotnet build src/SwbsJailbreak/SwbsJailbreak.csproj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
foreach ($tfm in @("net10.0", "net8.0")) {
  $tag = $tfm.Split('.')[0]
  $out = "artifacts/$tag"
  $pluginDir = "$out/addons/counterstrikesharp/plugins/SwbsJailbreak"
  $bin = "src/SwbsJailbreak/bin/$Configuration/$tfm"
  New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
  Copy-Item "$bin/SwbsJailbreak.dll" $pluginDir
  if (Test-Path "$bin/SwbsJailbreak.pdb") { Copy-Item "$bin/SwbsJailbreak.pdb" $pluginDir }
  if (Test-Path "$bin/SwbsJailbreak.deps.json") { Copy-Item "$bin/SwbsJailbreak.deps.json" $pluginDir }
  Copy-Item "$bin/lang" $pluginDir -Recurse
  if (Test-Path package) { Copy-Item "package/*" $out -Recurse -Force }
  Compress-Archive -Path "$out/addons" -DestinationPath "artifacts/SwbsJailbreak-$version-$tag.zip" -Force
  Write-Host "Package: artifacts/SwbsJailbreak-$version-$tag.zip"
}
