# CLOUD-INIT — Provisioning und Claude-Code-Installation

> Recherche-/Implementierungsstand, 25.09.2026. Die lokale Cloud-init-Checkout,
> die aktuelle CoreBridge-Implementierung und die offiziellen Installations-
> und Classic-Ubuntu-Dokumentationen wurden ausgewertet. Die Zielentscheidung
> ist nun Classic Ubuntu 26.10 Edge.

## 1. Kurzergebnis

Die aktive Zielbasis ist **klassisches Ubuntu 26.10 Edge**:

- Modell: `ubuntu-classic-2610-amd64-dangerous.model`
- Multipass-Runtime: `daily:26.10`
- `26.04` bleibt nur das Werkzeug-Image der Image-Bau-VM.
- Claude wird nicht über den Core-only `claude-code`-Snap provisioniert.
  `scripts/components/install-claude.sh` ist eine Gast-Datei, die in die
  generierte cloud-init-YAML eingebettet und dort per `runcmd` ausgeführt wird.

Die Provisionierung ist bewusst getrennt:

1. `image-configure` lädt/validiert das Classic-Modell, schreibt Profil,
   Runtime und die Cloud-init-Metadaten.
2. `vm-create --image <profil>` startet die gespeicherte Runtime
   (`daily:26.10`) und übergibt die generierte Cloud-init.
3. Cloud-init installiert Pakete, schreibt das Claude-Skript als Datei und
   führt es im Gast per `runcmd` aus.
4. Ein erfolgreicher Launch allein gilt nicht als erfolgreiche
   Agent-Provisionierung; der Postcondition-Befehl muss explizit geprüft
   werden.

Cloud-init bleibt für Gast-Komponenten zuständig (Snaps, Symlinks,
`.profile`-Exports und optionale Bibliotheken). Ein großes Binary wird nicht
in YAML eingebettet.

## 2. Vorhandener Claude-Code-Pfad

### 2.1 Konfiguration und Ausführung sind getrennt

Die aktuelle Komponentenddefinition ist:

```json
"claude": {
  "CloudInitScript": "scripts/components/install-claude.sh",
  "CloudInitInstallPath": "/usr/local/sbin/corebridge-install-claude",
  "CloudInitPackages": ["bash", "curl", "ca-certificates"]
}
```

Quelle: `src/CoreBridge.Cli/appsettings.json`.

`ImageCloudInit` liest die Skriptquelle, schreibt sie mit `write_files` als
`/usr/local/sbin/corebridge-install-claude` und ruft sie über `runcmd` auf.
Der Host führt das Skript nicht aus; es gibt im aktuellen Classic-Workflow
keine Host-Installationsphase.

### 2.2 Aktueller Ablauf des eingebetteten Skripts

Das Skript führt im Gast aus:

1. `curl -fsSL https://claude.ai/install.sh | bash -s stable` als Benutzer
   `ubuntu` ausführen.
2. `claude --version` als Postcondition prüfen.
3. `/var/lib/corebridge/claude.ready` als Provisioning-Marker anlegen.

Pakete werden vorher über das `packages`-Modul von cloud-init installiert.

### 2.3 Schwächen des aktuellen Ablaufs

#### Nicht authentifizierte Manifestquelle

Ein SHA-256 aus einem unmittelbar vom selben Download-Host geladenen Manifest
schützt vor Beschädigung, aber nicht vor einer Kompromittierung dieses Hosts.
Anthropic dokumentiert inzwischen:

- `manifest.json.sig`,
- den festen Signing-Key,
- Fingerabdruck
  `31DD DE24 DDFA B679 F42D 7BD2 BAA9 29FF 1A7ECACE`.

Detached Manifest-Signaturen sind laut Anthropic erst ab Release `2.1.89`
verfügbar. Für einen `pinned` Vertrag sollte CoreBridge genau solche Releases
verlangen, statt die fehlende Signatur als Fallback zu akzeptieren.

Die Signatur des Manifests muss geprüft werden; erst danach ist dessen
Binary-Digest ein Vertrauensanker.

#### Nicht reproduzierbar

`latest` wird bei jedem Lauf neu aufgelöst. Selbst wenn CoreBridge heute eine
konkrete Version installiert, ist die dadurch erzeugte VM nicht dauerhaft auf
diese Version festgelegt.

Ein reproduzierbarer Modus benötigt:

- eine konkrete Version,
- den erwarteten Binary-Digest,
- `DISABLE_UPDATES=1`, wenn auch `claude install` und `claude update`
  ausgeschlossen werden sollen.

`DISABLE_AUTOUPDATER=1` allein blockiert nur Hintergrundupdates.
`DISABLE_UPDATES=1` blockiert zusätzlich manuelle Update-Pfade. Diese Variable
sollte in einer reproduzierbaren VM erst nach der erfolgreichen Erstinstallation
für den verwalteten Runtime-Kontext gesetzt werden; den Bootstrap-Aufruf selbst
nicht ungetestet mit einer Update-Sperre umgeben.

#### Schwache Idempotenzprüfung

- Die Versionsprüfung verwendet Substring-Matching. Eine angeforderte Version
  `2.1.28` könnte daher in `2.1.280` enthalten sein.
- Der Host-Cache hat keinen Versionsbestandteil und ist bei parallelen Läufen
  angreifbar.
- Es wird direkt auf den kanonischen Launcher-Pfad geschrieben.
- Bei einem Fehler nach dem Transfer gibt es keinen Rollback.
- Weder Launcher noch Version-Store werden nach der Installation geprüft.
- Der Test deckt nur eine frische VM ab, nicht Wiederholung, Upgrade,
  unterbrochene Übertragung oder Autoupdate-Policy.

### 2.4 Native Claude-Installation ist bereits versionsverwaltet

Anthropics aktuelle Installationsdokumentation beschreibt den nativen Linux-
Pfad selbst als Paketverwaltung im Benutzerverzeichnis:

