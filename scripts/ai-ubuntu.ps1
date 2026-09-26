<#
.SYNOPSIS
    Baut die ai-ubuntu-Familie: eine nackte Basis-VM mit Snapshot, zwei Klone,
    in die danach dotnet bzw. python installiert und gesichert werden.

.DESCRIPTION
    Teil 1 — Basis und Klone:
      ai-ubuntu           ohne Komponenten, Snapshot "base"
      ai-ubuntu-dotnet    Klon von ai-ubuntu
      ai-ubuntu-python    Klon von ai-ubuntu

    Teil 2 — Komponenten nachinstallieren (cb vm-install):
      ai-ubuntu-dotnet    dotnet installieren, Diagnose, Snapshot "dotnet" (nur bei Erfolg)
      ai-ubuntu-python    python installieren, Diagnose, Snapshot "python" (nur bei Erfolg)

    Ergebnis: ai-ubuntu.base als unberührter Ausgangspunkt, dazu je ein Snapshot
    mit dotnet bzw. python, auf dem man weiterbauen kann.

    Jeder Schritt wird gemessen; am Ende jedes Teils stehen Dauer, Exit-Codes,
    VMs und Snapshots.

.PARAMETER Part
    1 oder 2. Ohne Angabe: Teil 1, dann Pause, dann Teil 2.

.PARAMETER Sim
    Gegen den Multipass-Simulator (cb-sim) statt echtes Multipass.

.EXAMPLE
    .\scripts\ai-ubuntu.ps1 -Part 1
.EXAMPLE
    .\scripts\ai-ubuntu.ps1 -Part 2
.EXAMPLE
    .\scripts\ai-ubuntu.ps1 -Sim
#>
param(
    [ValidateSet(1, 2)]
    [int]$Part,
    [switch]$Sim
)

$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
. (Join-Path $PSScriptRoot 'cb.ps1')

$base = 'ai-ubuntu'
$dotnetVm = 'ai-ubuntu-dotnet'
$pythonVm = 'ai-ubuntu-python'

$script:results = [System.Collections.Generic.List[object]]::new()

<# Führt einen cb-Befehl aus, misst ihn und merkt sich das Ergebnis in
   $script:lastExit. Bewusst OHNE Rückgabewert und immer als Anweisung aufrufen
   (nie zuweisen oder pipen): nur dann schreibt cb direkt in die Konsole — mit
   Spinner und Rückfragen — statt in eine umgeleitete Pipeline.
   -Critical: bei Fehler den Teil abbrechen (Folgeschritte bauen darauf auf). #>
function step {
    param(
        [Parameter(Mandatory)] [string]$Title,
        [Parameter(Mandatory)] [string[]]$Cb,
        [switch]$Critical
    )

    $tool = if ($Sim) { 'cb-sim' } else { 'cb' }
    $line = ($Cb | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' '
    Write-Host "`n--- $Title" -ForegroundColor Green
    Write-Host "> $tool $line" -ForegroundColor Cyan
    $watch = [Diagnostics.Stopwatch]::StartNew()
    if ($Sim) { cb-sim @Cb } else { cb @Cb }
    $exit = $LASTEXITCODE
    $script:lastExit = $exit
    $watch.Stop()
    $color = if ($exit -eq 0) { 'DarkGray' } else { 'Yellow' }
    Write-Host ("  exit {0} nach {1:N1} s" -f $exit, $watch.Elapsed.TotalSeconds) -ForegroundColor $color
    $script:results.Add([pscustomobject]@{ Schritt = $Title; Dauer = '{0,7:N1} s' -f $watch.Elapsed.TotalSeconds; Exit = $exit })
    if ($Critical -and $exit -ne 0) {
        throw "Abbruch: '$Title' ist fehlgeschlagen (Exit $exit) — die folgenden Schritte bauen darauf auf."
    }
}

function summary([string]$Name, [Diagnostics.Stopwatch]$Watch) {
    Write-Host "`n=== Zusammenfassung $Name ($('{0:N1}' -f $Watch.Elapsed.TotalSeconds) s gesamt)" -ForegroundColor Green
    $script:results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    if ($Sim) { cb-sim vm-list; cb-sim vm-snapshots } else { cb vm-list; cb vm-snapshots }
    $script:results.Clear()
}

function part1 {
    Write-Host "`n###### Teil 1: Basis-VM ohne Komponenten, Snapshot, zwei Klone" -ForegroundColor Magenta
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        step 'Basis-VM anlegen (ohne Komponenten)' @('vm-create', '-n', $base, '-t', '600s') -Critical
        step 'Basis prüfen' @('vm-exec', '-n', $base, '-c', 'grep PRETTY /etc/os-release; df -h / | tail -1', '-t', '120s')
        step 'Basis stoppen' @('vm-stop', '-n', $base, '-t', '180s') -Critical
        step 'Snapshot base' @('vm-snapshot', '-n', $base, '-s', 'base', '-m', 'frisch, ohne Komponenten', '-t', '120s') -Critical
        step "Klon $dotnetVm" @('vm-clone', '-s', $base, '-n', $dotnetVm, '-t', '600s') -Critical
        step "Klon $pythonVm" @('vm-clone', '-s', $base, '-n', $pythonVm, '-t', '600s') -Critical
    }
    catch {
        Write-Host $_ -ForegroundColor Red
    }
    finally {
        summary 'Teil 1' $watch
    }
}

