# unsign.ps1 - remove the local self-signed code-signing certificate and strip signatures
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$subject = 'CN=SAM Modern (Local Build), O=ojwvs, C=CN'

foreach ($store in @('My','Root','TrustedPublisher')) {
  $path = "Cert:\CurrentUser\$store"
  Get-ChildItem $path -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $subject } |
    ForEach-Object {
      Remove-Item -Path $_.PSPath -Force
      "removed from $store"
    }
}

foreach ($exe in @("$root\dist\SAM.Modern.exe", "$root\build\SAM.Modern.exe")) {
  if (Test-Path -LiteralPath $exe) {
    Set-AuthenticodeSignature -FilePath $exe -Certificate $null | Out-Null
    '{0} -> {1}' -f (Split-Path $exe -Leaf), (Get-AuthenticodeSignature $exe).Status
  }
}
'done'