- `~/.local/bin/claude` ist ein Symlink.
- Die eigentlichen Versionen liegen unter
  `~/.local/share/claude/versions/`.
- Native Installationen aktualisieren sich standardmäßig selbst.
- `claude update` erzwingt eine Aktualisierung.
- `claude doctor` zeigt Installations- und Update-Zustand.

Deshalb wäre ein zusätzliches CoreBridge-Paket redundant. Es würde lediglich eine
zweite Updateautorität neben Claudes eigenem Versionsmanager schaffen.

### 2.5 Was das offizielle Installerscript bestätigt

Der aktuelle offizielle Installer `https://claude.ai/install.sh` lädt zunächst
das Binary der neuesten Releaseversion als Bootstrap und ruft dann auf:

```sh
"$binary_path" install "${TARGET:-latest}"
```

`TARGET` akzeptiert `latest`, `stable` oder eine exakte Version. Das bestätigt
den empfohlenen Vertrag:

- **exakt:** `install <x.y.z>`,
- **managed:** `install stable`,
- **floating:** `install latest`.

Das Bootstrap-Binary wird nach erfolgreicher Installation wieder gelöscht; der
native Versionsstore und Launcher bleiben erhalten. Auf Classic Ubuntu 26.10
stehen `curl` und `bash` nach dem `packages`-Schritt zur Verfügung. Das
eingebettete Skript ruft deshalb den offiziellen Shell-Installer direkt mit
`stable` auf:

```sh
sudo -u ubuntu -H bash -lc \
  'curl -fsSL https://claude.ai/install.sh | bash -s stable'
```

Das ist die aktive Classic-Implementierung. Eine Core-spezifische
Host-Transfer-Pipeline ist nicht mehr Teil des öffentlichen Workflows.

### 2.6 Tests und Bildkonfiguration

Aktuell muss die Test- und Konfigurationslage mitgeplant werden:

- `ImageTests` prüft nur per `Assert.Contains`, ob der erzeugte Text bestimmte
  Strings enthält. Das beweist weder valides YAML noch ein gültiges
  cloud-init-Schema.
- `CoreBridge.Cli.Tests` führt den öffentlichen CLI-Lifecycle aus; der frühere
  direkte `VmCycleTests`-Servicefluss ist entfernt.
- Der Integrationstest erwartet jedes Mal die Ausgabe
  `Prüfsumme bestätigt`. Ein korrekt idempotenter zweiter Lauf, der früh
  zurückkehrt, würde diesen Test dadurch fehlschlagen.
- Die Debug/Release-Auswahl im Test wirkt vertauscht: Für eine Test-Assembly
  unter `Release` wird `Debug` an das Script übergeben, für `Debug` dagegen
  `Release`.
- Es werden weder Rerun/Upgrade noch unterbrochene Übertragung, Installations-
  rollback, Autoupdate-Policy oder postinstallierter Binary-Hash getestet.
- `ImageBuildConfig` speichert Komponenten, Snaps, Runtime, Cloud-init-Dateiname,
  Modell-ID und Modell-Digest. Gast-Skriptinhalte werden config-getrieben
  bei `image-configure` in die YAML geschrieben.
- `image-build` übergibt ausschließlich `--snap <name>` an `ubuntu-image`.
  Ein in cloud-init konfiguriertes `--classic` kann dadurch beim Custom-Image-
  Build verloren gehen.
- Das Basis-Modell wird von einem beweglichen `main`-Branch geladen und auf
  Classic-Identität geprüft; Modell-ID und SHA-256 werden im Profil
  protokolliert.
- `--force` kann bei einer Neukonfiguration ohne cloud-init-Bedararf eine alte
  generierte `cloud-init.yaml` stehen lassen. `vm-create --image <name>`
  leitet diese alte Datei später automatisch ab und verwendet sie erneut.
  Configure muss den Store-Zustand transaktional aktualisieren oder veraltete
  Nebendateien gezielt entfernen.

Diese Punkte gehören in den späteren Implementierungsplan, auch wenn nur
`CLOUD-INIT.md` geändert wird.

## 3. Was die lokale cloud-init-Dokumentation bestätigt

### 3.1 Format und Zweck

Die vorhandene generierte Datei beginnt korrekt mit:

```yaml
#cloud-config
```

Ein einzelnes strukturiertes `#cloud-config` ist für CoreBridge angemessen.
MIME-Multipart, `#include`, `#cloud-config-archive` und Kernel-Command-Line-
Transport lösen hier kein reales Problem. Sie wären nur für mehrere heterogene
Payloads oder Remote-Includes sinnvoll.

### 3.2 `write_files`

`write_files` unterstützt:

- Text, `!!binary`, Base64 und gzip,
- `path`, `owner` und `permissions`,
- `append` und in neueren Versionen `defer: true`.

Lokale Quelle:
`../cloud-init/doc/examples/cloud-config-write-files.txt`.

Die Funktion eignet sich für kleine Konfigurationsdateien, `.profile`-Fragmente,
Marker und öffentliche Provisioning-Metadaten. Sie ist nicht der richtige
Transport für ein rund 238 MB großes Binary: Base64 vergrößert die Nutzlast,
erhöht die Boot-Zeit und verlagert nur die Integritätsfrage.

T06 hat bewiesen, dass ein kleiner Marker auf Core 26 via `write_files` sichtbar
wird. Damit ist nicht bewiesen, dass ein großes Binary, Boot-Performance,
Idempotenz oder komplette Benutzerrechte bei solchem Payload funktionieren.

Weitere vertragliche Details:

- Standardmodus ist `0644`, Standard-Owner laut Schema `root:root`.
- Octal-Rechte sollten als YAML-String wie `'0755'` serialisiert werden; ein
  numerisches `0o600` kann trotz Implementation-Akzeptanz am strict Schema
  scheitern.
- `owner` wirkt auf die geschriebene Datei und neu erzeugte Elternverzeichnisse,
  nicht rekursiv auf ein bestehendes Home.
