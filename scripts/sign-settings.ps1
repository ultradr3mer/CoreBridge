<#
.SYNOPSIS
  Signiert die mitgelieferten appsettings (oder angegebene Dateien) für CoreBridge.

.DESCRIPTION
  Deployment-Werkzeug, nicht Teil des Produkts: CoreBridge prüft beim Start
  appsettings.json und appsettings.{Environment}.json gegen den eingebetteten
  CoreBridge-Schlüssel (src/CoreBridge.Core/Authenticity/corebridge-signing.pub.asc).
  Signiert gilt eine Datei mit <datei>.clearsigned.asc oder <datei>.asc daneben.
  Fehlt die Signatur, warnt die CLI; ist sie ungültig (auch: Datei nach dem
  Signieren geändert), bricht sie mit Exit 3 ab.

  Pro Datei: gpg --detach-sign --armor --textmode (CanonicalTextDocument, damit
  CRLF/LF-Wechsel durch git die Signatur nicht bricht), SHA-256. Danach wird
  jede Signatur sofort geprüft: per gpg (VALIDSIG muss den erwarteten
  Fingerprint nennen) und per `cb settings-verify -f` gegen den eingebetteten
  Schlüssel. Die Passphrase fragt gpg-agent/pinentry ab; das Skript sieht sie nie.

  Mit -ClearSign entsteht stattdessen <datei>.clearsigned.asc (gpg --clearsign:
  Inhalt und Signatur in einer Datei); die abgetrennte <datei>.asc bleibt unberührt.

  Ohne gpg-agent (CI): tools/CoreBridge.Signer.

.PARAMETER Files
  Zu signierende Dateien. Ohne Angabe: src/CoreBridge.Cli/appsettings*.json
  außer appsettings.local.json.

.PARAMETER KeyId
  Fingerprint des Signierschlüssels. Default: $env:CORE_BRIDGE_SIGNING_KEY, sonst
  der CoreBridge-Schlüssel.

.PARAMETER NoCliCheck
  Überspringt die Gegenprobe mit `cb settings-verify` (nur die gpg-Prüfung).

.PARAMETER ClearSign
  Klartextsignatur (gpg --clearsign) nach <datei>.clearsigned.asc statt abgetrennter
  Signatur. Die CLI lädt weiter die Datei selbst und prüft sie gegen diese Signatur.

.EXAMPLE
  .\scripts\sign-settings.ps1
.EXAMPLE
  .\scripts\sign-settings.ps1 -Files src\CoreBridge.Cli\appsettings.json
.EXAMPLE
  .\scripts\sign-settings.ps1 -ClearSign
#>
param(
    [string[]] $Files,
    [string] $KeyId = $(if ($env:CORE_BRIDGE_SIGNING_KEY) { $env:CORE_BRIDGE_SIGNING_KEY } else { '7D67FF405BAA2BB53027F57280E7816D2A7F227B' }),
    [switch] $NoCliCheck,
    [switch] $ClearSign
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$KeyId = ($KeyId -replace '\s', '').ToUpperInvariant()

# Gpg4win bevorzugen: das gpg aus Git for Windows hat einen eigenen Keyring und oft keinen Agent.
$gpg = Get-Command gpg -All -ErrorAction SilentlyContinue |
    Sort-Object { if ($_.Source -match '\\Git\\') { 1 } else { 0 } } |
    Select-Object -First 1
if (-not $gpg) {
    throw 'gpg nicht gefunden — Gpg4win installieren (https://gpg4win.org) oder tools/CoreBridge.Signer verwenden.'
}

if (-not $Files) {
    $Files = Get-ChildItem -LiteralPath (Join-Path $root 'src\CoreBridge.Cli') -Filter 'appsettings*.json' |
        Where-Object Name -ne 'appsettings.local.json' |
        ForEach-Object FullName
}

$Files = $Files | ForEach-Object { (Resolve-Path -LiteralPath $_).Path }
Write-Host "gpg: $($gpg.Source)"
Write-Host "Schlüssel: $KeyId"

foreach ($file in $Files) {
    if ($ClearSign) {
        $signature = "$file.clearsigned.asc"
        & $gpg.Source --batch --yes --local-user $KeyId --clearsign --digest-algo SHA256 --output $signature $file
    }
    else {
        $signature = "$file.asc"
        & $gpg.Source --batch --yes --local-user $KeyId --detach-sign --armor --textmode --digest-algo SHA256 --output $signature $file
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Signieren fehlgeschlagen: $file (gpg exit $LASTEXITCODE)"
    }

    # Eine Klartextsignatur enthält den Inhalt selbst; die abgetrennte braucht die Datei dazu.
    $status = if ($ClearSign) {
        & $gpg.Source --batch --status-fd 1 --verify $signature 2>$null
    }
    else {
        & $gpg.Source --batch --status-fd 1 --verify $signature $file 2>$null
    }
    $valid = $status | Where-Object { $_ -match '^\[GNUPG:\] VALIDSIG (\S+)' -and $Matches[1] -eq $KeyId }
    if (-not $valid) {
        throw "gpg-Prüfung fehlgeschlagen: $signature ist keine gültige Signatur von $KeyId."
    }

    Write-Host "signiert: $signature"
}

if (-not $NoCliCheck) {
    # Gegenprobe mit dem eingebetteten Schlüssel, wie CoreBridge beim Start prüft.
    & dotnet run --project (Join-Path $root 'src\CoreBridge.Cli') -- settings-verify -f ($Files -join ',')
    if ($LASTEXITCODE -ne 0) {
        throw "cb settings-verify meldet ungültige Signaturen (exit $LASTEXITCODE)."
    }
}
