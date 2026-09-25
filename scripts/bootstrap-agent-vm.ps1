<#
.SYNOPSIS
    Veralteter Kompatibilitätsstub für die frühere Core-Bootstrap-Logik.

.DESCRIPTION
    Classic Ubuntu wird ausschließlich über den öffentlichen CLI-first-Workflow
    provisioniert. Der Host führt keine Komponenten-Installer und kein
    `dotnet run` aus. Diese Datei bleibt nur als klare Meldung für alte
    Aufrufer erhalten; sie verändert keine VM.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$VmName,

    [ValidateSet("claude", "dotnet", "opencode", "python")]
    [string[]]$Components = @("claude", "dotnet"),

    [ValidateSet("snap", "binary")]
    [string]$ClaudeInstallMode = "binary"
)

$componentsText = $Components -join ','
Write-Host "bootstrap-agent-vm.ps1 ist im Classic-Workflow veraltet."
Write-Host "Bitte verwende:"
Write-Host "  cb image-configure --name <profil> --components $componentsText"
Write-Host "  cb vm-create --name $VmName --image <profil>"
Write-Host "Die Gast-Provisionierung erfolgt anschließend über cloud-init."