- Ein unbekannter User/Group lässt den Chown fehlschlagen.
- `source.uri` lädt die Bytes unverändert. Ein Download über HTTPS ist noch
  keine Signatur- oder Digest-Prüfung.
- `encoding` gilt für Inline-`content`, nicht automatisch für eine erfolgreich
  geladene URI.
- Scheitert ein URI-Fetch, wird der Eintrag übersprungen, sofern kein Inline-
  Fallback existiert; nur ein Success-Marker kann diesen Fehler zuverlässig
  sichtbar machen.
- Eine private CA muss vor einem frühen URI-Fetch im Base-Image vorhanden sein
  oder der Datei-Write muss deferred erfolgen.

Lokale Quellen:

- `../cloud-init/doc/module-docs/cc_write_files/data.yaml`
- `../cloud-init/doc/rtd/reference/yaml_examples/write_files.rst`
- `../cloud-init/cloudinit/config/schemas/schema-cloud-config-v1.json`

### 3.3 `runcmd` und `bootcmd`

Die Beispiele bestätigen:

- `runcmd` ist geordnet, first-boot und console-sichtbar.
- `bootcmd` läuft früh und bei jedem Boot.
- Normale Komponenteninstallation gehört nach `runcmd`.
- Für aktuelle Skripte wird kein `bootcmd` benötigt.
- Temporäre Dateien sollen nicht ungeschützt unter `/tmp` angenommen werden,
  weil Boot-Cleanup-Rennen möglich sind.

Lokale Quellen:

- `../cloud-init/doc/examples/cloud-config-run-cmds.txt`
- `../cloud-init/doc/examples/cloud-config-boot-cmds.txt`
- `../cloud-init/doc/examples/cloud-config.txt`

Die Ausführungssemantik ist explizit:

- Ein `runcmd`-String wird als Shell-Quelle durch `/bin/sh` interpretiert.
- Eine Argumentliste wird als argv-ähnlicher Aufruf behandelt; normale
  Shell-Expansion, Pipes und Redirection gehören dann in ein explizites
  `[/bin/sh, -c, ...]`.
- Alle `runcmd`-Einträge werden in **ein** ausführbares Script geschrieben.
- Das Script erhält standardmäßig kein `set -e`.
- Ein Fehler in der Mitte wird nicht zwingend der Exit-Code des Gesamt-Scripts.
- `bootcmd` ist für jeden Boot und nur für Arbeit geeignet, die wirklich vor
  Network, Benutzer oder Paketverfügbarkeit laufen muss.

Die Modul-Frequenz wird nicht durch einen Schlüssel innerhalb von `runcmd`
oder `write_files` gesetzt, sondern durch die Modulliste der Base Config, etwa
`[runcmd, once]`. Module wie `write_files`, `snap` und `runcmd` sind in der
Referenz Ubuntu-Base standardmäßig per-instance.

Aktuelle Schwächen in `ImageCloudInit`:

1. `.profile` wird mit `append: true` verändert. Wiederholte Ausführung kann
   Exporte duplizieren.
2. Für `.profile` wird kein expliziter Modus angegeben.
3. Konfigurationswerte werden als rohe skalare YAML-Strings ausgegeben.
   Kommas, Doppelpunkte, `#`, Anführungszeichen und mehrzeilige Shell-Syntax
   müssen zuverlässig serialisiert oder explizit als `/bin/sh -c` übergeben
   werden.
4. `chown -R ubuntu:ubuntu /home/ubuntu` ist zu breit. Es kann Rechte fremder,
   später hinzugefügter Dateien verändern.
5. Mehrere Befehle der Lib-Vendoring-Pipeline werden über Shell-Operatoren,
   Schleifen und Variablen erzeugt. Dafür braucht es eine explizite, getestete
   Shell-Argumentstruktur.
6. `apt-get download` und anschließende `cp`-Fehler werden mit `|| echo` bzw.
   `2>/dev/null` teilweise verschluckt. Der letzte Cleanup-Befehl kann trotz
   unvollständiger Bibliotheken erfolgreich laufen.
7. Console-Ausgabe und ein erfolgreicher `launch` sind kein verlässlicher
   Maschinenvertrag für erfolgreiches Provisioning.

### 3.4 `packages`

Das generische `packages`-Modul unterstützt je nach Base-Image Paketmanager wie
APT und Snap. Es kann Paketquellen aktualisieren, Installationen ausführen und
bei Classic Ubuntu auch einen Reboot anfordern. Für Reproduzierbarkeit sind
explizite Manager-Formen wie `apt: [...]` oder `snap: [...]` und gegebenenfalls
exakte Versionen sinnvoller als unpräzise generische Einträge.

Das Modul ist trotzdem kein Ubuntu-Core-Installationsmodul und kein Weg, ein
lokales Claude-Paket aus einem ausgeführten PowerShell-Script zu erzeugen.
Ein `package_upgrade: true` kann außerdem `snap refresh` auslösen; bei
bewusst gepinnten Snap-Revisionen muss der Refresh-Hold berücksichtigt werden.

Lokale Quellen:

- `../cloud-init/doc/module-docs/cc_package_update_upgrade_install/data.yaml`
- `../cloud-init/doc/rtd/reference/yaml_examples/package_update_upgrade.rst`
- `../cloud-init/doc/examples/cloud-config-install-packages.txt`

Der heutige CoreBridge-Weg `apt-get download` → `dpkg -x` → Dateien nach
`~/.local/lib` kopieren ist ein eigener Mechanismus. `dpkg -x` entpackt nur
Dateien; es führt keine normale Paketinstallation, Dependency-Auflösung oder
Maintainer-Scripts aus. Das ist für bewusst vendorierte Bibliotheken brauchbar,
aber kein Claude-Paketvertrag.

### 3.5 Snap- und Paketbeispiele

In den 52 Dateien unter `doc/examples` gibt es zwar kein `snap:`-Modulbeispiel,
der breitere YAML-Reference-Baum bestätigt das dedicated Snap-Modul aber
eindeutig. Die aktuell erzeugte Form ist valide:

```yaml
snap:
  commands:
    - snap install opencode --classic
```

Wichtige Verträge:

- Das Top-Level-`snap`-Modul wartet vor Assertions/Commands auf
  `snapd`/Seeding.
- Ein Command-String muss vollständig mit `snap ` beginnen.
- Alternativ sind Argumentlisten erlaubt; `[install, snap-name]` bekommt vom
  Modul automatisch `snap` vorangestellt, `[snap, install, snap-name]` ist
  noch eindeutlicher.
- Assertions werden vor Commands ausgeführt.
- Fehlgeschlagene Snap-Commands werden gesammelt; nach allen Versuchen meldet
  das Modul den Gesamtfehler.
- Channel und Revision sind für reproduzierbare Builds nicht im aktuellen
  CoreBridge-Schema festgelegt.
- Private/unveröffentlichte Snaps gehören nicht als normale Store-Snaps
  behandelt; dafür sind Seed-Assertions vorgesehen.

Lokale Quellen:

- `../cloud-init/doc/module-docs/cc_snap/data.yaml`
- `../cloud-init/doc/module-docs/cc_snap/example2.yaml`
- `../cloud-init/doc/rtd/reference/yaml_examples/snap.rst`
- `../cloud-init/cloudinit/config/cc_snap.py`

Es gibt weder in den Examples noch im Reference einen generischen
Tar/ZIP/`.deb`-Extraktionsmechanismus. `#cloud-config-archive` ist trotz seines
Namens nur eine Liste typisierter Cloud-init-Parts und entpackt keine Dateien.

### 3.6 Erfolgssignal

`final_message` und Konsolenausgabe sind für Menschen gedacht. Ein belastbarer
CoreBridge-Vertrag sollte zusätzlich pro Komponente schreiben:

- Status `pending|running|ready|failed`,
- installierte Version,
- Artifact-Digest,
- Abschlusszeit,
- verwertbare Fehlermeldung.

Ein Marker ist erst `ready`, nachdem die eigentliche Postcondition erfüllt ist,
beispielsweise ein erfolgreicher `claude --version`-Aufruf.

### 3.7 First-Boot-Stufen und Frequenz

Cloud-init durchläuft Detect, Local, Network, Config und Final. Für die
CoreBridge-Konfiguration ergibt sich daraus die erwartete Reihenfolge:

1. `write_files` und Benutzer-/SSH-Konfiguration in Network,
2. Snap- und `runcmd`-Vorbereitung in Config,
3. Paketmodule, deferred writes und `scripts-user` in Final.

Die relevanten Module laufen standardmäßig `once-per-instance`. Ein normaler
Reboot beziehungsweise VM-Neustart ist deshalb **kein Retry** für fehlgeschlagenes
First-Boot-Provisioning. `cloud-init clean` ist ebenfalls keine geeignete
allgemeine Reparaturfunktion, weil einzelne Module nicht durchgängig idempotent
sind.

Lokale Quellen:

- `../cloud-init/doc/rtd/explanation/boot.rst`
- `../cloud-init/doc/rtd/howto/rerun_cloud_init.rst`
- `../cloud-init/doc/rtd/reference/cli.rst`

Für Templates ist zusätzlich ungeklärt, ob ein geklontes System unter einer
neuen Instance-ID dieselbe per-instance Konfiguration erneut ausführt. Genau
dieses Verhalten soll T13 klären. Bis dahin muss die `.profile`-Erzeugung auch
bei erneuter Ausführung duplicate-frei sein.

Ein Custom Image darf keinen abgeschlossenen Cloud-init-Cache/Semaphoren aus
einer früheren VM enthalten. Sonst kann Cloud-init die VM als bereits
provisioniert behandeln und `write_files`, `snap` oder `runcmd` überspringen.
Umgekehrt muss ein Template mitgegebene SSH-Host-Keys nicht unbeabsichtigt in
Agent-VMs weiterreichen.

Die generierte launch-time Datei sollte weder `datasource: NoCloud` noch einen
Jinja-Header hartcodieren. Multipass liefert den Datasource; Jinja würde vor
allem das erste Header-Verhalten verändern und muss separat auf Multipass-
Akzeptanz geprüft werden.

### 3.8 Multipass-Launch ist kein Provisioning-Erfolg

Der Multipass-How-to akzeptiert bei `--cloud-init` ausschließlich User-Data im
Format `#cloud-config`; die erste Zeile und valides YAML sind Pflicht. Die
aktuelle Generatorausgabe erfüllt das.

Ein erfolgreicher `launch`-Prozess ist aber kein vollständiger Health-Check:

- Multipass hat eigene Startup- und Initialisierungs-Timeouts.
- Bei einem Timeout kann cloud-init im Hintergrund weiterlaufen.
- `VmService.Create` prüft derzeit nur den Exit-Code des
  `MultipassCli`-Prozesses.
- Ein erfolgreicher Prozess kann mit `degraded done` enden.

Nach `vm-create` sollte CoreBridge deshalb mindestens prüfen:

1. `cloud-init status --wait --format json`,
2. Exit-Code und `extended_status`,
3. komponentenspezifische Success-Sentinels,
4. direkte Postconditions wie `claude --version`, `dotnet --info`, Symlink-Ziele
   und `ldd` ohne `not found`.

Die wichtigsten Logs sind:

- `/run/cloud-init/ds-identify.log`,
- `/var/log/cloud-init.log`,
- `/var/log/cloud-init-output.log`.

Lokale Quellen:

- `../cloud-init/doc/rtd/howto/launch_multipass.rst`
- `../cloud-init/doc/rtd/howto/debug_user_data.rst`
- `../cloud-init/doc/rtd/howto/status.rst`
- `../cloud-init/doc/rtd/howto/debugging.rst`
- `../cloud-init/doc/rtd/reference/cli.rst`

### 3.9 Konkrete Defekte im aktuellen `runcmd`

Die How-to- und Modulquellen bestätigen zwei zusätzliche Probleme, die über
allgemeine Robustheit hinausgehen:

1. Das Zielverzeichnis `/home/ubuntu/.local/lib` für vendored Libraries wird
   nicht explizit angelegt. `cp` kann deshalb auf einer frischen VM fehlschlagen.
2. Der Guard

   ```sh
   ldd ... | grep -q "not found" || exit 0
   ```

   beendet das gemeinsame `runcmd`-Shellskript, wenn keine Bibliothek fehlt.
   Dadurch werden alle danach erzeugten Befehle, einschließlich des
   Ownership-Fixes, übersprungen.

Zusätzlich fehlen:

- eine abschließende `ldd`-Postcondition,
- eine Prüfung, ob alle erwarteten `.deb`-Dateien und extrahierten Libraries
  vorhanden sind,
- fail-fast-Semantik für den kompletten Vendor-Block,
- eine Erklärung der doppelten Snap-Installation: Ein Custom Image baut
  konfigurierte Snaps bereits mit `ubuntu-image --snap` ein, während das beim
  Start automatisch abgeleitete cloud-config dieselben Snaps nochmals installiert.

Snap-Kommandos sollten als YAML-Argumentliste, beispielsweise
`[install, snap-name, --classic]`, statt als roher Shell-String serialisiert
werden. Store-Snaps brauchen außerdem eine explizite Channel-/Revision-Policy,
wenn Builds reproduzierbar sein sollen.

### 3.10 Netzwerk, Ownership und Secrets

- Multipass liefert die Netzkonfiguration. Das generierte cloud-config darf
  keine Interface-, Route- oder DHCP-Konfiguration ersetzen.
- Provisioning-Material wird als config-getriebene `write_files`/`runcmd`-YAML
  übergeben. Die VM baut keine Rückverbindung zum Host auf, um
  Installationsmaterial zu holen.
- Falls später Remote-Includes verwendet werden, müssen sie immutable sein,
  ohne Credentials in URL/Header enthalten und anschließend geprüft werden.
- `write_files` läuft im Network-Stadium vor `users_groups`. Ein Benutzer, auf
  den eine Datei zeigt, muss daher schon im Base-Image existieren oder die
  Datei braucht `defer: true`.
- Granulare Ownership für `.profile`, `.local/bin`, `.local/lib` und gegebenen-
  falls `~/snap` ist sicherer als `chown -R /home/ubuntu`.
- Generiertes user-data sollte weder private SSH-Keys noch API-/Anthropic-
  Credentials enthalten.

### 3.11 Benutzer, SSH und Locale

`runcmd` und `scripts_user` laufen normalerweise als root. Das eingebettete
Gast-Skript verwendet deshalb explizit `sudo -u ubuntu -H bash -lc`, damit
Claude im Benutzer-Home und mit korrekter PATH-Umgebung installiert wird.

`ImageCloudInit` ist an `/home/ubuntu` gekoppelt. Das entspricht dem aktuellen
Ubuntu-Base, ist aber kein vertraglich erkannter Default User. Ein Custom Model
mit anderem Default User würde sämtliche Pfade, Profile und Ownership-Annahmen
brechen. Eine zukünftige Config sollte den Benutzer explizit als Teil des
Komponentenplans modellieren.

Das Weglassen eines `users`-Blocks ist momentan richtig, weil der vorhandene
Default User und seine SSH-Anbindung erhalten bleiben. Falls später ein
`users`-Block nötig wird, muss `default` erhalten bleiben; private Host-Keys
und `ssh_import_id` gehören nicht in ein isoliertes Agent-Image.

Ohne `locale`-Key übernimmt Cloud-init den Datasource-Default. Das ist für den
aktuellen Start okay. Für einen expliziten deterministischen Vertrag sollte
erst `C.UTF-8` beziehungsweise `locale: false` gegen das jeweilige Base-Image
verifiziert werden; Locale-Daten dürfen auf Core nicht vorausgesetzt werden.

### 3.12 Output und Diagnose

- Das Reference-Default für `output` leitet Stage- und User-Script-Ausgabe in
  `/var/log/cloud-init-output.log`; die primären Logs bleiben unter
  `/var/log/cloud-init.log` und `/run/cloud-init/ds-identify.log`.
- `runcmd` läuft im Final-Stadium und unterliegt deshalb der `final`-/`all`-
  Output-Policy.
- `2>/dev/null` und `|| true` entfernen genau die Evidenz, die CoreBridge zur
  Fehlerdiagnose braucht.
- Ein nie endender Befehl blockiert den Final-Stadium-Abschluss. Für
  wirklich every-boot Arbeit braucht `bootcmd` einen expliziten
  `cloud-init-per once`-Guard.
- `final_message` bleibt für Menschen nützlich, ist aber kein Health-Check.

Lokale Quellen:

- `../cloud-init/doc/rtd/development/logging.rst`
- `../cloud-init/doc/rtd/reference/user_files.rst`
- `../cloud-init/doc/module-docs/cc_locale/data.yaml`
- `../cloud-init/doc/module-docs/cc_ssh/data.yaml`

## 4. Packaging-Optionen

| Option | Classic-Passung | Reproduzierbarkeit | Empfehlung |
|---|---:|---:|---|
| Offizielles `claude.ai/install.sh` per cloud-init `runcmd` | gut | mit `stable`/späterer Pin-Policy | **Aktiver Classic-Weg** |
| Binary über `write_files` | technisch möglich, ungeeignet für ~238 MB | aufwendig | **Nein** |
| Core-only Host-Transfer | für Classic unnötig | gut, aber nicht mehr aktiv | **Veraltet** |
| CoreBridge-eigener Snap | unnötig | möglich | Nur für breitere Paketstrategie neu bewerten |
| Custom Image | langfristig gut | gut mit exakten Revisionen | Windows-Import blockiert aktuell |
| Gestoppte Template-VM + `clone` | gut | gut bei unveränderlicher, protokollierter Vorlage | **Spätere Optimierung nach T12/T13** |

