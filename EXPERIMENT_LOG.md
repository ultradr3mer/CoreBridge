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

## T09 — `start` — 2026-09-26 01:13:18

- Kommando: `multipass start cbx-t03-a`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 49795 ms
- Rohdaten: `experiment-data/T09-20260926-011318-272.json`
- Beobachtung: Dauer: 49795 ms

Roh-Ausgabe:

```
[2K[0A[0EStarting cbx-t03-a  /-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/-\|/- [2K[0A[0E[2K[0A[0E
```

## T09 — Zustand nach `start` — 2026-09-26 01:13:19

- Kommando: `multipass list --format json (Auszug)`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 0 ms
- Rohdaten: `experiment-data/T09-20260926-011319-013.json`
- Beobachtung: State: Running

Roh-Ausgabe:

```
state: Running
```

## T09 — `delete` — in den Papierkorb (kein purge) — 2026-09-26 01:13:20

- Kommando: `multipass delete cbx-t03-a`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 1133 ms
- Rohdaten: `experiment-data/T09-20260926-011320-149.json`
- Beobachtung: Dauer: 1133 ms

Roh-Ausgabe:

```

```

## T09 — Zustand nach `delete` — 2026-09-26 01:13:20

- Kommando: `multipass list --format json (Auszug)`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 0 ms
- Rohdaten: `experiment-data/T09-20260926-011320-297.json`
- Beobachtung: cbx-t03-a im Papierkorb (state Deleted, nicht gepurgt).

Roh-Ausgabe:

```
state: Deleted
```

## T07 — IP aus `list` — 2026-09-26 01:13:20

- Kommando: `multipass list --format json (Auszug)`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 0 ms
- Rohdaten: `experiment-data/T07-20260926-011320-457.json`
- Beobachtung: IPv4: [172.30.84.209], State: Running

Roh-Ausgabe:

```
ipv4: 172.30.84.209; state: Running
```

## T07 — Routing — /proc/net/route — 2026-09-26 01:13:20

- Kommando: `multipass exec cbx-t03-b -- /bin/sh -c cat /proc/net/route`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 129 ms
- Rohdaten: `experiment-data/T07-20260926-011320-588.json`

Roh-Ausgabe:

```
Iface	Destination	Gateway 	Flags	RefCnt	Use	Metric	Mask		MTU	Window	IRTT                                                       
eth0	00000000	01501EAC	0003	0	0	100	00000000	0	0	0                                                                             
eth0	00501EAC	00000000	0001	0	0	100	00F0FFFF	0	0	0                                                                             
eth0	01501EAC	00000000	0005	0	0	100	FFFFFFFF	0	0	0
```

## T07 — DNS — /etc/resolv.conf — 2026-09-26 01:13:20

- Kommando: `multipass exec cbx-t03-b -- /bin/sh -c cat /etc/resolv.conf`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 124 ms
- Rohdaten: `experiment-data/T07-20260926-011320-716.json`

Roh-Ausgabe:

```
# This is /run/systemd/resolve/stub-resolv.conf managed by man:systemd-resolved(8).
# Do not edit.
#
# This file might be symlinked as /etc/resolv.conf. If you're looking at
# /etc/resolv.conf and seeing this text, you have followed the symlink.
#
# This is a dynamic resolv.conf file for connecting local clients to the
# internal DNS stub resolver of systemd-resolved. This file lists all
# configured search domains.
#
# Run "resolvectl status" to see details about the uplink DNS servers
# currently in use.
#
# Third party programs should typically not access this file directly, but only
# through the symlink at /etc/resolv.conf. To manage man:resolv.conf(5) in a
# different way, replace this symlink by a static file or a different symlink.
#
# See man:systemd-resolved.service(8) for details about the supported modes of
# operation for /etc/resolv.conf.

nameserver 127.0.0.53
options edns0 trust-ad
search .
```

## T07 — Verfügbare Netzwerk-Tools — 2026-09-26 01:13:20

