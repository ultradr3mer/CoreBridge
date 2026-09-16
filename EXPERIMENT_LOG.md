# EXPERIMENT_LOG — Multipass-Experimente

Jeder Lauf eines Experiments (T01–T10, IMPLEMENTATION_PLAN.md §4) protokolliert
Kommando, Binary, Exit-Code, Dauer und Roh-Ausgabe. Neue Einträge werden unten
angehängt — nichts wird nachträglich geändert oder sortiert.

---

## T01 — Installation & `version` — 2026-09-16 23:58:40

- Kommando: `multipass version --format json`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 52 ms
- Beobachtung: client=unbekannt, daemon=keine Antwort

Roh-Ausgabe:

```
{
    "multipass": "1.16.3+win",
    "multipassd": "1.16.3+win"
}
```

## T01 — Installation & `version` — 2026-09-16 23:59:38

- Kommando: `multipass version --format json`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 50 ms
- Beobachtung: client=1.16.3+win, daemon=1.16.3+win

Roh-Ausgabe:

```
{
    "multipass": "1.16.3+win",
    "multipassd": "1.16.3+win"
}
```

## T02 — `find --format json` — Image-Katalog — 2026-09-16 23:59:39

- Kommando: `multipass find --format json`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 338 ms
- Beobachtung: Katalog-JSON ohne info.images[] — Struktur in der Roh-Ausgabe prüfen.

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
            "remote": "",
            "version": "latest"
        },
        "docker": {
            "aliases": [
            ],
            "os": "",
            "release": "A Docker environment with Portainer and related tools",
            "remote": "",
            "version": "0.4"
        },
        "jellyfin": {
            "aliases": [
            ],
            "os": "",
            "release": "Jellyfin is a Free Software Media System that puts you in control of managing and streaming your media.",
            "remote": "",
            "version": "latest"
        },
        "minikube": {
            "aliases": [
            ],
            "os": "",
            "release": "minikube is local Kubernetes",
            "remote": "",
            "version": "latest"
        },
        "ros2-humble": {
            "aliases": [
            ],
            "os": "",
            "release": "A development and testing environment for ROS 2 Humble.",
            "remote": "",
            "version": "0.1"
        },
        "ros2-jazzy": {
            "aliases": [
            ],
            "os": "",
            "release": "A development and testing environment for ROS 2 Jazzy.",
            "remote": "",
            "version": "0.1"
        }
    },
    "errors": [
    ],
    "images": {
        "22.04": {
            "aliases": [
                "jammy"
            ],
            "os": "Ubuntu",
            "release": "22.04 LTS",
            "remote": "",
            "version": "20260913"
        },
        "24.04": {
            "aliases": [
                "noble"
            ],
            "os": "Ubuntu",
            "release": "24.04 LTS",
            "remote": "",
            "version": "20260911"
        },
        "26.04": {
            "aliases": [
                "resolute",
                "lts"
            ],
            "os": "Ubuntu",
            "release": "26.04 LTS",
            "remote": "",
            "version": "20260823"
        },
        "appliance:adguard-home": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "AdGuard Home Appliance",
            "remote": "appliance",
            "version": "20200812"
        },
        "appliance:mosquitto": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Mosquitto Appliance",
            "remote": "appliance",
            "version": "20200812"
        },
        "appliance:nextcloud": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Nextcloud Appliance",
            "remote": "appliance",
            "version": "20200812"
        },
        "appliance:openhab": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "openHAB Home Appliance",
            "remote": "appliance",
            "version": "20200812"
        },
        "appliance:plexmediaserver": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Plex Media Server Appliance",
            "remote": "appliance",
            "version": "20200812"
        },
        "core": {
            "aliases": [
                "core16"
            ],
            "os": "Ubuntu",
            "release": "Core 16",
            "remote": "",
            "version": "20200818"
        },
        "core18": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Core 18",
            "remote": "",
            "version": "20211124"
        },
        "core20": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Core 20",
            "remote": "",
            "version": "20230119"
        },
        "core22": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Core 22",
            "remote": "",
            "version": "20230717"
        },
        "core24": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Core 24",
            "remote": "",
            "version": "20240603"
        },
        "core26": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Core 26",
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
            "version": "20260911"
        }
    }
}
```

## T02 — `find --format json` — Image-Katalog — 2026-09-16 23:59:58

- Kommando: `multipass find --format json`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 89 ms
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
            "remote": "",
            "version": "latest"
        },
        "docker": {
            "aliases": [
            ],
            "os": "",
            "release": "A Docker environment with Portainer and related tools",
            "remote": "",
            "version": "0.4"
        },
        "jellyfin": {
            "aliases": [
            ],
            "os": "",
            "release": "Jellyfin is a Free Software Media System that puts you in control of managing and streaming your media.",
            "remote": "",
            "version": "latest"
        },
        "minikube": {
            "aliases": [
            ],
            "os": "",
            "release": "minikube is local Kubernetes",
            "remote": "",
            "version": "latest"
        },
        "ros2-humble": {
            "aliases": [
            ],
            "os": "",
            "release": "A development and testing environment for ROS 2 Humble.",
            "remote": "",
            "version": "0.1"
        },
        "ros2-jazzy": {
            "aliases": [
            ],
            "os": "",
            "release": "A development and testing environment for ROS 2 Jazzy.",
            "remote": "",
            "version": "0.1"
        }
    },
    "errors": [
    ],
    "images": {
        "22.04": {
            "aliases": [
                "jammy"
            ],
            "os": "Ubuntu",
            "release": "22.04 LTS",
            "remote": "",
            "version": "20260913"
        },
        "24.04": {
            "aliases": [
                "noble"
            ],
            "os": "Ubuntu",
            "release": "24.04 LTS",
            "remote": "",
            "version": "20260911"
        },
        "26.04": {
            "aliases": [
                "resolute",
                "lts"
            ],
            "os": "Ubuntu",
            "release": "26.04 LTS",
            "remote": "",
            "version": "20260823"
        },
        "appliance:adguard-home": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "AdGuard Home Appliance",
            "remote": "appliance",
            "version": "20200812"
        },
        "appliance:mosquitto": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Mosquitto Appliance",
            "remote": "appliance",
            "version": "20200812"
        },
        "appliance:nextcloud": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Nextcloud Appliance",
            "remote": "appliance",
            "version": "20200812"
        },
        "appliance:openhab": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "openHAB Home Appliance",
            "remote": "appliance",
            "version": "20200812"
        },
        "appliance:plexmediaserver": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Plex Media Server Appliance",
            "remote": "appliance",
            "version": "20200812"
        },
        "core": {
            "aliases": [
                "core16"
            ],
            "os": "Ubuntu",
            "release": "Core 16",
            "remote": "",
            "version": "20200818"
        },
        "core18": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Core 18",
            "remote": "",
            "version": "20211124"
        },
        "core20": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Core 20",
            "remote": "",
            "version": "20230119"
        },
        "core22": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Core 22",
            "remote": "",
            "version": "20230717"
        },
        "core24": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Core 24",
            "remote": "",
            "version": "20240603"
        },
        "core26": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Core 26",
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
            "version": "20260911"
        }
    }
}
```