### 4.1 Warum kein eigenes `.deb` auf Classic Ubuntu

Ein selbstgebautes Paket würde:

- die native Claude-Versionverwaltung duplizieren,
- eine zweite Update- und Uninstall-Policy erzeugen,
- Paketmetadaten und Signaturpflege erfordern,
- weiterhin das originale Binary enthalten,
- keinen Vorteil gegenüber einem verifizierten Host-Artifact bringen,
- auf klassischem Ubuntu sogar einer offiziell signierten apt-Quelle
  unterlegen.

CoreBridge sollte außerdem keine binären Releases in das Git-Repository
einchecken.

### 4.2 Empfohlene Alternative: versionierter Artifact-Store

Unter einem maschinenlokalen Pfad beispielsweise:

```text
%LOCALAPPDATA%\CoreBridge\artifacts\claude\<version>\
  manifest.json
  manifest.json.sig
  linux-x64
  provenance.json
```

`provenance.json` sollte mindestens enthalten:

```json
{
  "component": "claude",
  "version": "<exact version>",
  "platform": "linux-x64",
  "sha256": "<binary digest>",
  "manifestUrl": "<source URL>",
  "signingKeyFingerprint": "31DD DE24 DDFA B679 F42D 7BD2 BAA9 29FF 1A7ECACE",
  "verifiedAtUtc": "<timestamp>"
}
```

Das ist kein neues Ausführungsformat. Es ist eine nachvollziehbare, wiederverwendbare
Kopie der bereits verifizierten Upstream-Artefakte. Wird der Hash bereits als
Schlüssel verwendet, ist zusätzlich ein content-addressed Cache möglich.

### 4.3 Ein eigener Snap ist technisch möglich, aber nicht der Sofortweg

Ubuntu Core selbst besteht aus snaps. Canonical dokumentiert für ein `dangerous`
Model ausdrücklich, dass `ubuntu-image snap ... --snap ./lokales.snap` ein
lokal gebautes Snap in das Image aufnehmen kann. Ein CoreBridge-Snap wäre daher
technisch der native Paketweg und könnte Revision, Abhängigkeiten und Assertions
über das Modell verankern.

Die Gegenseite sind:

- Innerhalb eines strict snaps wird `HOME` auf `SNAP_USER_DATA` gesetzt.
- Zugriff auf Workspace, Credentials, Toolchains und Dateien außerhalb des
  Snap-Zustands benötigt bewusst deklarierte Interfaces.
- Claude ist selbst ein CLI-Agent und startet weitere Prozesse; Snap-Confinement,
  AppArmor, Seccomp und `network` müssen zusammenpassen.
- Ein klassisch konfiner Snap verzichtet auf diese Isolation und ist auf Ubuntu
  Core nicht derselbe Vertrag wie auf klassischem Ubuntu.
- Snap-Refresh und Claudes eigener nativer Updater wären zwei konkurrierende
  Updateautoritäten; der native Updater müsste in einem gepackten Snap
  deaktiviert werden.
- Die aktuelle Windows-Importpipeline für das fertige `.img` ist blockiert.

Ein eigener Snap lohnt sich daher erst, wenn mehrere Core-only Binaries eine
gemeinsame Paket-, Confinement- und Provenienzstrategie brauchen. Als
schnellster Core-Weg bleibt der hostseitig verifizierte native Bootstrap.

### 4.4 Klassisches Ubuntu als anderer Zielpfad

Falls die Basisentscheidung auf klassisches Ubuntu fällt, ist der von Anthropic
offiziell signierte apt-Kanal die einfachste Installationsquelle. Der
dokumentierte Stable-Channel sieht grob so aus:

```sh
sudo install -d -m 0755 /etc/apt/keyrings
curl -fsSL https://downloads.claude.ai/keys/claude-code.asc \
  -o /etc/apt/keyrings/claude-code.asc
gpg --show-keys /etc/apt/keyrings/claude-code.asc
# Fingerabdruck: 31DD DE24 DDFA B679 F42D 7BD2 BAA9 29FF 1A7ECACE
echo 'deb [signed-by=/etc/apt/keyrings/claude-code.asc] https://downloads.claude.ai/claude-code/apt/stable stable main' \
  | sudo tee /etc/apt/sources.list.d/claude-code.list
sudo apt update
sudo apt install claude-code
```

Der `latest`-Channel verwendet die entsprechende `/latest`-URL und Suite `latest`.
Paketmanager-Installationen aktualisieren sich nicht über Claudes eigenen
Background-Updater; Updates laufen über `apt update && apt upgrade`. Das ist
ein sauberer Paketvertrag, aber nicht der empfohlene Core-Pfad.

## 5. Empfohlener Installationsvertrag

### 5.1 Zwei ausdrückliche Update-Modi

#### `pinned`

- exakte Version,
- `DISABLE_UPDATES=1`,
- Template- oder Reproduzierbarkeitszweck,
- Upgrade nur durch einen kontrollierten Neuaufbau.

#### `managed-stable`

- stabiler Release Channel,
- dokumentierte Hintergrundupdates erlaubt,
- beobachtete Version in VM-Metadaten speichern,
- keine Behauptung, dass zwei VMs exakt denselben Claude-Stand haben.

Ein `floating-latest`-Modus ist allenfalls für interaktive Ad-hoc-VMs sinnvoll,
nicht als Default für Images oder Templates.

### 5.2 Installationsphasen

1. **Auflösen:** exakte Version aus Konfiguration oder `stable`-Policy wählen.
2. **Signatur prüfen:** Manifest und detached Signature mit gepinntem
   Anthropic-Fingerprint prüfen.
3. **Digest prüfen:** Binärdatei gegen das signierte Manifest prüfen.
4. **Cache schreiben:** versioniert und atomar in den lokalen Artifact-Store
   legen.
5. **Prüfen:** Existenz, Version und Digest eines vorhandenen VM-Installations-
   Eintrags exakt vergleichen.
