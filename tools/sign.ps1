$ErrorActionPreference = 'Stop'
# repo root = parent of tools/, so the script works from any clone location
$root = Split-Path -Parent $PSScriptRoot
$subject = 'CN=SAM Modern (Local Build), O=ojwvs, C=CN'

'== existing certs =='
$old = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject }
if ($old) { 'found ' + $old.Count + ' existing' } else { 'none' }

if (-not $old) {
  $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation 'Cert:\CurrentUser\My' -KeyUsage DigitalSignature -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(5) -HashAlgorithm SHA256
  'created ' + $cert.Thumbprint
} else {
  $cert = $old | Select-Object -First 1
  'reuse ' + $cert.Thumbprint
}

'== trust stores =='
foreach ($store in @('Root','TrustedPublisher')) {
  $path = "Cert:\CurrentUser\$store"
  $existing = Get-ChildItem $path | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
  if (-not $existing) {
    $cer = Join-Path $env:TEMP ('sam-' + $store + '.cer')
    Export-Certificate -Cert $cert -FilePath $cer -Type CERT | Out-Null
    Import-Certificate -FilePath $cer -CertStoreLocation $path | Out-Null
    'imported into ' + $store
  } else { 'already in ' + $store }
}

'== export public cert =='
$pub = Join-Path $root 'tools\sam-modern-codesign.cer'
Export-Certificate -Cert $cert -FilePath $pub -Type CERT -Force | Out-Null
'saved ' + $pub

'== sign =='
foreach ($exe in @("$root\dist\SAM.Modern.exe", "$root\build\SAM.Modern.exe")) {
  $sig = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com' -ErrorAction SilentlyContinue
  if (-not $sig -or $sig.Status -eq 'UnknownError') {
    $sig = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256
  }
  $check = Get-AuthenticodeSignature $exe
  '{0,-16} {1}' -f (Split-Path $exe -Leaf), $check.Status
}

'== publisher =='
(Get-AuthenticodeSignature "$root\dist\SAM.Modern.exe").SignerCertificate.Subject
