# AGENTS.md — CoreBridge

.NET-10-Konsolen-App („Multipass Bridge“): verbindet ein echtes Host-Git-Repo mit
isolierten Agent-VMs über Multipass/Hyper-V. Frühstadium — der Plan (Phasen A–D)
ist verbindlicher als der vorhandene Code.

## Wichtigste Regel (VERBOT)

**Niemals `multipass` direkt in der Shell aufrufen — auch nicht zum Nachschauen.**
Einziger Weg an Multipass: `dotnet test` auf `test/Multipass.Experiments` bzw. der
`MultipassCli`-Wrapper in `src/CoreBridge.Core/Vm/`. Jedes Ergebnis muss reproduzierbar
sein und in `EXPERIMENT_LOG.md` landen — „mal eben in der Shell ausprobieren“ ist nie
ein Ergebnis.

## Lesen zuerst (in dieser Reihenfolge)

1. `IMPLEMENTATION_PLAN.md` — verbindlicher Plan: Leitsätze, Phasen, Experimente
   T01–T10, offene Fragen, Status §10.
2. `COMMANDLINEPARSER_COMMANDLINE_README.md` — Referenz für CommandLineParser 2.9.1,
   die einzige CLI-Parser-Bibliothek.
3. `PROJECT_MULTIPASS_BRIDGE.md` — ursprüngliche Projektidee. Achtung Leitsatz 2:
   Nichts wird daraus übernommen, *weil es dort steht* — jede Übernahme ist eine
   bewusste Entscheidung mit Verweis auf einen Log-Eintrag.
- `EXPERIMENT_LOG.md` — alle bisherigen Experimentläufe (Kommando, Exit, Dauer,
  Roh-Ausgabe); selbst anlegen, falls noch nicht vorhanden.
- Weitere Quellen hinten an: `F:\source\PROJECT.md` (alte SandboxBridge-Spec,
  Inhaltsbasis); lokale Multipass-Doku als blobless Clone unter `refs/multipass`
  (sparse auf `docs/`, gegitignored).

## Aktueller Stand (25.09.2026) — Plan §10 laufend gegen den Repo-Zustand prüfen

### Verbindliche Zielentscheidung: Classic Ubuntu 26.10 Edge

- Neues Standardmodell: `ubuntu-classic-2610-amd64-dangerous.model`
  (`https://raw.githubusercontent.com/canonical/models/main/ubuntu-classic-2610-amd64-dangerous.model`).
- Runtime-Image für normale VMs und Profile: `daily:26.10`.
- `26.04` in `Images:Build:Image` bleibt ausschließlich das Werkzeug-Image
  der klassischen Ubuntu-Bau-VM; es ist kein Fallback für die Ziel-Runtime.
- `core26` und die alten Core-Messungen bleiben historische Experimentdaten,
  keine Defaults für neue Implementierungen oder Tests.
- `image-configure` erzeugt ein Profil; `vm-create --image <profil>` löst
  Runtime und Cloud-init auf. Claude wird standardmäßig über ein in die
  cloud-init-YAML eingebettetes Gast-Skript (`scripts/components/install-claude.sh`)
  installiert, nicht über einen Core-only Claude-Snap.
- `CLOUD-INIT.md` beschreibt die Gast-Provisionierung; es gibt keine
  Host-Installationsschritte im aktuellen Classic-Workflow.

- Phase A steht: `CoreBridge.slnx` (SDK 10 erzeugt XML-Solution- statt `.sln`),
  `src/CoreBridge.Core` (`Vm/MultipassCli`, `Vm/Doctor`, `Vm/VmService`), `src/CoreBridge.Cli`
  (Verbs: `multipass-status` (ehem. `doctor`), `vm-list`, `vm-create`, `vm-start`, `vm-stop`, `vm-delete`,
  `vm-exec`, `vm-connect`), `test/CoreBridge.Core.Tests`, `test/Multipass.Experiments`.
