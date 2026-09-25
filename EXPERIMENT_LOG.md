# EXPERIMENT_LOG — Multipass-Experimente

Jeder Lauf eines Experiments (T01–T10, IMPLEMENTATION_PLAN.md §4) protokolliert
Kommando, Binary, Exit-Code, Dauer und die gekürzte Roh-Ausgabe. Die volle
Roh-Ausgabe liegt je Lauf als JSON in `experiment-data/`. Neue Einträge werden
unten angehängt — nichts wird nachträglich geändert oder sortiert.

---

## T03 — `launch` — kalt (erster Launch des Images, inkl. Download) — 2026-09-26 01:12:07

- Kommando: `multipass launch core26 --name cbx-t03-a`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 57597 ms
- Rohdaten: `experiment-data/T03-20260926-011207-471.json`
- Beobachtung: Dauer: 57597 ms — VMs vor Launch: [cbx-t06, cbx-cli-vm-67884-a58650cd670844]

Roh-Ausgabe:

```
[2K[0A[0E[2K[0A[0ECreating cbx-t03-a  /-\| [2K[0A[0E[2K[0A[0EConfiguring cbx-t03-a  /-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\| [2K[0A[0E[2K[0A[0EStarting cbx-t03-a  /-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\ [2K[0A[0E[2K[0A[0EWaiting for initialization to complete  / [2K[0A[0ELaunched: cbx-t03-a
[2K[0A[0E
```

## T11 — Custom-Image-Launch des lokal gebauten core26-Images — 2026-09-26 01:12:17

- Kommando: `multipass launch <image-url> --name cbx-t11-local`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 0 ms
- Rohdaten: `experiment-data/T11-20260926-011217-790.json`
- Beobachtung: Übersprungen — CORE_BRIDGE_T11_IMAGE_URL nicht gesetzt. Hinweis: Windows-file:// ist in multipass 1.16.3 unbrauchbar (Client mangled Laufwerkspfad, Daemon verwirft host != leer) — Image daher per http:// (z. B. `python -m http.server`) servieren.

Roh-Ausgabe:

```


StdErr:
Keine Image-URL
```

## T10 — `delete` — cbx-t03-b — 2026-09-26 01:12:18

- Kommando: `multipass delete cbx-t03-b`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 2
- Dauer: 126 ms
- Rohdaten: `experiment-data/T10-20260926-011218-074.json`
- Beobachtung: Dauer: 126 ms

Roh-Ausgabe:

```


StdErr:
delete failed: Cannot shut down instance cbx-t03-b while starting.
Use --purge to forcefully delete it.
```

## T09 — `stop` — 2026-09-26 01:12:24

- Kommando: `multipass stop cbx-t03-a`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 4665 ms
- Rohdaten: `experiment-data/T09-20260926-011224-395.json`
- Beobachtung: Dauer: 4665 ms

Roh-Ausgabe:

```
[2K[0A[0EStopping cbx-t03-a  /-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/ [2K[0A[0E
```

## T09 — Zustand nach `stop` — 2026-09-26 01:12:28

- Kommando: `multipass list --format json (Auszug)`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 0 ms
- Rohdaten: `experiment-data/T09-20260926-011228-473.json`
- Beobachtung: State: Stopped

Roh-Ausgabe:

```
state: Stopped
```
