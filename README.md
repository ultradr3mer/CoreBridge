# CoreBridge — Multipass Bridge

.NET-10-Konsolen-App, die klassische Ubuntu-26.10-Edge-VMs unter
Multipass/Hyper-V verwaltet: anlegen, starten/stoppen, Dateien austauschen,
Kommandos ausführen — als Grundlage dafür, ein echtes Host-Git-Repo mit
isolierten Agent-VMs (Claude Code, OpenCode, ...) zu verbinden, ohne dass die
VM einen direkten Zugriff auf den Host bekommt.

## Voraussetzungen

- Windows mit aktiviertem Hyper-V
- [Multipass](https://canonical.com/multipass/download/windows) (getestet mit
  1.16.3/1.16.4) — Installer, `winget install Canonical.Multipass` (laut
  Canonical nicht offiziell unterstützt) oder Direktdownload
- .NET SDK 10 (`global.json` pinnt `10.0.401`, `rollForward: latestPatch`)

`cb multipass-status` prüft das nach der Installation (siehe unten).

## Schnellstart

```powershell
git clone https://support.ngroup.info/Root/Sandbox/_git/Clara-CoreBridge
cd CoreBridge
dotnet build
.\scripts\install-cb.ps1   # installiert die `cb`-Shell-Funktion in beide PS-Profile
```

Danach neues PowerShell-Fenster öffnen (oder `. $PROFILE`) und aus **jedem**
Verzeichnis:

```powershell
cb multipass-status
cb image-configure --name mein-image [ --components claude,dotnet ]
cb vm-create --name meine-vm --image mein-image
cb vm-start --name meine-vm 
cb vm-exec --name meine-vm --command 'echo hallo'
cb vm-connect --name meine-vm
cb vm-delete --name meine-vm
```

`image-configure` erzeugt ein Profil. `vm-create --image <profilname>` löst
dieses Profil auf, startet `daily:26.10` und übergibt die generierte
cloud-init. Alle Gast-Komponenten, einschließlich Claude, werden dort
installiert. Ein direkt angegebener Katalogname wie `26.04` bleibt ein
normaler, unprofilierter Launch. `vm-connect` ist eine interaktive
Multipass-Shell und benötigt ein echtes Terminal.

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
| `vm-create -n <name> [-i <image>] [-c <cloud-init>] [-d <disk>]` | Legt eine neue VM an (Standard: `daily:26.10`, Classic Ubuntu 26.10 Edge) |
| `vm-start -n <name>` | Startet eine gestoppte VM |
| `vm-stop -n <name>` | Stoppt eine laufende VM |
| `vm-delete -n <name> [-p/--purge]` | Löscht eine VM in den Papierkorb; `--purge` endgültig |
| `vm-exec -n <name> -c <command> [-t <timeout-s>]` | Führt ein Kommando in der VM aus (`/bin/sh -c`) |
| `vm-connect -n <name>` | Öffnet eine interaktive Shell in der VM (nur im echten Terminal, nicht in Agent-Shells) |
| `vm-put -n <name> -s <lokale-datei> [-d <ziel-in-vm>]` | Kopiert eine Datei Host → VM |
| `vm-get -n <name> -r <pfad-in-vm> -d <lokales-ziel>` | Kopiert eine Datei VM → Host |

Classic-Profile (Ubuntu-26.10-Edge mit cloud-init-basierter Gast-Provisionierung):

| Befehl | Zweck |
|---|---|
| `image-configure -n <name> [-c claude,dotnet,opencode,python] [--model-url <url>] [--runtime-image <image>] [-f/--force]` | Erzeugt ein Classic-Profil (Standardmodell `ubuntu-classic-2610-amd64-dangerous.model`, Runtime `daily:26.10`) und persistiert die Gast-cloud-init |
| `image-list` | Listet konfigurierte Classic-Profile (Komponenten und Snaps) |
| `image-delete -n <name>` | Löscht ein Classic-Profil (Modell, Profilkonfiguration und cloud-init) |

T11 bleibt als historische Custom-Image-Evidenz in `EXPERIMENT_LOG.md`.
Der aktive Workflow verwendet ausschließlich Runtime-Profile plus Cloud-init;
eigenes Image-Bauen und Windows-Import sind nicht Bestandteil von CoreBridge.

`--help` bzw. `<verb> --help` zeigt die aktuelle Referenz direkt aus dem Code
(CommandLineParser generiert sie aus den `[Verb]`/`[Option]`-Attributen in
`src/CoreBridge.Cli/Options.cs`) — im Zweifel verbindlicher als diese Tabelle.

## Konfiguration

CLI liest `config.Get<AppConfig>()` aus (Reihenfolge, letztes gewinnt):
`appsettings.json` → `appsettings.{DOTNET_ENVIRONMENT}.json` (Default
`Development`) → `appsettings.local.json` (gitignored, für lokale
Überschreibungen) → Umgebungsvariablen (doppelter Unterstrich, z. B.
`Images__StoreRoot`).

Wichtige Einstellungen (`src/CoreBridge.Cli/appsettings.json`):

- `Images:StoreRoot` — wohin `image-configure` Modelle, Profilkonfigurationen
  und cloud-init ablegt (Development: `artifacts`)
- `Images:DefaultModelUrl` — Standardmodell
  `ubuntu-classic-2610-amd64-dangerous.model`
- `Images:DefaultRuntimeImage` — Standard-Runtime `daily:26.10`
  (Classic Ubuntu 26.10 Edge)
