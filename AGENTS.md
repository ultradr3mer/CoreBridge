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

## Aktueller Stand (16.09.2026) — Plan §10 laufend gegen den Repo-Zustand prüfen

- Phase A steht: `CoreBridge.slnx` (SDK 10 erzeugt XML-Solution- statt `.sln`),
  `src/CoreBridge.Core` (`Vm/MultipassCli`, `Vm/Doctor`), `src/CoreBridge.Cli`
  (einziges Verb: `doctor`), `test/CoreBridge.Core.Tests`, `test/Multipass.Experiments`
  (T01/T02 implementiert).
- T01/T02 liefen read-only; Erkenntnisse in `EXPERIMENT_LOG.md`: `version --format json`
  ist flach (`multipass`/`multipassd`), `find --format json` liefert `images` als
  Objekt (kein Array), Ubuntu Core ist im Katalog (core16–core26).
- Multipass 1.16.3 liegt im **User-PATH** (`C:\Program Files\Multipass\bin`), NICHT
  machine-weit → Locator-Stufe 2 (PATH) greift bereits.
- T03–T09 sind noch NICHT implementiert — NUR nach ausdrücklicher Freigabe
  schreiben/laufen lassen: sie legen `cbx-*`-VMs an, erster Launch lädt ein Image in
  GB-Größenordnung.

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
- `test/Multipass.Experiments` — reproduzierbare Experimente, keine klassischen
  Tests.
- `net10.0`, SDK via `global.json` gepinnt. Config in CLI und TUI aus derselben
  Quelle: `config.Get<AppConfig>()`.

## Regeln für Experimente

- Jeder Experiment-Test ist einzeln lauffähig und überspringt sich ohne
  Multipass-Installation selbst (xUnit-`MultipassFact`).
- Jeden Lauf protokollieren: Kommando, Exit-Code, Dauer, Roh-Ausgabe →
  `EXPERIMENT_LOG.md`.
- Testkollektionen im Experiments-Projekt laufen serialisiert (ein `multipassd`
  ist eine geteilte Ressource).
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

- CLI lokal: `dotnet run --project src/CoreBridge.Cli -- doctor`
  (später `cb`-PowerShell-Funktion und VS-Code-`launch.json`, Plan §7).
- Alle Tests: `dotnet test` · nur Experiments: `dotnet test test/Multipass.Experiments`
- Einzelnes Experiment: `dotnet test test/Multipass.Experiments --filter "FullyQualifiedName~T01"`
- Binary-Override für Tests: Env `CORE_BRIDGE_MULTIPASS_EXE`
  (Locator-Reihenfolge: Env → PATH → `%ProgramFiles%\Multipass\bin\multipass.exe`).

## Sicherheit

- Keine Passwörter in der Config — Env-Kette `CORE_BRIDGE_[ALL,<NAME>,...]_PASSWORD`.
- Secrets vor jedem Sync scannen; reviewed Merge/Pull-Request ist das einzige
  Einfalltor zurück zum Host.
- Die VM baut NIE selbst eine Verbindung zum Host auf (Isolationsprinzip,
  Git-Topologie „bridge-vermittelt“, Plan §6).
