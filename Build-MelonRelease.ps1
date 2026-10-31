# Package the MelonLoader build of MenaceCoop for release.
#
# A MelonLoader mod ships as JUST ITS DLL. Players already have MelonLoader --
# the MENACE Modkit installs it, and the mod manager pins the version -- and
# bundling a loader would fight theirs. So this zip is two files, against the
# BepInEx release's 230: that is the point, not an omission.
#
# NEXUS RULES, verified on the finished zip rather than assumed:
#   - no archives inside the archive (a co-op release was quarantined for one:
#     copying BepInEx wholesale brought along unity-libs\6000.0.72.zip)
#   - no scripts of any kind (.ps1 included -- this very script must never ship)
#   - no private paths (Assert-NoPrivatePaths, user requirement 2026-09-14)
$ErrorActionPreference = "Stop"
$root  = $PSScriptRoot   # the repo root
$proj  = "$root\MenaceCoopMelon\MenaceCoopMelon.csproj"
$dll   = "$root\MenaceCoopMelon\bin\Release\MenaceCoop.dll"
$stage = "$root\stage-melon"

$m = Select-String -Path "$root\MenaceCoopMelon\MelonEntry.cs" -Pattern 'PluginVersion\s*=\s*"([^"]+)"'
if (-not $m) { throw "Could not read PluginVersion from MelonEntry.cs" }
$ver = $m.Matches[0].Groups[1].Value

if (Get-Process Menace -ErrorAction SilentlyContinue) {
    throw "MENACE is running -- the DLL is locked and the build would silently ship the OLD one."
}

. "$PSScriptRoot\tools\RigPaths.ps1"   # Get-Dotnet: a dotnet that has an SDK
$dotnet = Get-Dotnet
& $dotnet build $proj -c Release --no-incremental -p:DevTools=false
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
if (-not (Test-Path $dll)) { throw "Build reported success but $dll is missing" }

# DEV TOOLS MUST NOT SHIP (user, 2026-10-03). They are compiled out above; prove
# it on the finished DLL. Every dev-tool string carries the marker "[DEVTOOLS]".
# C# literals are UTF-16 in the DLL, so check both byte alignments.
$bytes = [IO.File]::ReadAllBytes($dll)
$marker = '[DEVTOOLS]'
$found = $false
foreach ($off in 0, 1) {
    $text = [Text.Encoding]::Unicode.GetString($bytes, $off, $bytes.Length - $off - (($bytes.Length - $off) % 2))
    if ($text.Contains($marker)) { $found = $true }
}
if ([Text.Encoding]::ASCII.GetString($bytes).Contains($marker)) { $found = $true }
if ($found) { throw "Release DLL still contains dev tools ('$marker') -- refusing to package" }
Write-Host "Dev tools: absent from the release DLL (checked)."

# DLL AT THE ZIP ROOT, not under Mods\.
#
# IntroSkip shipped Mods\MenaceIntroSkip.dll once. A player extracted it into
# Mods\ -- the obvious thing to do with a MelonLoader mod -- and it landed at
# Mods\<zip name>\Mods\, one level too deep, where MelonLoader never looked.
# Every MelonLoader mod for this game ships the DLL at the root. Do not "fix" it.
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item $dll "$stage\MenaceCoop.dll" -Force
Copy-Item "$root\MenaceCoop-README.md" "$stage\README.txt" -Force

$zip = "$root\MenaceCoop-MelonLoader-v$ver.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
Remove-Item $stage -Recurse -Force

$check = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $names   = @($check.Entries | ForEach-Object { $_.FullName })
    $nested  = @($names | Where-Object { $_ -match '\.(zip|7z|rar|gz|tar|cab)$' })
    $scripts = @($names | Where-Object { $_ -match '\.(ps1|psm1|psd1|bat|cmd|sh|vbs|js|py)$' })
    $loader  = @($names | Where-Object { $_ -match '(^|[\\/])(MelonLoader|BepInEx|dotnet)[\\/]|^(version|winhttp)\.dll$|doorstop' })
    $mod     = @($names | Where-Object { $_ -match '^MenaceCoop\.dll$' }).Count
} finally { $check.Dispose() }

if ($nested.Count  -gt 0) { throw "Nested archives present -- Nexus will quarantine: $($nested -join ', ')" }
if ($scripts.Count -gt 0) { throw "Scripts present -- Nexus will reject: $($scripts -join ', ')" }
if ($loader.Count  -gt 0) { throw "Loader files present -- a MelonLoader mod must not bundle a loader: $($loader -join ', ')" }
if ($mod -ne 1)           { throw "MenaceCoop.dll missing from the zip root" }
# The private-path scan lives in the maintainer's shared tools folder, outside
# this repo; elsewhere the Release PathMap (Directory.Build.props) still applies.
$assert = Join-Path $PSScriptRoot "..\_Tools\Assert-NoPrivatePaths.ps1"
if (Test-Path $assert) { & $assert -Zip $zip -OwnDlls MenaceCoop.dll }
else { Write-Host "Private-path scan skipped (_Tools\Assert-NoPrivatePaths.ps1 not present)." -ForegroundColor DarkYellow }

Write-Host ""
Write-Host "=== READY: MenaceCoop (MelonLoader) v$ver ===" -ForegroundColor Green
Write-Host "  Zip: $(Split-Path $zip -Leaf)  ($([math]::Round((Get-Item $zip).Length/1KB,1)) KB)"
Write-Host "  Contents: $($names -join ', ')"
Write-Host "  Nexus check: no nested archives, no scripts, no bundled loader" -ForegroundColor Green
Write-Host ""
Write-Host "  NOT verified by this script: that the mod actually works." -ForegroundColor Yellow
Write-Host "  Run Launch-MelonCoopTest.ps1 -AutoPlay first." -ForegroundColor Yellow