- Phase D ist angelaufen: vm-*-Verbs implementiert und end-to-end verifiziert
  (create ~58 s / stop ~4 s / start ~50 s / delete ~1 s), dazu `vm-put`
  (Datei Host→VM). `cb`-Shortcut liegt als PowerShell-Funktion im Profil
  (absoluter Pfad, funktioniert aus jedem Verzeichnis; neues Fenster oder
  `. $PROFILE` nötig).
- Claude Code lief historisch als Native-Binary in einer core26-VM (kein
  npm/curl nötig); das ist keine aktive Classic-Installationsanweisung.
- **.NET SDK 10 auf core26** (historische Core-Evidenz, inkl. `dotnet new/run` + NuGet-Restore):
  `snap install dotnet-sdk-100` (classic Snap — geht auf Core 26 DOCH), Symlink
  nach `~/.local/bin` (/usr/local ist read-only), vendored Runtime-Libs aus
  Ubuntu-debs nach `~/.local/lib` + `LD_LIBRARY_PATH`, `DOTNET_ROOT` in
  `~/.profile`. Reproduzierbar: `scripts/bootstrap-agent-vm.ps1 -VmName <vm>`.
  Lib-Vendoring ist ldd-getrieben (nur wenn `ldd dotnet` „not found“ meldet) und
  läuft komplett auf der Ziel-VM selbst (`apt-get download` ohne root, kein
  Umweg mehr über Build-VM/Host; `BuildVmName`-Parameter entfallen):
  Kandidaten `libunwind8 libicu78 libbrotli1 libgssapi-krb5-2 libssl3t64
  zlib1g` — `libc6`/`libgcc-s1`/`libstdc++6` bewusst NICHT (Basissystem,
  ABI-Risiko), `ca-certificates`/`tzdata` sind keine `.so` (gehören nicht nach
  `~/.local/lib`).
