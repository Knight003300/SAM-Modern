param([Parameter(Mandatory=$true)][string]$Exe)
$ErrorActionPreference = 'Stop'
$subject = 'CN=SAM Modern (Local Build), O=ojwvs, C=CN'
if (-not (Test-Path -LiteralPath $Exe)) { 'sign skip: missing ' + $Exe; exit 0 }
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey } | Select-Object -First 1
if (-not $cert) { 'sign skip: no local code-signing cert (run tools/sign.ps1 once)'; exit 0 }
$sig = Set-AuthenticodeSignature -FilePath $Exe -Certificate $cert -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com' -ErrorAction SilentlyContinue
if (-not $sig -or $sig.Status -eq 'UnknownError') {
  $sig = Set-AuthenticodeSignature -FilePath $Exe -Certificate $cert -HashAlgorithm SHA256
}
$check = Get-AuthenticodeSignature -LiteralPath $Exe
'signed ' + (Split-Path $Exe -Leaf) + ' -> ' + $check.Status