- Kommando: `multipass exec cbx-t03-b -- /bin/sh -c for t in ping ping4 curl wget nc; do command -v $t >/dev/null 2>&1 && echo $t=ja || echo $t=nein; done`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 125 ms
- Rohdaten: `experiment-data/T07-20260926-011320-843.json`
- Beobachtung: Gateway: 172.30.80.1 — Dauer: 125 ms

Roh-Ausgabe:

```
ping=ja
ping4=ja
curl=nein
wget=nein
nc=ja
```

## T07 — Gateway-Ping (172.30.80.1) — 2026-09-26 01:13:20

- Kommando: `multipass exec cbx-t03-b -- /bin/sh -c ping -c 2 172.30.80.1`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 2
- Dauer: 126 ms
- Rohdaten: `experiment-data/T07-20260926-011320-971.json`

Roh-Ausgabe:

```


StdErr:
ping: socktype: SOCK_RAW
ping: socket: Operation not permitted
ping: => missing cap_net_raw+p capability or setuid?
```

## T07 — Internet — TCP-Check via nc (example.com:80) — 2026-09-26 01:13:26

- Kommando: `multipass exec cbx-t03-b -- /bin/sh -c nc -w 5 example.com 80 </dev/null >/dev/null 2>&1 && echo tcp-ok || echo tcp-fail`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 5196 ms
- Rohdaten: `experiment-data/T07-20260926-011326-170.json`

Roh-Ausgabe:

```
tcp-ok
```

## T06 — `launch --cloud-init` — write_files — 2026-09-26 01:13:26

- Kommando: `multipass launch --name cbx-t06 --cloud-init <yaml> core26`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 0 ms
- Rohdaten: `experiment-data/T06-20260926-011326-427.json`
- Beobachtung: Launch übersprungen — VM existierte bereits: cbx-t06

Roh-Ausgabe:

```

```

## T05 — `transfer` — Host -> VM — 2026-09-26 01:13:26

- Kommando: `multipass transfer C:\Users\Clara\AppData\Local\Temp\cbx-t05-fa4b93ece21b44f1a91ffcfaade4c460\payload.txt cbx-t03-b:/tmp/cbx-t05-payload.txt`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 170 ms
- Rohdaten: `experiment-data/T05-20260926-011326-757.json`
- Beobachtung: Dauer: 170 ms

Roh-Ausgabe:

```

```

## T05 — `exec cat` — Inhalt in der VM prüfen — 2026-09-26 01:13:26

- Kommando: `multipass exec cbx-t03-b -- /bin/sh -c 'cat /tmp/cbx-t05-payload.txt'`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 146 ms
- Rohdaten: `experiment-data/T05-20260926-011326-906.json`
- Beobachtung: Dauer: 146 ms

Roh-Ausgabe:

```
cbx-t05-payload-ffc714925d524ee39ab932820210bc36
```

## T05 — `transfer` — VM -> Host — 2026-09-26 01:13:27

- Kommando: `multipass transfer cbx-t03-b:/tmp/cbx-t05-payload.txt C:\Users\Clara\AppData\Local\Temp\cbx-t05-fa4b93ece21b44f1a91ffcfaade4c460\payload-out.txt`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 2
- Dauer: 136 ms
- Rohdaten: `experiment-data/T05-20260926-011327-044.json`
- Beobachtung: Dauer: 136 ms

Roh-Ausgabe:

```


StdErr:
[2026-09-26T01:13:27.035] [error] [sftp] cannot set permissions for local file C:\Users\Clara\AppData\Local\Temp\cbx-t05-fa4b93ece21b44f1a91ffcfaade4c460\payload-out.txt
```

## T05 — `transfer` VM -> Host — Bewertung — 2026-09-26 01:13:27

- Kommando: `multipass transfer cbx-t03-b:/tmp/cbx-t05-payload.txt <lokal>`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 2
- Dauer: 136 ms
- Rohdaten: `experiment-data/T05-20260926-011327-046.json`
- Beobachtung: Exit-Code 2 (sftp-Chmod-Fehler auf Windows), Inhalt aber korrekt — Sync-Weg nutzbar.

Roh-Ausgabe:

```
Datei korrekt angekommen

StdErr:
[2026-09-26T01:13:27.035] [error] [sftp] cannot set permissions for local file C:\Users\Clara\AppData\Local\Temp\cbx-t05-fa4b93ece21b44f1a91ffcfaade4c460\payload-out.txt
```

## T05 — `exec rm` — VM-seitig aufräumen — 2026-09-26 01:13:27

- Kommando: `multipass exec cbx-t03-b -- /bin/sh -c 'rm -f /tmp/cbx-t05-payload.txt'`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 208 ms
- Rohdaten: `experiment-data/T05-20260926-011327-256.json`
- Beobachtung: Dauer: 208 ms

Roh-Ausgabe:

```

```

## T04 — `exec` — /bin/sh Verfügbarkeit — 2026-09-26 01:13:27

- Kommando: `multipass exec cbx-t03-a -- /bin/sh -c 'echo shell-ok'`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 2
- Dauer: 122 ms
- Rohdaten: `experiment-data/T04-20260926-011327-524.json`
- Beobachtung: Dauer: 122 ms

Roh-Ausgabe:

```


StdErr:
exec failed: The following errors occurred:
instance "cbx-t03-a" is deleted
```

## T03 — `launch` — kalt (erster Launch des Images, inkl. Download) — 2026-09-26 01:13:27

- Kommando: `multipass launch core26 --name cbx-t03-a`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 2
- Dauer: 66 ms
- Rohdaten: `experiment-data/T03-20260926-011327-745.json`
- Beobachtung: Dauer: 66 ms — VMs vor Launch: [cbx-t03-b, cbx-t06, cbx-cli-vm-56180-52c778c558f74a]

Roh-Ausgabe:

```
[2K[0A[0E[2K[0A[0E

StdErr:
launch failed: instance "cbx-t03-a" already exists
```

## T02 — `find --format json` — Image-Katalog — 2026-09-26 01:13:27

- Kommando: `multipass find --format json`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 98 ms
- Rohdaten: `experiment-data/T02-20260926-011327-848.json`
- Beobachtung: 15 Images, 7 (deprecated) Blueprints. Ubuntu Core IST im Katalog: core, core18, core20, core22, core24, core26.
22.04 (OS: Ubuntu, Release: 22.04 LTS, Aliases: jammy)
24.04 (OS: Ubuntu, Release: 24.04 LTS, Aliases: noble)
26.04 (OS: Ubuntu, Release: 26.04 LTS, Aliases: resolute, lts)
appliance:adguard-home (OS: Ubuntu, Release: AdGuard Home Appliance, Aliases: )
appliance:mosquitto (OS: Ubuntu, Release: Mosquitto Appliance, Aliases: )
appliance:nextcloud (OS: Ubuntu, Release: Nextcloud Appliance, Aliases: )
appliance:openhab (OS: Ubuntu, Release: openHAB Home Appliance, Aliases: )
appliance:plexmediaserver (OS: Ubuntu, Release: Plex Media Server Appliance, Aliases: )
core (OS: Ubuntu, Release: Core 16, Aliases: core16)
core18 (OS: Ubuntu, Release: Core 18, Aliases: )
core20 (OS: Ubuntu, Release: Core 20, Aliases: )
core22 (OS: Ubuntu, Release: Core 22, Aliases: )
core24 (OS: Ubuntu, Release: Core 24, Aliases: )
core26 (OS: Ubuntu, Release: Core 26, Aliases: )
daily:26.10 (OS: Ubuntu, Release: 26.10, Aliases: stonking, devel)

Roh-Ausgabe:

```
{
    "blueprints (deprecated)": {
        "anbox-cloud-appliance": {
            "aliases": [
            ],
            "os": "",
            "release": "Anbox Cloud Appliance",
            "remote": "",
            "version": "latest"
        },
        "charm-dev": {
            "aliases": [
            ],
            "os": "",
            "release": "A development and testing environment for charmers",
... [161 Zeilen gekürzt] ...
            "remote": "",
            "version": "20260506"
        },
        "daily:26.10": {
            "aliases": [
                "stonking",
                "devel"
            ],
            "os": "Ubuntu",
            "release": "26.10",
            "remote": "daily",
            "version": "20260919"
        }
    }
}
```