6. **Staging:** in einen temporären Guest-Pfad übertragen; niemals direkt den
   funktionierenden Launcher überschreiben.
7. **Gastprüfung:** SHA-256 im Gast wiederholen.
8. **Installation:** das Bootstrap-Binary aus dem Staging-Pfad mit explizitem
   Target `install <version>` beziehungsweise `install stable` aufrufen; das
   native Claude-Layout atomar aktivieren.
9. **Postcondition:** `~/.local/bin/claude`, `claude --version`, Versionsdatei und
   gespeicherte Metadaten prüfen.
10. **Protokollieren:** Version, Digest, Quelle, Signaturkey, Zeitpunkt und
    Installerstatus ablegen.

Ein zukünftiger CoreBridge-Befehl kann diese Phase später als
`component-status` oder im Autobridge-Lauf auswerten. Der aktuelle
Cloud-init-Weg schreibt und startet das Gast-Skript direkt; es gibt keine
Host-Installationsphase.

## 6. Empfohlene Härtung von `ImageCloudInit`

Unabhängig von der Claude-Entscheidung:

1. **YAML sicher serialisieren:** keine unescapen config-abgeleiteten Strings.
2. **Shell explizit aufrufen:** komplexe Kommandos als
   `[/bin/sh, -c, <script>]` modellieren und testbar serialisieren; keine
   `exit`-Statements in einem gemeinsamen `runcmd`-Skript.
3. **Idempotente `.profile`-Blöcke:** vollständige verwaltete Datei oder
   markierten Block mit Duplikatschutz statt blindem `append`.
4. **Explizite Rechte:** als YAML-Oktalstrings, `.profile` `'0644'`,
   ausführbare Dateien `'0755'`, private Daten nur mit passendem Modus.
5. **Granulare Ownership:** nur von CoreBridge erzeugte Pfade ändern, nicht das
   gesamte Home-Verzeichnis.
6. **Zielverzeichnisse anlegen:** insbesondere `.local/lib` vor jedem `cp`.
7. **Fail-fast Vendoring:** Paketdownload, Extraktion und Kopieren müssen
   fehlschlagen lassen; keine blanket `|| true`-Semantik.
8. **Vendor-Postcondition:** vollständige Library-Menge prüfen und abschließend
   `ldd` ohne `not found` verlangen.
9. **Quoting validieren:** Pfade, Paketnamen und Snaps gegen Shell- und
   YAML-Metazeichen validieren.
10. **Snap-Policy:** Channel/Revision pinnen und bereits gebaute Snaps nicht
    erneut installieren.
11. **Komponentenmarker:** Status und Version nach erfüllter Postcondition.
12. **Cloud-init-Health:** nach `vm-create` JSON-Status inklusive
    `extended_status` prüfen.
13. **Final Message:** kurze menschenlesbare Zusammenfassung zusätzlich zum
    Marker.
14. **Keine Secrets:** private SSH-, API- oder Anthropic-Schlüssel gehören
    niemals in generiertes cloud-init.
15. **Benutzer explizit modellieren:** `/home/ubuntu` nicht stillschweigend
    voraussetzen; Owner und Ausführungs-Home müssen zum Base-Image passen.
16. **Root-Kontext vermeiden:** einen möglichen Claude-CLI-Aufruf als
    `ubuntu -H` ausführen, damit `~/.local` und `~/.claude` korrekt sind.
17. **Schema validieren:** generiertes YAML parsen und gegen das relevante
    cloud-init-Schema prüfen; nicht nur Textfragmente vergleichen.
18. **Image-Cache absichern:** ein Custom Image darf keinen abgeschlossenen
    First-Boot-Cache oder alte Semaphoren enthalten.
19. **Keine breite Paket-Upgrade-Policy:** insbesondere keine ungewollten
    Snap-Refreshes bei gepinnten Revisionen.

## 7. Template-Fabrik als spätere Lösung

Nach Freigabe von T12/T13 passt am besten Variante B aus
`PLAN_TEMPLATE_FACTORY.md`:

- eine gestoppte Template-VM je benötigtem Komponenten-Set,
- Claude bereits über die cloud-init-Postcondition als verfügbar geprüft,
- native Updates im Template-Modus deaktiviert,
- keine Credentials, kein Projekt-Sync und kein Agent-State im Template,
- Upgrade durch kontrollierten Neuaufbau vom sauberen Template,
- Agent-VM durch `clone` erzeugen.

Ein Template ist dabei kein Ersatz für die Artifact-Provenienz. Die Vorlage
sollte auf eine konkrete Version und einen Binary-Digest zurückgeführt werden
können.

## 8. Entscheidung: Classic Ubuntu 26.10 Edge

### Zielbetriebssystem

- **Aktiv:** klassisches Ubuntu 26.10 Edge.
- Modell: `ubuntu-classic-2610-amd64-dangerous.model`.
- Multipass-Runtime: `daily:26.10`.
- `26.04` wird ausschließlich als Werkzeug-Image der Build-VM verwendet.
- Die alten Core-26-Aussagen bleiben historische Experimente und werden nicht
  auf Classic übertragen.

### Reproduzierbarkeit versus Autoupdate

- Profil und Runtime werden als `daily:26.10`/Modell-Digest persistiert;
  Edge-Inputs können sich bewegen und werden bei jedem `image-configure`
  erneut validiert.
- Eine `pinned`-/`managed-stable`-Claude-Policy ist noch nicht als separates
  Vertragsmodell implementiert.

### Eigener Snap

Ein eigener Snap bleibt zurückgestellt. Für Classic Ubuntu wird Claude
standardmäßig über das eingebettete Gast-Skript und cloud-init-Pakete
provisioniert; der `claude-code`-Snap ist kein Default.

## 9. Empfohlene Reihenfolge für eine spätere Umsetzung

### Phase 0 — Entscheidung (erledigt)

1. Classic Ubuntu 26.10 Edge als Zielbasis festlegen.
2. `image-configure` als Profil-/Rezeptschritt und `vm-create` als
   Launch-Orchestrator verwenden.
