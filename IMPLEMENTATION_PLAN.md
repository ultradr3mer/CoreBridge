# IMPLEMENTATION_PLAN — CoreBridge („Multipass Bridge")

> Plan-Input von opencode (GLM), 16.09.2026, als Antwort auf `PROJECT_MULTIPASS_BRIDGE.md`.
> Parser-Referenz ist `COMMANDLINEPARSER_COMMANDLINE_README.md` (NuGet-Doku des
> CommandLineParser). Inhaltliche Basis: `../PROJECT.md` (SandboxBridge-Spec) —
> aber: **nichts wird von dort übernommen, weil es existiert**; jede Übernahme ist
> eine bewusste Entscheidung nach den Multipass-Experimenten (Leitsatz 2).
> Die eigene Projekt-MD bleibt unangetastet, dieser Plan liegt daneben.

## 0. Festgestellter Stand (16.09.2026, verifiziert)

| Prüfung | Ergebnis |
|---|---|
| Multipass installiert | ja — `1.16.3+win`, unter `C:\Program Files\Multipass\bin\multipass.exe` |
| Multipass im PATH | nein (mindestens nicht machine-weit sichtbar) → `doctor` muss Standardpfade mitprüfen |
| winget | `Canonical.Multipass` (1.16.4) — von Canonical „nicht offiziell unterstützt"; nur Doku-Vormerkung für `doctor` |
| .NET SDK | 10.0.401 → `net10.0` machbar |
| OS | Windows 11 Pro, Hyper-V im Einsatz (Multipass auf Windows baut ebenfalls auf Hyper-V auf) |

## 1. Leitsätze

1. **VERBOT wird Architektur.** Der Agent ruft Multipass nie direkt in der Shell
   auf — auch nicht zum Nachschauen. Einziger Weg: `dotnet test` auf
   `test/Multipass.Experiments` bzw. der `MultipassCli`-Wrapper in
   `CoreBridge.Core/Vm/`. Die Regel wandert ins `AGENTS.md` des neuen Projekts —
   ein Agent, der sie nicht kennt, verstößt sonst zwangsläufig dagegen.
2. **Experiment first.** Die SandboxBridge-Commands sind keine Vorlage. Erst wenn
   ein Experiment etwas belegt hat, wird übernommen — dann als bewusste Entscheidung
   mit Verweis auf den Log-Eintrag.