## T01 — Installation & `version` — 2026-09-26 01:13:27

- Kommando: `multipass version --format json`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 49 ms
- Rohdaten: `experiment-data/T01-20260926-011327-901.json`
- Beobachtung: client=1.16.3+win, daemon=1.16.3+win

Roh-Ausgabe:

```
{
    "multipass": "1.16.3+win",
    "multipassd": "1.16.3+win",
    "update": {
        "description": "Fully open source, custom image launch, GUI improvements, and many fixes.",
        "title": "Multipass 1.16.4 release",
        "url": "https://github.com/canonical/multipass/releases/tag/v1.16.4"
    }
}
```

## T11 — Custom-Image-Launch des lokal gebauten core26-Images — 2026-09-26 04:47:02

- Kommando: `multipass launch <image-url> --name cbx-t11-local`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 0 ms
- Rohdaten: `experiment-data/T11-20260926-044702-934.json`
- Beobachtung: Übersprungen — CORE_BRIDGE_T11_IMAGE_URL nicht gesetzt. Hinweis: Windows-file:// ist in multipass 1.16.3 unbrauchbar (Client mangled Laufwerkspfad, Daemon verwirft host != leer) — Image daher per http:// (z. B. `python -m http.server`) servieren.

Roh-Ausgabe:

```


StdErr:
Keine Image-URL
```

## T10 — Cleanup — löscht nur cbx-* — 2026-09-26 04:47:03

- Kommando: `multipass delete <cbx-*> (keine aktiv vorhanden)`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 0 ms
- Rohdaten: `experiment-data/T10-20260926-044703-052.json`
- Beobachtung: Nichts zu löschen — bereits im Papierkorb: cbx-t03-a, cbx-t03-b, cbx-t06.

Roh-Ausgabe:

```

```

## T09 — Lifecycle — stop/start/delete — 2026-09-26 04:47:03

- Kommando: `multipass stop/start/delete cbx-t03-a`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 0 ms
- Rohdaten: `experiment-data/T09-20260926-044703-136.json`
- Beobachtung: Übersprungen — VM existiert nicht (oder nur im Papierkorb): cbx-t03-a

Roh-Ausgabe:

```

```

## T06 — `launch --cloud-init` — write_files — 2026-09-26 04:47:03

- Kommando: `multipass launch --name cbx-t06 --cloud-init <yaml> core26`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 0 ms
- Rohdaten: `experiment-data/T06-20260926-044703-307.json`
- Beobachtung: Launch übersprungen — VM existierte bereits: cbx-t06

Roh-Ausgabe:

```

```

## T05 — `transfer` — Host -> VM — 2026-09-26 04:47:03

- Kommando: `multipass transfer C:\Users\Clara\AppData\Local\Temp\cbx-t05-ad9ded2851ea4cf38291865f16f92ade\payload.txt cbx-t03-b:/tmp/cbx-t05-payload.txt`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 2
- Dauer: 67 ms
- Rohdaten: `experiment-data/T05-20260926-044703-463.json`
- Beobachtung: Dauer: 67 ms

Roh-Ausgabe:

```


StdErr:
transfer failed: The following errors occurred:
instance "cbx-t03-b" is deleted
```

## T04 — `exec` — /bin/sh Verfügbarkeit — 2026-09-26 04:47:03

- Kommando: `multipass exec cbx-t03-a -- /bin/sh -c 'echo shell-ok'`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 2
- Dauer: 102 ms
- Rohdaten: `experiment-data/T04-20260926-044703-658.json`
- Beobachtung: Dauer: 102 ms

Roh-Ausgabe:

```


StdErr:
exec failed: The following errors occurred:
instance "cbx-t03-a" is deleted
```

## T03 — `launch` — kalt (erster Launch des Images, inkl. Download) — 2026-09-26 04:47:04

- Kommando: `multipass launch core26 --name cbx-t03-a`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 2
- Dauer: 335 ms
- Rohdaten: `experiment-data/T03-20260926-044704-081.json`
- Beobachtung: Dauer: 335 ms — VMs vor Launch: [keine]

Roh-Ausgabe:

```
[2K[0A[0E[2K[0A[0E

StdErr:
launch failed: instance "cbx-t03-a" already exists
```

## T02 — `find --format json` — Image-Katalog — 2026-09-26 04:47:04

- Kommando: `multipass find --format json`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 89 ms
- Rohdaten: `experiment-data/T02-20260926-044704-175.json`
- Beobachtung: 15 Images, 7 (deprecated) Blueprints. Ubuntu Core IST im Katalog: core, core18, core20, core22, core24, core26.
22.04 (OS: Ubuntu, Release: 22.04 LTS, Aliases: jammy)
24.04 (OS: Ubuntu, Release: 24.04 LTS, Aliases: noble)
26.04 (OS: Ubuntu, Release: 26.04 LTS, Aliases: resolute, lts)
appliance:adguard-home (OS: Ubuntu, Release: AdGuard Home Appliance, Aliases: )
appliance:mosquitto (OS: Ubuntu, Release: Mosquitto Appliance, Aliases: )
appliance:nextcloud (OS: Ubuntu, Release: Nextcloud Appliance, Aliases: )
appliance:openhab (OS: Ubuntu, Release: openHAB Home Appliance, Aliases: )
appliance:plexmediaserver (OS: Ubuntu, Release: Plex Media Server Appliance, Aliases: )
core (OS: Ubuntu, Release: Core 16, Aliases: core16)
core18 (OS: Ubuntu, Release: Core 18, Aliases: )
core20 (OS: Ubuntu, Release: Core 20, Aliases: )
core22 (OS: Ubuntu, Release: Core 22, Aliases: )
core24 (OS: Ubuntu, Release: Core 24, Aliases: )
core26 (OS: Ubuntu, Release: Core 26, Aliases: )
daily:26.10 (OS: Ubuntu, Release: 26.10, Aliases: stonking, devel)

Roh-Ausgabe:

```
{
    "blueprints (deprecated)": {
        "anbox-cloud-appliance": {
            "aliases": [
            ],
            "os": "",
            "release": "Anbox Cloud Appliance",
            "remote": "",
            "version": "latest"
        },
        "charm-dev": {
            "aliases": [
            ],
            "os": "",
            "release": "A development and testing environment for charmers",
... [161 Zeilen gekürzt] ...
            "remote": "",
            "version": "20260506"
        },
        "daily:26.10": {
            "aliases": [
                "stonking",
                "devel"
            ],
            "os": "Ubuntu",
            "release": "26.10",
            "remote": "daily",
            "version": "20260919"
        }
    }
}
```

## T01 — Installation & `version` — 2026-09-26 04:47:04

- Kommando: `multipass version --format json`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 48 ms
- Rohdaten: `experiment-data/T01-20260926-044704-227.json`
- Beobachtung: client=1.16.3+win, daemon=1.16.3+win

Roh-Ausgabe:

```
{
    "multipass": "1.16.3+win",
    "multipassd": "1.16.3+win",
    "update": {
        "description": "Fully open source, custom image launch, GUI improvements, and many fixes.",
        "title": "Multipass 1.16.4 release",
        "url": "https://github.com/canonical/multipass/releases/tag/v1.16.4"
    }
}
```

## T11 — Custom-Image-Launch des lokal gebauten core26-Images — 2026-09-26 04:47:15

- Kommando: `multipass launch <image-url> --name cbx-t11-local`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 0 ms
- Rohdaten: `experiment-data/T11-20260926-044715-450.json`
- Beobachtung: Übersprungen — CORE_BRIDGE_T11_IMAGE_URL nicht gesetzt. Hinweis: Windows-file:// ist in multipass 1.16.3 unbrauchbar (Client mangled Laufwerkspfad, Daemon verwirft host != leer) — Image daher per http:// (z. B. `python -m http.server`) servieren.

Roh-Ausgabe:

```


StdErr:
Keine Image-URL
```

## T10 — Cleanup — löscht nur cbx-* — 2026-09-26 04:47:15

- Kommando: `multipass delete <cbx-*> (keine aktiv vorhanden)`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 0 ms
- Rohdaten: `experiment-data/T10-20260926-044715-563.json`
- Beobachtung: Nichts zu löschen — bereits im Papierkorb: cbx-t03-a, cbx-t03-b, cbx-t06.

