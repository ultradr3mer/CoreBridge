<#
.SYNOPSIS
    Spielt den typischen CoreBridge-Ablauf Schritt für Schritt durch —
    standardmäßig gegen den Multipass-Simulator (cb-sim), mit -Real gegen echtes Multipass.

.DESCRIPTION
    Zum Ausprobieren der Bedienung: jeder Schritt zeigt den Befehl, führt ihn aus
    und meldet den Exit-Code. Rückfragen der CLI (weiter warten?, stoppen?, Backup?)
    beantwortest du selbst in der Konsole.

    Alle VMs heißen cbx-suite-*, das Profil cbx-suite. Schritt "cleanup" räumt beides weg.

.PARAMETER Real
    Echtes Multipass statt Simulator. Legt echte VMs an (Minuten statt Sekunden).

.PARAMETER Pause
    Nach jedem Schritt auf Enter warten.

.PARAMETER From
    Ab diesem Schritt (Name oder Nummer) beginnen, z. B. -From clone.

.PARAMETER Only
    Nur diese Schritte ausführen, z. B. -Only status,list.

.PARAMETER List
    Nur die Schritte auflisten.

.EXAMPLE
    .\scripts\cb-suite.ps1 -Pause
.EXAMPLE
    .\scripts\cb-suite.ps1 -From clone
.EXAMPLE
    .\scripts\cb-suite.ps1 -Real -Only create,check
#>
param(
    [switch]$Real,
    [switch]$Pause,
    [string]$From,
    [string[]]$Only,
    [switch]$List
)

$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
. (Join-Path $PSScriptRoot 'cb.ps1')

$profileName = 'cbx-suite'
$base = 'cbx-suite-basis'
$clone = 'cbx-suite-dev-01'
$slow = 'cbx-suite-langsam'
$hostFile = Join-Path $env:TEMP 'cbx-suite-hin.txt'
$backFile = Join-Path $env:TEMP 'cbx-suite-zurueck.txt'

# Echte VMs brauchen länger als die 10 s DefaultTimeout.
$t = if ($Real) { @('-t', '180s') } else { @() }

function run {
    $line = ($args | ForEach-Object {
        $text = if ($_ -is [array]) { $_ -join ',' } else { "$_" }
        if ($text -match '\s') { "`"$text`"" } else { $text }
    }) -join ' '
    $tool = if ($Real) { 'cb' } else { 'cb-sim' }
    Write-Host "`n> $tool $line" -ForegroundColor Cyan
    if ($Real) { cb @args } else { cb-sim @args }
    $color = if ($LASTEXITCODE -eq 0) { 'DarkGray' } else { 'Yellow' }
    Write-Host "  exit $LASTEXITCODE" -ForegroundColor $color
}

$steps = [ordered]@{
    'reset'    = @{ Text = 'Simulator-Zustand leeren (nur Simulation)'; Do = {
        if (-not $Real) { Remove-Item (Join-Path $repo 'artifacts\multipass-sim') -Recurse -ErrorAction SilentlyContinue; 'Simulator-Zustand geleert.' } } }
    'status'   = @{ Text = 'Multipass gefunden? Versionen'; Do = { run multipass-status } }
    'profile'  = @{ Text = 'Profil mit claude und dotnet anlegen'; Do = {
        run init-configure -n $profileName -c claude,dotnet -f
        run init-list } }
    'create'   = @{ Text = 'Basis-VM aus dem Profil anlegen (cloud-init)'; Do = { run vm-create -n $base -i $profileName } }
    'check'    = @{ Text = 'Provisionierung prüfen'; Do = {
        run vm-exec -n $base -c 'hostname; claude --version; dotnet --version' @t
        run vm-exec -n $base -c 'ls /var/lib/corebridge/' @t } }
    'files'    = @{ Text = 'Datei hin und zurück kopieren'; Do = {
        'hallo aus dem host' | Set-Content $hostFile
        run vm-put -n $base -s $hostFile -d suite.txt @t
        run vm-get -n $base -r suite.txt -d $backFile @t
        if (Test-Path $backFile) { "Zurück: $(Get-Content $backFile)" } } }
    'snapshot' = @{ Text = 'Stoppen und frischen Stand sichern'; Do = {
        run vm-stop -n $base @t
        run vm-snapshot -n $base -s frisch -m 'nach cloud-init' @t
        run vm-snapshots -n $base } }
    'clone'    = @{ Text = 'Zwei Klone der Basis (benannt und automatisch)'; Do = {
        run vm-clone -s $base -n $clone @t
        run vm-clone -s $base @t
        run vm-list } }
    'use'      = @{ Text = 'Klon starten und benutzen'; Do = {
        run vm-start -n $clone @t
        run vm-exec -n $clone -c 'hostname; claude --version' @t } }
    'restore'  = @{ Text = 'Basis auf den Snapshot zurücksetzen (fragt nach Backup)'; Do = {
        run vm-restore -n $base -s frisch @t
        run vm-snapshots -n $base } }
    'errors'   = @{ Text = 'Fehlerfälle: unbekannte VM, doppelter Snapshot, fehlender Snapshot, ungültiger Name'; Do = {
        run vm-start -n cbx-suite-gibtsnicht
        run vm-snapshot -n $base -s frisch
        run vm-restore -n $base -s gibtsnicht
        run vm-clone -s $base -n 'kaputt-' } }
    'running'  = @{ Text = 'Snapshot einer laufenden VM (fragt: stoppen?)'; Do = { run vm-snapshot -n $clone @t } }
    'timeout'  = @{ Text = '"Weiter warten?"-Dialog: Anlegen mit zu kurzem Timeout'; Do = {
        run vm-create -n $slow -t '2s'
        run vm-list } }
    'list'     = @{ Text = 'Übersicht'; Do = {
        run vm-list
        run vm-snapshots } }
    'cleanup'  = @{ Text = 'Alle cbx-suite-VMs und das Profil entfernen'; Do = {
        foreach ($vm in @($clone, "$base-clone2", $slow, $base)) {
            run vm-stop -n $vm @t
            run vm-delete -n $vm --purge @t
        }
        run init-delete -n $profileName
        run vm-list } }
}

$names = @($steps.Keys)
if ($List) {
    for ($i = 0; $i -lt $names.Count; $i++) { '{0,2}  {1,-9} {2}' -f ($i + 1), $names[$i], $steps[$names[$i]].Text }
    return
}

$start = 0
if ($From) {
    $start = if ($From -match '^\d+$') { [int]$From - 1 } else { $names.IndexOf($From) }
    if ($start -lt 0) { throw "Unbekannter Schritt '$From'. Schritte: $($names -join ', ')" }
}

$mode = if ($Real) { 'ECHTES Multipass' } else { 'Simulator (cb-sim)' }
Write-Host "CoreBridge-Suite gegen $mode" -ForegroundColor Green
for ($i = $start; $i -lt $names.Count; $i++) {
    $name = $names[$i]
    if ($Only -and $name -notin $Only) { continue }
    Write-Host ("`n=== {0}/{1} {2}: {3}" -f ($i + 1), $names.Count, $name, $steps[$name].Text) -ForegroundColor Green
    & $steps[$name].Do
    if ($Pause -and $i -lt $names.Count - 1) { Read-Host 'Enter = weiter, Strg+C = abbrechen' | Out-Null }
}