3. `.NET 10` (`net10.0`), **CommandLineParser 2.9.1** statt handgebautem Argument-Parser.
4. Struktur: `src/CoreBridge.Core` (Logik, kein Konsolen-I/O), `src/CoreBridge.Cli`
   (Parser + Ausgabe), `src/CoreBridge.Tui` (erst später), `test/CoreBridge.Core.Tests`
   plus `test/Multipass.Experiments` („trying things out", aber reproduzierbar).
5. Keine Passwörter in der Config — Env-Kette `CORE_BRIDGE_[ALL,<NAME>,...]_PASSWORD`;
   Secrets vor jedem Sync scannen; reviewter Merge/Pull-Request als einziges Einfalltor zurück.
6. `config.Get<AppConfig>()` in CLI und TUI — dieselbe Config-Quelle.

## 2. Phase A — Skeleton (`F:\source\CoreBridge`, eigenes Git-Repo)

- `dotnet new sln` + Projekte wie in Leitsatz 4; `global.json` pinnt SDK 10
- NuGet: CommandLineParser 2.9.1 nur in `CoreBridge.Cli`
- `AGENTS.md` mit der Verbotsregel (Leitsatz 1), `README.md`, `EXPERIMENT_LOG.md`
- Multipass-Doku lokal: `git clone --filter=blob:none --no-checkout
  https://github.com/canonical/multipass.git` → `refs/multipass`
  (blobless, optional sparse auf `docs/`) als Lesequelle für die Experimente

### `MultipassCli`-Wrapper (Herzstück in `CoreBridge.Core/Vm/`)

- `MultipassBinaryLocator`: 1. Env `CORE_BRIDGE_MULTIPASS_EXE` (Test-/Override-Pfad),
  2. `PATH`, 3. `%ProgramFiles%\Multipass\bin\multipass.exe`
- `MultipassCli.Run(args, timeoutMs)` →
  `MultipassRunResult { ExitCode, StdOut, StdErr, DurationMs }`; Timeout → Kill + Exception
- JSON (`--format json`) via `System.Text.Json`; kein anderer Ort im Code ruft
  `Process.Start` für Multipass auf — nur dieser Wrapper

### CLI v0: `doctor`

- Multipass auffindbar? → Versionen (CLI + Daemon) ausgeben
- Nicht auffindbar → Installationshinweise:
  - Doku: `https://canonical.com/multipass/docs/stable/how-to-guides/install-multipass/#how-to-guides-install-multipass`
  - Direktdownload: `https://canonical.com/multipass/download/windows`
  - winget-Hinweis (`Canonical.Multipass`), inkl. „nicht offiziell unterstützt"
- Exit 0 = ok, 1 = Multipass fehlt. Weitere Commands erst nach Phase C.

## 3. Phase B — Multipass-Installation (erledigt)

Installation ist bereits vorhanden (1.16.3+win). Die in der Projekt-MD notierten
Wege (winget, Direktdownload) fließen nur als Doku-Vormerkung in `doctor` ein.

## 4. Phase C — Experimente (`test/Multipass.Experiments`)

Jeder Experiment-Test ist einzeln lauffähig, überspringt sich selbst ohne
Multipass-Installation (`MultipassFact`) und schreibt jeden Lauf (Kommando, Exit,
Dauer, Roh-Ausgabe) in `EXPERIMENT_LOG.md` — „mal eben ausprobieren" ist damit
nie nötig und nie ein Ergebnis.

| # | Experiment | Misst/klärt | speist Entscheidung |
|---|---|---|---|
| T01 | Installation & `version` | Binary-Locator, Versionen | `doctor` |
| T02 | `find --format json` | Image-Katalog — **klärt, ob es „Ubuntu Core" im Katalog überhaupt gibt** | Image-Wahl |
| T03 | `launch`-Latenz kalt + warm | Dauer VM-Erstellung | Pool-Frage (FRAGE 2) |
| T04 | `exec` | Kommando-Ausführung, stdout, Exit-Code-Weitergabe | Bridge-Kanal |
| T05 | `transfer` | Datei rein/raus | Sync-Weg |
| T06 | `launch --cloud-init` | Provisioning (Dateien, User, Keys), Async-Latenz | Bootstrap-Pfad |
| T07 | Netzwerk | IP, Gateway-Erreichbarkeit, Internet-Zugang der VM | Sicherheitsmodell |
| T08 | SSH-Direktzugriff (eigener Key via cloud-init) | geht natives SSH in die VM? | Bridge-Kanal-Alternativen |
| T09 | Lifecycle `stop`/`start`/`delete` | Dauern + Zustände | Pool, Cleanup |
| T10 | Cleanup | löscht nur `cbx-*`-VMs | Hygiene |

Regeln fürs Aufräumen: nie `multipass delete --all`/`purge` blind — Experiment-VMs
tragen das Präfix `cbx-`, gelöscht wird nur danach. Testkollektionen laufen im
Experiments-Projekt serialisiert (ein `multipassd` ist eine geteilte Ressource).

## 5. Phase D — nach den Experimenten

1. Git-/Sync-Topologie und ggf. Pool festlegen (aus T03/T07/T08)
2. CLI-Ausbau: VMs erstellen / konfigurieren / verbinden — Command-Namen und -Form
   erst dann festlegen
3. Autobridge-Spezifikation (`--directory`, VM-Auswahl, Agent-Wahl [Claude|OpenCode],
   Python|Net)
4. Secret-Scanning vor jedem Sync, Env-Passwortkette (`CORE_BRIDGE_*_PASSWORD`)
5. TUI (`dotnet new tui --WithTests`; Achtung: Terminal.Gui v2 ist eine komplette
   Neuschreibung, v1-Beispiele kompilieren nicht — v2-Patterns bei Start der
   TUI-Phase aus `F:\source\SandboxBridge\src\SandboxBridge.Tui\AGENTS.md` übernehmen,
   falls dann passend)
6. `cb`-Shortcut + Debug-Run (siehe Abschnitt 7)

## 6. FRAGE/KLÄRUNG — Vorschläge (final erst nach Experimenten)

### FRAGE 1: Git pro VM oder Server auf dem Host?

- (a) *VM pullt direkt vom Host* — verworfen: direkter Link Agent↔Host-Repo
  widerspricht dem Isolationsprinzip („Agents nicht direkt mit dem Host-Repo gelinkt").
- (b) *Git-Server auf dem Host* — ein kontrollierter Endpunkt, aber Dauerbetrieb
  einer zusätzlichen Angriffsfläche, bevor der Bedarf belegt ist.
- (c) *Bridge-vermittelt* — **Empfehlung als Startmodell**: pro VM ein lokales
  bare-Repo als `remote` des Workspace-Clones; die Bridge pustet Host→VM hinein
  und holt für den Review VM→Host ab. Die VM baut selbst NIE eine Verbindung zum
  Host auf. Gemeinsame Historie wie in der Projekt-MD („die VM hat ihren clone"),
  reviewter Merge als einziges Einfalltor — ohne direkten Agent-Zugriff auf den Host.
- Experimente T07/T08 liefern die Fakten (welche Kanäle existieren), T03 entscheidet
  mit, ob persistente VM pro Repo oder kurzlebige Task-VMs — davon hängt ab, wo das
  bare-Repo leben muss.

### FRAGE 2: Schnell genug für eine VM pro Repo/Task? Pool nötig?

- T03 misst kalt vs. warm: der erste Launch lädt das Image, danach liegt es im
  Multipass-Cache — die warme Zahl ist die entscheidende.
- Faustregel: warm < ~60 s → per-Task-VM ohne Pool realistisch; deutlich darüber →
  persistente VM pro Repo. Ein Pool ist erst nötig, wenn parallele Tasks gewünscht
  sind — laut Projekt-MD (noch) nicht gefordert.

## 7. `cb`-Shortcut + Debug-Run (analog `sb`, mit `cb` statt `sb`)

```powershell
function cb {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]] $Arguments
    )

    $cliProject = "src/CoreBridge.Cli"

    if (Test-Path $cliProject) {
        dotnet run --project $cliProject -- @Arguments
    }
    else {
        core-bridge @Arguments   # installierte Variante, sobald es sie gibt
    }
}
```

Debug-Run („run, aber mit Debugger"): VS-Code-`launch.json` im neuen Repo, `args`
pro Konfiguration änderbar:

```json
{
  "name": "cb debug",
  "type": "coreclr",
  "request": "launch",
  "program": "${workspaceFolder}/src/CoreBridge.Cli/bin/Debug/net10.0/CoreBridge.Cli.dll",
  "args": [ "doctor" ],
  "cwd": "${workspaceFolder}",
  "console": "integratedTerminal"
}
```

## 8. CommandLineParser — konkrete Nutzung

- Eine `[Verb]`-Options-Klasse pro Befehl (z. B. `[Verb("doctor", HelpText = ...)]`),
  `[Option]`-Attribute mit HelpText; Kurzformen explizit vergeben, pro Klasse eindeutig
- `parser.ParseArguments<T1,...>(args).MapResult(...)` — kein manuelles Arg-Schleifen
- `HelpWriter` + AutoHelp/AutoVersion = `--help`/`--version` gratis; Parse-Fehler
  brechen mit sauberem Hilfetext ab
- Ein globaler JSON-/Exit-Code-Vertrag wird bewusst OFFEN gehalten, bis das echte
  Command-Set nach Phase C steht (kein Früh-Commit auf einen Vertrag ohne Nutzer)

## 9. Offene Punkte / bewusst zurückgestellt

- Ubuntu-Core-Katalogfrage: durch T02 geklärt (16.09.2026) — Core-Images `core` (=16) bis
  `core26` sind im Standard-Katalog (siehe `EXPERIMENT_LOG.md`, T02-Eintrag). Die konkrete
  Wahl der VM-Basis bleibt trotzdem Phase D vorbehalten (Core vs. plain Ubuntu,
  Snap-Härte als spätere Option)
- Autobridge-Details, TUI, Installationsweg für `core-bridge` (PATH/Installation)
- Git-Modell-Feinheiten (Sync-/Merge-Mechanik) — nach Phase-C-Ergebnissen

## 10. Status der Umsetzung (wird laufend aktualisiert)

- [x] Plan hier dokumentiert (16.09.2026)
- [x] Phase A: Skeleton unter `F:\source\CoreBridge` — `CoreBridge.slnx` (SDK-10-Standard,
      XML-Format) mit Core/Cli/Tests/Experiments, `AGENTS.md`, `README.md`,
      `EXPERIMENT_LOG.md`, `global.json` (10.0.401), `refs/multipass` (blobless Clone,
      sparse auf `docs/`); `MultipassCli`-Wrapper (Locator Env → PATH → ProgramFiles,
      Timeout → Kill + Exception) und CLI-Verb `doctor` implementiert (16.09.2026)
- [x] T01/T02 über `dotnet test` gelaufen (read-only, legt keine VM an) — Erkenntnisse
      in `EXPERIMENT_LOG.md`: `version --format json` ist flach (`multipass`/`multipassd`),
      `find --format json` liefert `images` als Objekt (kein Array), Ubuntu Core ist im
      Katalog (core16–core26); Binary liegt im User-PATH
- [x] T03 über `dotnet test` gelaufen (core26, legt `cbx-t03-a`/`cbx-t03-b` an; sie
      bleiben für T09/T10 bestehen) — kalt 88,5 s (inkl. Download), warm 58,2 s;
      Rohdaten in `EXPERIMENT_LOG.md`. Vorläufige Lesart für FRAGE 2: warm liegt
      KNAPP unter der 60-s-Faustregel → Pool-/Persistenz-Frage bleibt Phase-D-Thema
- [x] T04–T10 über `dotnet test` gelaufen (17.09.2026) — Kernergebnisse, Details in
      `EXPERIMENT_LOG.md`:
      - T04: `exec` auf core26 läuft über `/bin/sh`; stdout/stderr getrennt,
        Exit-Code wird 1:1 weitergereicht (42 bestätigt); Roundtrip ~130 ms;
        Default-User `ubuntu`, Kernel 7.0
      - T05: `transfer` Host→VM ok; VM→Host liefert den Inhalt korrekt, kommt aber
        mit Exit-Code 2 zurück (sftp-Chmod-Fehler auf Windows) — Sync darf nie nur
        auf den Exit-Code vertrauen
      - T06: cloud-init `write_files` funktioniert auf core26; Marker schon nach
        ~250 ms sichtbar (kaum Async-Verzögerung nach launch)
      - T07: VM bekommt IPv4 vom Hyper-V-Default-Switch, Gateway 172.30.80.1, DNS
        (systemd-resolved) ok; ICMP/ping blockiert (kein cap_net_raw auf Core),
        TCP nach draußen funktioniert (nc-Check), curl/wget fehlen im Core-Image
      - T08: natives SSH in die VM funktioniert — Key über cloud-init
        (`ssh_authorized_keys`), Erfolg nach ~10 s als `ubuntu@<ip>`
      - T09: stop ~4,0 s, start ~48,6 s, delete ~3,9 s — ein Neustart kostet fast
        so viel wie ein frischer Warm-Launch (58 s) → wichtig für FRAGE 2
      - T10: Cleanup löscht gezielt alle cbx-VMs (kein purge); gelöschte VMs bleiben
        mit state `Deleted` in `list` sichtbar — Existenzchecks müssen den State
        prüfen. Offen: ob ein Launch den Namen einer Papierkorb-VM übernehmen kann
- [ ] Phase D (angelaufen, 17.09.2026):
      - [x] CLI-Ausbau Schritt 1: Verbs `vm-list`, `vm-create` (Default-Image
            core26), `vm-start`, `vm-stop`, `vm-delete`, `vm-exec`,
            `vm-connect` (interaktiv über `multipass shell`). Core-Layer:
            `VmService`, `MultipassVmList`, `VmInfo`, `MultipassCli.Attach`.
            End-to-end verifiziert: create 57,9 s / stop 4,0 s / start 49,6 s /
            delete 1,1 s — deckt sich mit den T09-Messwerten. Damit sind die
            Command-Namen gesetzt (vm-*-Schema, §8-Konventionen).
      - [x] `cb`-Shortcut im PowerShell-Profil installiert (Plan §7; absoluter
            Pfad statt relativ, damit `cb` aus jedem Verzeichnis funktioniert).
      - [ ] Git-/Sync-Topologie, Autobridge, Secret-Scanning, TUI