Roh-Ausgabe:

```

```

## T09 — Lifecycle — stop/start/delete — 2026-09-26 04:47:15

- Kommando: `multipass stop/start/delete cbx-t03-a`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 0 ms
- Rohdaten: `experiment-data/T09-20260926-044715-682.json`
- Beobachtung: Übersprungen — VM existiert nicht (oder nur im Papierkorb): cbx-t03-a

Roh-Ausgabe:

```

```

## T06 — `launch --cloud-init` — write_files — 2026-09-26 04:47:15

- Kommando: `multipass launch --name cbx-t06 --cloud-init <yaml> core26`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 0 ms
- Rohdaten: `experiment-data/T06-20260926-044715-847.json`
- Beobachtung: Launch übersprungen — VM existierte bereits: cbx-t06

Roh-Ausgabe:

```

```

## T05 — `transfer` — Host -> VM — 2026-09-26 04:47:15

- Kommando: `multipass transfer C:\Users\Clara\AppData\Local\Temp\cbx-t05-ddc83f40542f4d31b37ac87840bbd17c\payload.txt cbx-t03-b:/tmp/cbx-t05-payload.txt`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 2
- Dauer: 61 ms
- Rohdaten: `experiment-data/T05-20260926-044715-993.json`
- Beobachtung: Dauer: 61 ms

Roh-Ausgabe:

```


StdErr:
transfer failed: The following errors occurred:
instance "cbx-t03-b" is deleted
```

## T04 — `exec` — /bin/sh Verfügbarkeit — 2026-09-26 04:47:16

- Kommando: `multipass exec cbx-t03-a -- /bin/sh -c 'echo shell-ok'`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 2
- Dauer: 75 ms
- Rohdaten: `experiment-data/T04-20260926-044716-158.json`
- Beobachtung: Dauer: 75 ms

Roh-Ausgabe:

```


StdErr:
exec failed: The following errors occurred:
instance "cbx-t03-a" is deleted
```

## T03 — `launch` — kalt (erster Launch des Images, inkl. Download) — 2026-09-26 04:47:16

- Kommando: `multipass launch core26 --name cbx-t03-a`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 2
- Dauer: 63 ms
- Rohdaten: `experiment-data/T03-20260926-044716-304.json`
- Beobachtung: Dauer: 63 ms — VMs vor Launch: [keine]

Roh-Ausgabe:

```
[2K[0A[0E[2K[0A[0E

StdErr:
launch failed: instance "cbx-t03-a" already exists
```

## T02 — `find --format json` — Image-Katalog — 2026-09-26 04:47:16

- Kommando: `multipass find --format json`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 100 ms
- Rohdaten: `experiment-data/T02-20260926-044716-408.json`
- Beobachtung: 15 Images, 7 (deprecated) Blueprints. Ubuntu Core IST im Katalog: core, core18, core20, core22, core24, core26.
22.04 (OS: Ubuntu, Release: 22.04 LTS, Aliases: jammy)
24.04 (OS: Ubuntu, Release: 24.04 LTS, Aliases: noble)
26.04 (OS: Ubuntu, Release: 26.04 LTS, Aliases: resolute, lts)
appliance:adguard-home (OS: Ubuntu, Release: AdGuard Home Appliance, Aliases: )
appliance:mosquitto (OS: Ubuntu, Release: Mosquitto Appliance, Aliases: )
appliance:nextcloud (OS: Ubuntu, Release: Nextcloud Appliance, Aliases: )
appliance:openhab (OS: Ubuntu, Release: openHAB Home Appliance, Aliases: )
appliance:plexmediaserver (OS: Ubuntu, Release: Plex Media Server Appliance, Aliases: )
core (OS: Ubuntu, Release: Core 16, Aliases: core16)
core18 (OS: Ubuntu, Release: Core 18, Aliases: )
core20 (OS: Ubuntu, Release: Core 20, Aliases: )
core22 (OS: Ubuntu, Release: Core 22, Aliases: )
core24 (OS: Ubuntu, Release: Core 24, Aliases: )
core26 (OS: Ubuntu, Release: Core 26, Aliases: )
daily:26.10 (OS: Ubuntu, Release: 26.10, Aliases: stonking, devel)