<# Installiert eine Komponente per vm-install; der Snapshot entsteht nur bei Erfolg.
   Danach Diagnose — auch nach einem Fehler, dann läuft die VM ohnehin noch. #>
function install([string]$Vm, [string]$Component, [string[]]$Diagnose) {
    step "$Component in $Vm installieren (+ Snapshot $Component bei Erfolg)" @('vm-install', '-n', $Vm, '-c', $Component, '--snapshot', $Component, '-t', '900s')
    $exit = $script:lastExit
    step "$Vm für die Diagnose starten" @('vm-start', '-n', $Vm, '-t', '180s')
    foreach ($command in $Diagnose) {
        step "Diagnose: $command" @('vm-exec', '-n', $Vm, '-c', $command, '-t', '120s')
    }

    step "$Vm stoppen" @('vm-stop', '-n', $Vm, '-t', '180s')
    $verdict = if ($exit -eq 0) { "OK — Snapshot $Vm.$Component angelegt" } else { "FEHLGESCHLAGEN (Exit $exit) — kein Snapshot, siehe Diagnose" }
    Write-Host "`n>>> $Component in ${Vm}: $verdict" -ForegroundColor $(if ($exit -eq 0) { 'Green' } else { 'Red' })
}

function part2 {
    Write-Host "`n###### Teil 2: dotnet und python in die Klone installieren" -ForegroundColor Magenta
    $watch = [Diagnostics.Stopwatch]::StartNew()

    # Diagnose dotnet: vm-exec läuft als nicht-Login-sh; install-dotnet.sh verlinkt
    # dotnet nur nach ~/.local/bin, das steht dort womöglich nicht im PATH.
    install $dotnetVm 'dotnet' @(
        'dotnet --version',
        "bash -lc 'dotnet --version'",
        '/snap/bin/dotnet --version',
        'ls -l ~/.local/bin',
        'ls /var/lib/corebridge'
    )

    install $pythonVm 'python' @(
        'python3.14 --version',
        "bash -lc 'python3.14 -m venv /tmp/probe && /tmp/probe/bin/python --version'",
        'ls /var/lib/corebridge'
    )

    summary 'Teil 2' $watch
}

$mode = if ($Sim) { 'Simulator (cb-sim)' } else { 'ECHTES Multipass' }
Write-Host "ai-ubuntu gegen $mode" -ForegroundColor Green
switch ($Part) {
    1 { part1 }
    2 { part2 }
    default {
        part1
        Read-Host "`nTeil 1 fertig. Enter = Teil 2 starten, Strg+C = hier aufhören" | Out-Null
        part2
    }
}