## T02 — `find --format json` — Image-Katalog — 2026-09-17 00:05:57

- Kommando: `multipass find --format json`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 346 ms
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
            "remote": "",
            "version": "latest"
        },
        "docker": {
            "aliases": [
            ],
            "os": "",
            "release": "A Docker environment with Portainer and related tools",
            "remote": "",
            "version": "0.4"
        },
        "jellyfin": {
            "aliases": [
            ],
            "os": "",
            "release": "Jellyfin is a Free Software Media System that puts you in control of managing and streaming your media.",
            "remote": "",
            "version": "latest"
        },
        "minikube": {
            "aliases": [
            ],
            "os": "",
            "release": "minikube is local Kubernetes",
            "remote": "",
            "version": "latest"
        },
        "ros2-humble": {
            "aliases": [
            ],
            "os": "",
            "release": "A development and testing environment for ROS 2 Humble.",
            "remote": "",
            "version": "0.1"
        },
        "ros2-jazzy": {
            "aliases": [
            ],
            "os": "",
            "release": "A development and testing environment for ROS 2 Jazzy.",
            "remote": "",
            "version": "0.1"
        }
    },
    "errors": [
    ],
    "images": {
        "22.04": {
            "aliases": [
                "jammy"
            ],
            "os": "Ubuntu",
            "release": "22.04 LTS",
            "remote": "",
            "version": "20260913"
        },
        "24.04": {
            "aliases": [
                "noble"
            ],
            "os": "Ubuntu",
            "release": "24.04 LTS",
            "remote": "",
            "version": "20260911"
        },
        "26.04": {
            "aliases": [
                "resolute",
                "lts"
            ],
            "os": "Ubuntu",
            "release": "26.04 LTS",
            "remote": "",
            "version": "20260823"
        },
        "appliance:adguard-home": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "AdGuard Home Appliance",
            "remote": "appliance",
            "version": "20200812"
        },
        "appliance:mosquitto": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Mosquitto Appliance",
            "remote": "appliance",
            "version": "20200812"
        },
        "appliance:nextcloud": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Nextcloud Appliance",
            "remote": "appliance",
            "version": "20200812"
        },
        "appliance:openhab": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "openHAB Home Appliance",
            "remote": "appliance",
            "version": "20200812"
        },
        "appliance:plexmediaserver": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Plex Media Server Appliance",
            "remote": "appliance",
            "version": "20200812"
        },
        "core": {
            "aliases": [
                "core16"
            ],
            "os": "Ubuntu",
            "release": "Core 16",
            "remote": "",
            "version": "20200818"
        },
        "core18": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Core 18",
            "remote": "",
            "version": "20211124"
        },
        "core20": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Core 20",
            "remote": "",
            "version": "20230119"
        },
        "core22": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Core 22",
            "remote": "",
            "version": "20230717"
        },
        "core24": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Core 24",
            "remote": "",
            "version": "20240603"
        },
        "core26": {
            "aliases": [
            ],
            "os": "Ubuntu",
            "release": "Core 26",
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
            "version": "20260911"
        }
    }
}
```

## T01 — Installation & `version` — 2026-09-17 00:05:57

- Kommando: `multipass version --format json`
- Binary: `C:\Program Files\Multipass\bin\multipass.exe`
- Exit-Code: 0
- Dauer: 50 ms
- Beobachtung: client=1.16.3+win, daemon=1.16.3+win

Roh-Ausgabe:

```
{
    "multipass": "1.16.3+win",
    "multipassd": "1.16.3+win"
}
```