Roh-Ausgabe:

```
{
    "blueprints (deprecated)": {
        "anbox-cloud-appliance": {
            "aliases": [
            ],
            "os": "",
            "release": "Anbox Cloud Appliance",
            "remote": "",
            "version": "latest"
        },
        "charm-dev": {
            "aliases": [
            ],
            "os": "",
            "release": "A development and testing environment for charmers",
... [161 Zeilen gekürzt] ...
            "remote": "",
            "version": "20260506"
        },
        "daily:26.10": {
            "aliases": [
                "stonking",
                "devel"
            ],
            "os": "Ubuntu",
            "release": "26.10",
            "remote": "daily",
            "version": "20260919"
        }
    }
}
```

## T01 — Installation & `version` — 2026-09-26 04:47:16

- Kommando: `multipass version --format json`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 56 ms
- Rohdaten: `experiment-data/T01-20260926-044716-469.json`
- Beobachtung: client=1.16.3+win, daemon=1.16.3+win

Roh-Ausgabe:

```
{
    "multipass": "1.16.3+win",
    "multipassd": "1.16.3+win",
    "update": {
        "description": "Fully open source, custom image launch, GUI improvements, and many fixes.",
        "title": "Multipass 1.16.4 release",
        "url": "https://github.com/canonical/multipass/releases/tag/v1.16.4"
    }
}
```

## T11 — Custom-Image-Launch des lokal gebauten core26-Images — 2026-09-26 04:47:30

- Kommando: `multipass launch <image-url> --name cbx-t11-local`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 0 ms
- Rohdaten: `experiment-data/T11-20260926-044730-406.json`
- Beobachtung: Übersprungen — CORE_BRIDGE_T11_IMAGE_URL nicht gesetzt. Hinweis: Windows-file:// ist in multipass 1.16.3 unbrauchbar (Client mangled Laufwerkspfad, Daemon verwirft host != leer) — Image daher per http:// (z. B. `python -m http.server`) servieren.

Roh-Ausgabe:

```


StdErr:
Keine Image-URL
```

## T10 — Cleanup — löscht nur cbx-* — 2026-09-26 04:47:30

- Kommando: `multipass delete <cbx-*> (keine aktiv vorhanden)`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 0 ms
- Rohdaten: `experiment-data/T10-20260926-044730-514.json`
- Beobachtung: Nichts zu löschen — bereits im Papierkorb: cbx-t03-a, cbx-t03-b, cbx-t06.

Roh-Ausgabe:

```

```

## T09 — Lifecycle — stop/start/delete — 2026-09-26 04:47:30

- Kommando: `multipass stop/start/delete cbx-t03-a`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 0 ms
- Rohdaten: `experiment-data/T09-20260926-044730-594.json`
- Beobachtung: Übersprungen — VM existiert nicht (oder nur im Papierkorb): cbx-t03-a

Roh-Ausgabe:

```

```

## T06 — `launch --cloud-init` — write_files — 2026-09-26 04:47:30

- Kommando: `multipass launch --name cbx-t06 --cloud-init <yaml> core26`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 0 ms
- Rohdaten: `experiment-data/T06-20260926-044730-764.json`
- Beobachtung: Launch übersprungen — VM existierte bereits: cbx-t06

Roh-Ausgabe:

```

```

## T05 — `transfer` — Host -> VM — 2026-09-26 04:47:30

- Kommando: `multipass transfer C:\Users\Clara\AppData\Local\Temp\cbx-t05-7cd96a1287934b57a430b3d4fa8d02fd\payload.txt cbx-t03-b:/tmp/cbx-t05-payload.txt`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 2
- Dauer: 64 ms
- Rohdaten: `experiment-data/T05-20260926-044730-924.json`
- Beobachtung: Dauer: 64 ms

Roh-Ausgabe:

```


StdErr:
transfer failed: The following errors occurred:
instance "cbx-t03-b" is deleted
```

