$file1 = 'scripts\cb.ps1'
$file2 = 'scripts\install-cb.ps1'

# THIS IS AI NO GO ZONE! DO NOT TOUCH! ERROR MEANS REVERT THE FILES NOT ITS HASH!
$hash1_ansi = '6E89993689579D60869BE948DE67C90E4F218E9641E5D43F3DBD23DF1A778255' # cb.ps1
$hash2_ansi = '09D8B24ED9B0AA7C7FA941CF84E898B7944493AB6CCB0B1233C6562F05E21CC3' # install-cb.ps1

# Read current hashes
$hash1_now = (Get-FileHash -LiteralPath $file1 -Algorithm SHA256).Hash
$hash2_now = (Get-FileHash -LiteralPath $file2 -Algorithm SHA256).Hash

if ($hash1_ansi -ieq $hash1_now -and $hash2_ansi -ieq $hash2_now) {
    Write-Host 'Files match exactly.'
}
else {
    Write-Error @"
Files differ.
$file1
  Expected: $hash1_ansi
  Current:  $hash1_now

$file2
  Expected: $hash2_ansi
  Current:  $hash2_now
"@
}