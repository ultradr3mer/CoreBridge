# CoreBridge — Multipass Bridge

.NET-10-Konsolen-App, die Ubuntu-Core-VMs unter Multipass/Hyper-V verwaltet:
anlegen, starten/stoppen, Dateien austauschen, Kommandos ausführen — als
Grundlage dafür, ein echtes Host-Git-Repo mit isolierten Agent-VMs (Claude
Code, OpenCode, ...) zu verbinden, ohne dass die VM einen direkten Zugriff
auf den Host bekommt.

## Voraussetzungen

- Windows mit aktiviertem Hyper-V
- [Multipass](https://canonical.com/multipass/download/windows) (getestet mit
  1.16.3/1.16.4) — Installer, `winget install Canonical.Multipass` (laut
  Canonical nicht offiziell unterstützt) oder Direktdownload
- .NET SDK 10 (`global.json` pinnt `10.0.401`, `rollForward: latestPatch`)

`cb multipass-status` prüft das nach der Installation (siehe unten).

## Schnellstart

```powershell
git clone https://github.com/ultradr3mer/CoreBridge.git
cd CoreBridge
dotnet build
.\scripts\install-cb.ps1   # installiert die `cb`-Shell-Funktion in beide PS-Profile
```

Danach neues PowerShell-Fenster öffnen (oder `. $PROFILE`) und aus **jedem**
Verzeichnis:

```powershell
cb multipass-status
cb vm-create --name meine-vm
cb vm-exec --name meine-vm --command 'echo hallo'
cb vm-delete --name meine-vm
```

`cb` ruft im Repo `dotnet run --project src/CoreBridge.Cli -- @args` auf; ohne
Repo würde es einen installierten `core-bridge`-Befehl erwarten (noch nicht
gebaut/veröffentlicht). Ohne den Shortcut geht's auch direkt:

```powershell
dotnet run --project src/CoreBridge.Cli -- <verb> ...
```

**Wichtig unter PowerShell:** bei `--command`/`-c` (z. B. `vm-exec`) immer
Single-Quotes um den Befehl setzen. Mit Double-Quotes expandiert PowerShell
`$HOME`/`$(...)` selbst, bevor die VM sie sieht.

## Befehle

VM-Lifecycle:

| Befehl | Zweck |
|---|---|
| `multipass-status` | Prüft, ob Multipass auffindbar ist, gibt CLI- und Daemon-Version aus |
| `vm-list` | Listet alle Multipass-VMs (inkl. Papierkorb, state `Deleted`) |
| `vm-create -n <name> [-i <image>] [-c <cloud-init>] [-d <disk>]` | Legt eine neue VM an (Standard-Image: `core26`) |
| `vm-start -n <name>` | Startet eine gestoppte VM |
| `vm-stop -n <name>` | Stoppt eine laufende VM |
| `vm-delete -n <name> [-p/--purge]` | Löscht eine VM in den Papierkorb; `--purge` endgültig |
| `vm-exec -n <name> -c <command> [-t <timeout-s>]` | Führt ein Kommando in der VM aus (`/bin/sh -c`) |
| `vm-connect -n <name>` | Öffnet eine interaktive Shell in der VM (nur im echten Terminal, nicht in Agent-Shells) |
| `vm-put -n <name> -s <lokale-datei> [-d <ziel-in-vm>]` | Kopiert eine Datei Host → VM |
| `vm-get -n <name> -r <pfad-in-vm> -d <lokales-ziel>` | Kopiert eine Datei VM → Host |

Custom-Images (Ubuntu-Core-Image mit vorinstallierten Snaps/Tools bauen):

| Befehl | Zweck |
|---|---|
| `image-configure -n <name> [-c claude,dotnet,opencode,python] [--model-url <url>] [-b <build-to>] [-f/--force]` | Erzeugt Build-Config, Basis-Modell (Canonical, signiert) und cloud-init für ein Custom-Image |
| `image-list` | Listet konfigurierte Custom-Images (Komponenten, Snaps, vorhandene `.img`) |
| `image-delete -n <name>` | Löscht Konfiguration + gebaute `.img` eines Custom-Images |

`image-build` existiert bereits (baut in einer Wegwerf-Ubuntu-VM via
`ubuntu-image`), ist aber noch nicht end-to-end verifiziert und deshalb aus
der Hilfe ausgeblendet — siehe `IMPLEMENTATION_PLAN.md` §10.

`--help` bzw. `<verb> --help` zeigt die aktuelle Referenz direkt aus dem Code
(CommandLineParser generiert sie aus den `[Verb]`/`[Option]`-Attributen in
`src/CoreBridge.Cli/Verbs.cs`) — im Zweifel verbindlicher als diese Tabelle.

### Beispiel: VM mit vorinstalliertem Claude Code + .NET

```powershell
cb image-configure --name agent-vm --components claude,dotnet
cb vm-create --name meine-agent-vm --cloud-init "$env:LOCALAPPDATA\CoreBridge\models\agent-vm\cloud-init.yaml"
```