## T04 — `exec` — /bin/sh Verfügbarkeit — 2026-09-26 04:47:31

- Kommando: `multipass exec cbx-t03-a -- /bin/sh -c 'echo shell-ok'`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 2
- Dauer: 75 ms
- Rohdaten: `experiment-data/T04-20260926-044731-098.json`
- Beobachtung: Dauer: 75 ms

Roh-Ausgabe:

```


StdErr:
exec failed: The following errors occurred:
instance "cbx-t03-a" is deleted
```

## T03 — `launch` — kalt (erster Launch des Images, inkl. Download) — 2026-09-26 04:47:31

- Kommando: `multipass launch core26 --name cbx-t03-a`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 2
- Dauer: 59 ms
- Rohdaten: `experiment-data/T03-20260926-044731-238.json`
- Beobachtung: Dauer: 59 ms — VMs vor Launch: [keine]

Roh-Ausgabe:

```
[2K[0A[0E[2K[0A[0E

StdErr:
launch failed: instance "cbx-t03-a" already exists
```

## T02 — `find --format json` — Image-Katalog — 2026-09-26 04:47:31

- Kommando: `multipass find --format json`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 89 ms
- Rohdaten: `experiment-data/T02-20260926-044731-332.json`
- Beobachtung: 15 Images, 7 (deprecated) Blueprints. Ubuntu Core IST im Katalog: core, core18, core20, core22, core24, core26.
22.04 (OS: Ubuntu, Release: 22.04 LTS, Aliases: jammy)
24.04 (OS: Ubuntu, Release: 24.04 LTS, Aliases: noble)
26.04 (OS: Ubuntu, Release: 26.04 LTS, Aliases: resolute, lts)
appliance:adguard-home (OS: Ubuntu, Release: AdGuard Home Appliance, Aliases: )
appliance:mosquitto (OS: Ubuntu, Release: Mosquitto Appliance, Aliases: )
appliance:nextcloud (OS: Ubuntu, Release: Nextcloud Appliance, Aliases: )
appliance:openhab (OS: Ubuntu, Release: openHAB Home Appliance, Aliases: )
appliance:plexmediaserver (OS: Ubuntu, Release: Plex Media Server Appliance, Aliases: )
core (OS: Ubuntu, Release: Core 16, Aliases: core16)
core18 (OS: Ubuntu, Release: Core 18, Aliases: )
core20 (OS: Ubuntu, Release: Core 20, Aliases: )
core22 (OS: Ubuntu, Release: Core 22, Aliases: )
core24 (OS: Ubuntu, Release: Core 24, Aliases: )
core26 (OS: Ubuntu, Release: Core 26, Aliases: )
daily:26.10 (OS: Ubuntu, Release: 26.10, Aliases: stonking, devel)

Roh-Ausgabe:

```
{
    "blueprints (deprecated)": {
        "anbox-cloud-appliance": {
            "aliases": [
            ],
            "os": "",
            "release": "Anbox Cloud Appliance",
            "remote": "",
            "version": "latest"
        },
        "charm-dev": {
            "aliases": [
            ],
            "os": "",
            "release": "A development and testing environment for charmers",
... [161 Zeilen gekürzt] ...
            "remote": "",
            "version": "20260506"
        },
        "daily:26.10": {
            "aliases": [
                "stonking",
                "devel"
            ],
            "os": "Ubuntu",
            "release": "26.10",
            "remote": "daily",
            "version": "20260919"
        }
    }
}
```

## T01 — Installation & `version` — 2026-09-26 04:47:31

- Kommando: `multipass version --format json`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 47 ms
- Rohdaten: `experiment-data/T01-20260926-044731-383.json`
- Beobachtung: client=1.16.3+win, daemon=1.16.3+win

Roh-Ausgabe:

```
{
    "multipass": "1.16.3+win",
    "multipassd": "1.16.3+win",
    "update": {
        "description": "Fully open source, custom image launch, GUI improvements, and many fixes.",
        "title": "Multipass 1.16.4 release",
        "url": "https://github.com/canonical/multipass/releases/tag/v1.16.4"
    }
}
```
