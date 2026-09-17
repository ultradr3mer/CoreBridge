param(
    [string]$GroupName = "CoreBridge",
    [string]$VmPattern = "cbx-*",
    [switch]$Remove
)

$ErrorActionPreference = "Stop"

if ($Remove) {
    Remove-VMGroup -Name $GroupName -ErrorAction SilentlyContinue
    Write-Host "VM-Gruppe '$GroupName' entfernt."
    return
}

$group = Get-VMGroup -Name $GroupName -ErrorAction SilentlyContinue
if (-not $group) {
    $group = New-VMGroup -Name $GroupName -GroupType VMCollectionType
    Write-Host "VM-Gruppe '$GroupName' angelegt."
}

$members = @($group.VMMembers | ForEach-Object { $_.Name })
$vms = @(Get-VM | Where-Object { $_.Name -like $VmPattern })
$new = @($vms | Where-Object { $members -notcontains $_.Name })

if ($new.Count -gt 0) {
    Add-VMGroupMember -Name $GroupName -VM $new
    Write-Host "Hinzugefuegt: $($new.Name -join ', ')"
}
else {
    Write-Host "Keine neuen VMs passend zu '$VmPattern'."
}

Write-Host "Gruppe '$GroupName' enthaelt:"
Get-VMGroup -Name $GroupName | Select-Object -ExpandProperty VMMembers | ForEach-Object { $_.Name }