`image-configure` gibt den passenden `vm-create`-Aufruf am Ende selbst aus.

## Konfiguration

CLI liest `config.Get<AppConfig>()` aus (Reihenfolge, letztes gewinnt):
`appsettings.json` → `appsettings.{DOTNET_ENVIRONMENT}.json` (Default
`Development`) → `appsettings.local.json` (gitignored, für lokale
Überschreibungen) → Umgebungsvariablen (doppelter Unterstrich, z. B.
`Images__Build__VmName`).

Wichtige Einstellungen (`src/CoreBridge.Cli/appsettings.json`):

- `Images:StoreRoot` — wohin `image-configure`/`image-build` Modelle,
  cloud-init und `.img`-Dateien ablegen (Default `%LOCALAPPDATA%\CoreBridge`)
- `Images:Components` — verfügbare Custom-Image-Bausteine (Snap-Name,
  `--classic`-Flag, Symlinks, `.profile`-Exports, vendored Libs). Neue
  Komponente = neuer Config-Eintrag, kein Code
- `Images:Build` — Default-Bau-VM/-Image/-Disk/-Timeout für `image-build`

**Keine Passwörter in der Config** — Secrets laufen über die Env-Kette
`CORE_BRIDGE_[ALL,<NAME>,...]_PASSWORD` (Sync-Feature, noch nicht
implementiert).

## Tests & Experimente

```powershell
dotnet test                                    # alles
dotnet test test/CoreBridge.Core.Tests         # Unit- + Integrationstests
dotnet test test/Multipass.Experiments         # Experimente (siehe EXPERIMENT_LOG.md)
```

- `test/CoreBridge.Core.Tests` — u. a. `IntegrationsTests.cs`: ein
  durchgängiger `image-configure` → `vm-create` → `vm-exec`/`vm-connect`
  (Pipe **und** natives SSH) → `vm-stop`/`vm-delete` → `image-delete`-Zyklus
  gegen eine echte `cbx-cycle-a`-VM.
- `test/Multipass.Experiments` — die T01–T11-Experimente, die die
  Architekturentscheidungen belegen (Latenzen, JSON-Formate, Netzwerk, SSH,
  Lifecycle). Jeder Lauf wird protokolliert: Kommando, Exit-Code, Dauer,
  Roh-Ausgabe in `EXPERIMENT_LOG.md`, volle Rohdaten unter `experiment-data/`.
- Beide überspringen sich automatisch (kein Fehlschlag), wenn kein Multipass
  gefunden wird.
- Env `CORE_BRIDGE_MULTIPASS_EXE` überschreibt das Binary für Tests.

**Für Beiträge/Agenten gilt eine harte Regel:** Multipass wird nie direkt in
der Shell aufgerufen, jedes Ergebnis muss über `dotnet test` reproduzierbar
sein. Details und der volle Regelkatalog stehen in `AGENTS.md`.

## Projektstruktur

```
src/
  CoreBridge.Core    Logik, kein Konsolen-I/O — Vm/ (MultipassCli-Wrapper,
                      VmService, Doctor), Images/ (ImageService, ImageStore),
                      Config/ (AppConfig)
  CoreBridge.Cli      CommandLineParser-Verbs + Ausgabe (einziges Projekt mit
                      dem NuGet-Paket CommandLineParser)
test/
  CoreBridge.Core.Tests   Unit- + Integrationstests
  Multipass.Experiments   reproduzierbare Experimente, keine klassischen Tests
scripts/               cb-Shortcut, VM-Bootstrap, Custom-Image-Build, Hyper-V-Gruppierung
```

`src/CoreBridge.Tui` (Terminal-UI) ist für eine spätere Phase vorgesehen,
existiert noch nicht.

## Status

Phase D (siehe `IMPLEMENTATION_PLAN.md` §10): VM-Lifecycle-Verbs, Datei-
transfer und die Custom-Image-Pipeline (`image-configure`/`image-list`/
`image-delete`) sind implementiert und end-to-end verifiziert; `image-build`
und die geplante Autobridge (automatischer Host↔VM-Workflow für Coding-Agents)
sind offene Punkte. Aktueller Stand im Detail: `AGENTS.md` → „Aktueller
Stand“.

## Weiterführende Dokumentation

- `AGENTS.md` — Regeln und Detailstand für Beiträge/Agenten (verbindlicher
  als dieses README, wenn beides auseinanderläuft)
- `IMPLEMENTATION_PLAN.md` — der verbindliche Plan: Leitsätze, Phasen,
  Experimente T01–T11, offene Fragen, Umsetzungsstatus
- `EXPERIMENT_LOG.md` / `experiment-data/` — alle bisherigen Experimentläufe
- `COMMANDLINEPARSER_COMMANDLINE_README.md` — Referenz für CommandLineParser
  2.9.1
- `PROJECT_MULTIPASS_BRIDGE.md` — die ursprüngliche Projektidee (Ausgangspunkt,
  nicht der aktuelle Stand)

## Lizenz

MIT — siehe `LICENSE`.