3. Gast-Installer über `write_files`/`runcmd` in cloud-init ausführen.

### Phase 1 — Gast-Cloud-init-Provisionierung

1. Paketliste und Skriptinhalt config-getrieben modellieren.
2. `write_files`-Pfad, Rechte, Ownership und `runcmd`-Aufruf prüfen.
3. `claude --version` und `/var/lib/corebridge/claude.ready` als
   Postconditions auswerten.
4. Idempotenz, Fehlerstatus und cloud-init-Health dokumentieren.

### Phase 2 — Profil und Runtime

1. Modell-ID und SHA-256 im Profil persistieren.
2. `daily:26.10` und Classic-Modell gemeinsam verwalten.
3. Keine stillen Fallbacks auf `26.04` oder `core26` einbauen.

### Phase 3 — Cloud-init-Härtung und Health-Vertrag

1. YAML-Ausgabe als Datenmodell serialisieren.
2. Snap- und einfache `runcmd`-Kommandos als Argumentlisten ausgeben.
3. Vendor-Fehler, Zielverzeichnisse und `ldd`-Postcondition reparieren.
4. `.profile`-Duplikate und überbreite Ownership entfernen.
5. Komponentenmarker und `cloud-init status --wait --format json` auswerten.
6. Bestehende String-Assertions durch YAML-Parsing/-Schema und semantische
   Postconditionen ergänzen.

### Phase 4 — Integrationstests

Nach expliziter Freigabe eines reproduzierbaren Laufs:

- frische Installation,
- identischer zweiter Lauf ohne Transfer,
- Upgrade,
- fehlender/teilweiser Vendor-Block,
- beschädigtes Binary,
- Signaturfehler,
- unterbrochene Übertragung,
- `pinned` versus `managed-stable`,
- cloud-init `done` versus `degraded done`,
- Cloud-init-Status nach Timeout,
- Sicherheitscheck auf Credentials in generiertem user-data.

### Phase 5 — Template-Optimierung

Erst nach T12/T13 und einer Entscheidung für Variante A oder B:

1. Template ohne Credentials und Agent-State bauen.
2. Claude-Policy und Provenienz im Template protokollieren.
3. Template stoppen und Agent-VMs daraus clonen.
4. Template-Upgrades nur durch kontrollierten Neuaufbau.

Diese Reihenfolge trennt die dringende Installationskorrektur von der noch
nicht freigegebenen Template-Fabrik.

## 10. Forschungsstand und Validierung

Die Recherche ist abgeschlossen:

- alle 52 Dateien unter `doc\examples`,
- alle 18 Dateien unter `doc\rtd\howto`,
- der relevante Teil von `doc\rtd\reference\yaml_examples`,
- der relevante Gesamtbestand von `doc\rtd\reference`,
- verlinkte Modul-, Schema- und Bootquellen,
- die aktuelle CoreBridge-Implementierung und Tests,
- offizielle Anthropic- und Ubuntu-Core-Dokumentation.

Validiert wurde:

- `scripts/components/install-claude.sh` wird in die generierte cloud-init-YAML
  eingebettet; die Datei wird nicht separat vom Host gestartet.
- `CLOUD-INIT.md` besteht `git diff --check`.
- Der CLI-first-Prozesslauf verwendet `DOTNET_ENVIRONMENT=Development` und
  `appsettings.Development.json` (`Images:StoreRoot=artifacts`).
- Es wurde kein Multipass-Kommando direkt aus der Agent-Shell aufgerufen.

Offen bleiben bewusst spätere Entscheidungen beziehungsweise freizugebende
Experimente:

1. `pinned` oder `managed-stable` als Default-Update-Policy.
2. T12/T13 für Template/Snapshot/Clone-Semantik.
3. Classic-Edge-Messungen für Latenz, Cloud-init-Health und Komponenten-
   Postconditions; historische Core-Messungen dürfen nicht als Classic-Werte
   verwendet werden.
4. Import eines gebauten Classic-`.img` über den Windows-https-Workaround.

## 11. Quellen

### CoreBridge

- `scripts/components/install-claude.sh`
- `src/CoreBridge.Cli/appsettings.json`
- `src/CoreBridge.Core/Config/AppConfig.cs`
- `src/CoreBridge.Core/Images/ImageCloudInit.cs`
- `src/CoreBridge.Core/Images/ImageComponents.cs`
- `src/CoreBridge.Core/Images/ImageService.cs`
- `test/CoreBridge.Core.Tests/ImageTests.cs`
- `test/CoreBridge.Core.Tests/IntegrationsTests.cs`
- `IMPLEMENTATION_PLAN.md`
- `EXPERIMENT_LOG.md`
- `PLAN_TEMPLATE_FACTORY.md`

### Lokaler cloud-init-Checkout

- `F:\source\cloud-init\doc\examples` — vollständig geprüft
- `F:\source\cloud-init\doc\rtd\howto` — vollständig geprüft
- `F:\source\cloud-init\doc\rtd\reference\yaml_examples` — vollständig geprüft
- `F:\source\cloud-init\doc\rtd\reference` — relevanter Gesamtbestand geprüft

### Anthropic

- Native Installation, Updates, Paketmanager und Signaturprüfung:
  <https://code.claude.com/docs/en/setup>
- Offizielles Linux-Installerscript und bestätigte `install`-Targets:
  <https://claude.ai/install.sh>
- Abruf am 25.09.2026.

### Ubuntu Core

- Security, Snap-Confinement, `HOME=SNAP_USER_DATA` und Interfaces:
  <https://documentation.ubuntu.com/core/explanation/security-and-sandboxing/>
- Lokale Custom Snaps in einem `dangerous` Model:
  <https://documentation.ubuntu.com/core/how-to-guides/image-creation/add-custom-snaps/>
- Read-only Seed und snap-basiertes System-/User-Storage-Layout:
  <https://documentation.ubuntu.com/core/explanation/core-elements/storage-layout/>
- Abruf am 25.09.2026.