- Custom-Image (T11): Bau komplett lokal bewiesen (`scripts/build-coreimage.ps1`,
  Canonical-dangerous-Modell, kein Ubuntu One). Import nach Multipass unter Windows
  BLOCKIERT (multipass 1.16.3: file://-Client-Mangling + erzwungenes https) —
  Auswege: https-Workaround (Zertifikat) oder Upstream-Bugfix.
- T01–T10 sind implementiert und gelaufen; alle Messwerte in `EXPERIMENT_LOG.md`
  (volle Rohdaten unter `experiment-data/`). Kernaussagen: launch core26 kalt
  88,5 s / warm 58,2 s; exec-Roundtrip ~130 ms mit sauberer Exit-Code-Weitergabe;
  transfer VM→Host liefert korrekten Inhalt trotz Exit-Code 2 (Windows-sftp-Quirk);
  cloud-init `write_files` und `ssh_authorized_keys` funktionieren auf core26
  (natives SSH nach ~10 s als `ubuntu@`); ping durch cap_net_raw blockiert, TCP
  nach draußen geht; stop ~4 s / start ~49 s / delete ~4 s.
- Alle cbx-VMs liegen GELÖSCHT im Multipass-Papierkorb (kein purge, Regel) und
  erscheinen in `list` mit state `Deleted`. Offen: ob `launch` einen Namen aus
  dem Papierkorb übernehmen kann (T03-Rerun würde es klären).
- Multipass 1.16.3 liegt im **User-PATH** (`C:\Program Files\Multipass\bin`), NICHT
  machine-weit → Locator-Stufe 2 (PATH) greift bereits.
- **Custom-Image-Pipeline als CLI-Verbs (25.09.2026):**
  - `cb image-configure --name <n> [-c claude,dotnet,opencode,python] [--model-url <url>] [--runtime-image <image>]`
    lädt/validiert das Classic-Modell
    (`ubuntu-classic-2610-amd64-dangerous.model`), schreibt `<n>.build.json`
    mit Runtime `daily:26.10` und erzeugt cloud-init für Gast-Komponenten.
  - `cb image-build --name <n>` backt weiterhin Store-Snaps in der
    26.04-Bau-VM; der Import des fertigen `.img` unter Windows bleibt offen.
  - Komponentendefinitionen (Pakete, Snap-Name, Cloud-init-Skript,
    Symlinks, `.profile`-Exports, vendored Libs) stehen in
    `src/CoreBridge.Cli/appsettings.json`; neue Komponenten benötigen keinen
    Host-Code.
  - Claude wird als `scripts/components/install-claude.sh` in die generierte
    cloud-init-YAML eingebettet und per `runcmd` im Gast ausgeführt.
  - `bootstrap-agent-vm.ps1` ist kein notwendiger Schritt des öffentlichen
    CLI-first-Workflows.
- **WIP/offene Fäden (25.09.2026):**
  (a) Classic-26.10-Edge: Modell- und Runtime-Inputs können sich bewegen;
  `image-configure` validiert und persistiert den jeweils geladenen
  Modell-Digest. `pinned` versus `managed-stable` für Claude ist noch offen.
  (b) Custom-Image-Import: https-Workaround oder Upstream-Bugfix bleibt offen.
  (c) `opencode`/`python` und der .NET-Snap bleiben als Classic-Gast-
  Provisionierung zu validieren; die alte Core-spezifische Begründung darf
  nicht übertragen werden.
  (d) `cbx-t11-build2` und alte `cbx-*`-VMs können im Papierkorb liegen;
  Purge bleibt tabu.
- **Hyper-V-VM-Gruppe**: `scripts/group-vms.ps1` hält cbx-*-VMs (Multipass
  benennt Hyper-V-VMs wie Instanzen) in einer VMCollection-Gruppe (Default
  „CoreBridge“). Benötigt Hyper-V-Rechte → in elevated PowerShell laufen
  lassen; idempotent, einfach nach neuen cbx-VMs erneut ausführen.
  `-Remove` entfernt die Gruppe (nur Metadaten — multipassd merkt nichts davon,
  VMs selbst bleiben unberührt).
- Neue Experimente (T11+) NUR nach ausdrücklicher Freigabe.

## Soll-Struktur

- `src/CoreBridge.Core` — Logik, kein Konsolen-I/O. Enthält `Vm/MultipassCli`;
  nur dieser Wrapper ruft `Process.Start` für Multipass auf (JSON via
  `System.Text.Json`, Timeout → Kill + Exception).
- `src/CoreBridge.Cli` — Parser + Ausgabe; einziges Projekt mit dem NuGet-Paket
  CommandLineParser 2.9.1.
- `src/CoreBridge.Tui` — erst in Phase D. Terminal.Gui v2 ist eine komplette
  Neuschreibung (v1-Beispiele kompilieren nicht) — dann Patterns aus
  `F:\source\SandboxBridge\src\SandboxBridge.Tui\AGENTS.md` ansehen.
- `test/CoreBridge.Core.Tests` — Unit-Tests; jedes Projekt bekommt ein eigenes
  Testprojekt.
- `test/CoreBridge.Cli.Tests` — Prozessbasierte CLI-/Lifecycle-Tests; der
  öffentliche VM-Lifecycle läuft ausschließlich über diese Verben.
- `test/Multipass.Experiments` — reproduzierbare historische Experimente, keine
  klassischen Tests.
- `net10.0`, SDK via `global.json` gepinnt. Config in CLI und TUI aus derselben
  Quelle: `config.Get<AppConfig>()`.

## Regeln für Experimente

- Jeder Experiment-Test ist einzeln lauffähig und überspringt sich ohne
  Multipass-Installation selbst (xUnit-`MultipassFact`).
- Jeden Lauf protokollieren: Kommando, Exit-Code, Dauer, Roh-Ausgabe →
  `EXPERIMENT_LOG.md`.
- Roh-Ausgaben im Log werden automatisch gekürzt (Kopf/Schwanz je 15 Zeilen, Mitte
  als `... [N Zeilen gekürzt] ...`); die VOLLE Ausgabe landet je Lauf als JSON in
  `experiment-data/` und wird im Log-Eintrag unter „Rohdaten“ verlinkt.
- Testkollektionen im Experiments-Projekt laufen serialisiert (ein `multipassd`
  ist eine geteilte Ressource).
- Gelöschte VMs verschwinden NICHT aus `multipass list`, sondern tragen state
  `Deleted` (Papierkorb) — Existenzchecks müssen den State prüfen.
- Experiment-VMs tragen das Präfix `cbx-`. Gelöscht wird nur nach diesem Präfix —
  `delete --all`/`purge` sind tabu (und laufen sowieso nur über Tests).

## CommandLineParser-Konventionen (Plan §8)

- Eine `[Verb]`-Options-Klasse pro Befehl; `[Option]`-Attribute mit HelpText;
  Kurzformen explizit vergeben und pro Klasse eindeutig.
- **Single-Verb-Falle**: `ParseArguments<T>(args)` mit nur EINEM Verb parst im
  Options-Modus (Verb-Name wird ignoriert, `foo` würde doctor ausführen). Deshalb
  den `Type[]`-Overload nutzen: `ParseArguments(args, new[] { typeof(...) })`.
- `MapResult` mit Hilfe-/Versions-Fehlern → Exit 0 (`HelpRequestedError`,
  `VersionRequestedError`, `HelpVerbRequestedError`); Parse-Fehler → 1. Top-Level
  `--help` exitet im Verb-Modus mit 1 (`NoVerbSelectedError`) — bekanntes
  2.9-Verhalten, nicht „reparieren“.
- Globaler JSON-/Exit-Code-Vertrag ist bewusst OFFEN bis Phase C — kein
  Früh-Commit auf einen Vertrag ohne Nutzer.

## Run & Debug

- CLI: `cb <verb> ...` (Funktion im PowerShell-Profil) oder direkt
  `dotnet run --project src/CoreBridge.Cli -- <verb> ...`. Die `cb`-Funktion
  wird per AfterBuild-Target des Cli-Projekts bei jedem Build idempotent in
  BEIDE PS-Profile installiert (pwsh 7 + Windows PowerShell 5.1); Quelle ist
  `scripts/cb.ps1` — Änderungen an der Funktion NUR dort, nie direkt im Profil.
  Interaktiv (z. B. `cb vm-connect -n x`) nur in echtem Terminal — nicht in
  Agent-Shells. VS-Code-`launch.json` folgt (Plan §7).
- **Build-Lock**: Läuft noch eine CoreBridge.Cli-Instanz (z. B. offenes
  `cb vm-connect`), sperrt sie `CoreBridge.Core.dll` und der Debug-Build schlägt
  fehl (MSB3027). Session beenden oder mit `-c Release` bauen/ausführen.
- **Quoting-Falle bei `vm-exec --command`**: In PowerShell IMMER Single-Quotes um
  den Command-Befehl — Double-Quotes lassen den Host `$HOME`/`$(...)` expandieren
  und schreiben Müll in VM-Dateien. Shell-Variablen innerhalb des Befehls laufen
  über `\$` in sh-Double-Quotes.
- Alle Tests: `dotnet test` · nur Experiments: `dotnet test test/Multipass.Experiments`
- Einzelnes Experiment: `dotnet test test/Multipass.Experiments --filter "FullyQualifiedName~T01"`
- Binary-Override für Tests: Env `CORE_BRIDGE_MULTIPASS_EXE`
  (Locator-Reihenfolge: Env → PATH → `%ProgramFiles%\Multipass\bin\multipass.exe`).

## Ausgabe-Stil

- Antworten und wiedergegebene Kommando-/Datei-Ausgaben knapp halten: lange Blöcke
  auf Kopf+Schwanz kürzen und die Mitte mit `...` andeuten. Gleiches gilt für
  generierte Dateien (Logs etc.).

## Sicherheit

- Keine Passwörter in der Config — Env-Kette `CORE_BRIDGE_[ALL,<NAME>,...]_PASSWORD`.
- Secrets vor jedem Sync scannen; reviewed Merge/Pull-Request ist das einzige
  Einfalltor zurück zum Host.
- Die VM baut NIE selbst eine Verbindung zum Host auf (Isolationsprinzip,
  Git-Topologie „bridge-vermittelt“, Plan §6).
