$ErrorActionPreference = 'Stop'
$root  = Split-Path -Parent $PSScriptRoot
$build = Join-Path $root 'build'
$dist  = Join-Path $root 'dist'

'== stop running instances =='
Get-Process SAM.Modern -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

'== clean dist scratch =='
foreach ($junk in @('WebView2Data','relay','debug.log','sam-modern.log')) {
  $p = Join-Path $dist $junk
  if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force; 'removed ' + $junk }
}
# only temporary screenshots; never glob-delete the folder
foreach ($stray in @(Get-ChildItem -LiteralPath $dist -File | Where-Object { $_.Extension -eq '.png' })) {
  Remove-Item -LiteralPath $stray.FullName -Force
  'removed ' + $stray.Name
}

'== copy build -> dist =='
$payload = @('SAM.Modern.exe','SAM.API.dll','sam.ico','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll','sam-modern-codesign.cer','trust-cert.ps1','unblock.ps1')
foreach ($n in $payload) {
  $src = Join-Path $build $n
  if (Test-Path -LiteralPath $src) {
    Copy-Item -LiteralPath $src -Destination (Join-Path $dist $n) -Force
    'copied ' + $n
  } else { 'MISSING in build: ' + $n }
}
# markdown docs: take them from the repo root (source of truth) and refresh the build/ copies.
# build/*.md used to be hand-copied and silently went stale — the shipped README kept describing an old version.
# 按通配取根目录的 .md，不要在这里写中文文件名：Windows PowerShell 5.1 按 ANSI 读脚本，
# 源码里的中文会变成乱码，文件名匹配不上（曾因此把「使用说明.md」漏拷）。
foreach ($md in @(Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Extension -eq '.md' })) {
  Copy-Item -LiteralPath $md.FullName -Destination (Join-Path $dist $md.Name) -Force
  Copy-Item -LiteralPath $md.FullName -Destination (Join-Path $build $md.Name) -Force
  'copied ' + $md.Name
}
$distUi = Join-Path $dist 'ui'
New-Item -ItemType Directory -Force -Path $distUi | Out-Null
foreach ($n in @('index.html','app.css','app.js')) {
  Copy-Item -LiteralPath (Join-Path (Join-Path $build 'ui') $n) -Destination (Join-Path $distUi $n) -Force
  'copied ui\' + $n
}

# ship the games.xml catalog cache so the first launch does not wait on the network.
# 只发目录缓存（games.xml）。游戏库列表 owned.txt 和 game-<appId>.json 都是本机账号的数据，
# 之前 dist 里残留过一批，会跟着 zip 一起发出去 —— 先清干净再拷。
$cacheDir = Join-Path $dist 'cache'
New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
foreach ($stale in @(Get-ChildItem -LiteralPath $cacheDir -File | Where-Object { $_.Name -ne 'games.xml' })) {
  Remove-Item -LiteralPath $stale.FullName -Force
  'removed cache\' + $stale.Name
}
$buildCache = Join-Path $build 'cache\games.xml'
if (Test-Path -LiteralPath $buildCache) {
  Copy-Item -LiteralPath $buildCache -Destination (Join-Path $cacheDir 'games.xml') -Force
  'copied cache\games.xml'
} else { 'MISSING in build: cache\games.xml' }

'== sign dist exe =='
$exe = Join-Path $dist 'SAM.Modern.exe'
$sig = Get-AuthenticodeSignature -LiteralPath $exe
'dist signature: ' + $sig.Status
if ($sig.Status -ne 'Valid') {
  & (Join-Path (Join-Path $root 'tools') 'sign-one.ps1') $exe
}

'== repack zip =='
$zip = Join-Path $root 'SAM-Modern-zhCN.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zip -CompressionLevel Optimal
'zip bytes: ' + (Get-Item -LiteralPath $zip).Length

'== final verify =='
foreach ($p in @($exe, (Join-Path $build 'SAM.Modern.exe'))) {
  $s = Get-AuthenticodeSignature -LiteralPath $p
  $who = if ($s.SignerCertificate) { $s.SignerCertificate.Subject } else { '<none>' }
  $label = Split-Path (Split-Path $p -Parent) -Leaf
  $label + '\SAM.Modern.exe = ' + $s.Status + ' / ' + $who
}