$file1 = 'scripts\cb.ps1'
$file2 = 'scripts\install-cb.ps1'

# THIS IS AI NO GO ZONE! DO NOT TOUCH! ERROR MEANS REVERT THE FILES NOT ITS HASH!
$hash1_ansi = "841249de59043535ebf941c018f86233e8b7a226e13541d1bf8e5e0fdb9facad" #  cb.ps1
$hash2_ansi = "8238a115a62c481c12c27c4e74023ba50f58abb19e4dba2d1ea90766bf320b2d" #  install-cb.ps1

# Read current
$hash1_now = (Get-FileHash -LiteralPath $file1 -Algorithm SHA256).Hash
$hash2_now = (Get-FileHash -LiteralPath $file2 -Algorithm SHA256).Hash

if ($hash1_ansi -eq $hash1_now && $hash2_ansi -eq $hash2_now) {
    Write-Host 'Files match exactly.'
}
else {
    Write-Error "Files differ.`n$file1 : $hash1`n$file2 : $hash2"
}