- `Images:Components` — Gast-Skripte, Pakete, Benutzer und optionale
  Store-Snaps. Komponentenspezifische Symlinks, Umgebungsvariablen und
  Bibliotheks-Setup liegen im jeweiligen Gast-Skript; Claude wird standardmäßig
  über eine eingebettete cloud-init-Datei provisioniert, nicht über einen
  Core-Snap.

**Keine Passwörter in der Config** — Secrets laufen über die Env-Kette
`CORE_BRIDGE_[ALL,<NAME>,...]_PASSWORD` (Sync-Feature, noch nicht
implementiert).

## Tests & Experimente

```powershell
dotnet test                                             # alles
dotnet test test/CoreBridge.Core.Tests                  # schnelle Core-/Image-Unit-Tests
dotnet test test/CoreBridge.Cli.Tests --filter "Category!=Integration"
dotnet test test/CoreBridge.Cli.Tests                   # echter Classic-CLI-Lifecycle
dotnet test test/Multipass.Experiments                  # historische Experimente
```

- `test/CoreBridge.Cli.Tests` → `CliLifecycleTests` — der öffentliche
  CLI-first-Lifecycle als deterministisch geordnete, separate Stufen. Die
  Stufen verwenden ausschließlich `Program.Run(AppConfig, IOptions)` mit
  synthetischer Konfiguration und temporärem StoreRoot; der native
  `ProgramExecution`-Rückgabewert ist ein `IAsyncEnumerable<string>`. Die Tests
  starten keinen CLI-Kindprozess und capturen keine Console-Ausgabe.
  `multipass-status` →
  `image-configure` → `vm-create --image <profil>` → `vm-start` → `vm-exec` →
  Claude-Postcondition → Cleanup. Ein gemeinsames Collection-Fixture besitzt
  eindeutige `cbx-cli-*`-Namen und cleanup ohne `--purge`.
- Der Integrationstest verwendet `DOTNET_ENVIRONMENT=Development` und
  überschreibt `Images:StoreRoot` für seinen Collection-Fixture-Lauf mit einem
  eindeutigen temporären Verzeichnis. Temporäre Dateien für den
  Claude-Download landen ausschließlich in einem Test-`TEMP`-Verzeichnis.
- `test/CoreBridge.Core.Tests` enthält weiterhin schnelle Unit-Tests für
  Core, Image-Profilauflösung, Modellvalidierung und Cloud-init. Die alte
  direkte Service-Integration wurde in den CLI-Test verschoben.
- `test/Multipass.Experiments` — die T01–T11-Experimente, die die
  historischen Multipassaussagen belegen. Jeder Lauf wird protokolliert:
  Kommando, Exit-Code, Dauer, Roh-Ausgabe in `EXPERIMENT_LOG.md`, volle
  Rohdaten unter `experiment-data/`.
- Beide Multipass-Testprojekte überspringen sich automatisch, wenn kein
  Multipass gefunden wird. Für reale Läufe gilt weiterhin: niemals
  `multipass` direkt in der Shell aufrufen, sondern `dotnet test`.
- `test/Multipass.Experiments` — die T01–T11-Experimente, die die
  Architekturentscheidungen belegen (Latenzen, JSON-Formate, Netzwerk, SSH,
  Lifecycle). Jeder Lauf wird protokolliert: Kommando, Exit-Code, Dauer,
  Roh-Ausgabe in `EXPERIMENT_LOG.md`, volle Rohdaten unter `experiment-data/`.
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
  CoreBridge.Core.Tests   Unit-Tests für Core/Image-Profil/Cloud-init
  CoreBridge.Cli.Tests    CLI-Contract- und Lifecycle-Tests via Program.Run
  Multipass.Experiments   reproduzierbare historische Experimente
scripts/               cb-Shortcut, Cloud-init-Gastskripte, Hyper-V-Gruppierung
```

`src/CoreBridge.Tui` (Terminal-UI, geplant mit
[Terminal.Gui](https://github.com/tui-cs/Terminal.Gui) v2) ist für eine
spätere Phase vorgesehen und wurde bisher nicht angegangen — die CLI ist
bislang einfach genug (wenige Verbs, kein interaktiver Dauerbetrieb), dass
eine TUI noch keinen Mehrwert gebracht hätte.

## Status

Phase D (siehe `IMPLEMENTATION_PLAN.md` §10): VM-Lifecycle, Datei-transfer
und die Classic-26.10-Edge-Profil-Pipeline sind implementiert. `image-configure`
schreibt ein launchbares Profil; `vm-create --image <profil>` startet
`daily:26.10` und übergibt die cloud-init, die Claude und die übrigen
Gast-Komponenten installiert. Die spätere Autobridge bleibt offen. T11 bleibt
als historische Custom-Image-Evidenz erhalten; ein aktiver Image-Build-Pfad
existiert nicht. Aktueller Stand im Detail: `AGENTS.md` → „Aktueller Stand“.

## Weiterführende Dokumentation

- `AGENTS.md` — Regeln und Detailstand für Beiträge/Agenten (verbindlicher
  als dieses README, wenn beides auseinanderläuft)
- `IMPLEMENTATION_PLAN.md` — der verbindliche Plan: Leitsätze, Phasen,
  Experimente T01–T11, offene Fragen, Umsetzungsstatus
- `EXPERIMENT_LOG.md` / `experiment-data/` — alle bisherigen Experimentläufe
- CommandLineParser 2.9.1: <https://github.com/commandlineparser/commandline>
- `PROJECT_MULTIPASS_BRIDGE.md` — die ursprüngliche Projektidee (Ausgangspunkt,
  nicht der aktuelle Stand)

## Lizenz

MIT — siehe `LICENSE`.
