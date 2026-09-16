
# PROJECT: CORE-BRIDGE

Die idee bleibt die Gleiche, allerdings müssen hier erst erfahrungen gesammelt werden, es sollte erst eine Konsolenanwendung für de erstellen /  konfigurieren / verbinden von core vms erstellt werden

## UBUNTU CORE w MULTIPASS

Die multipass docs können so herntergeladen werden:
git clone --filter=blob:none --no-checkout https://github.com/canonical/multipass.git

veryfy with
> run multipass version 
to check if installed, otherwise show a link to 
https://canonical.com/multipass/docs/stable/how-to-guides/install-multipass/#how-to-guides-install-multipass
and
https://canonical.com/multipass/download/windows -direkter download

winget does have
Multipass Canonical.Multipass 1.16.4 
but
> Alternatively, you can also check your preferred package manager to see if it provides Multipass, although this option is not officially supported.

## CONTAINERIZATION

- Windows-Anwendung (C#/.NET), die ein **echtes Git-Repo auf dem Host** mit einem **isolierten Claude Code in einer Hyper-V-Linux-VM (Mint)** verbindet
- Ein gemeinsame Git-Historie: Host-Repo bleibt auf `main`, die VM hat ihren clone, änderungen werden vor dem zurückführen kurz mit 'git show' angezeigt
- **Niemals direkter Merge**:
  - Host → Sandbox: Änderungen fließen pull hinein
  - Sandbox → Host: nur über expliziten, **reviewten merge/pull-request**
- Claude arbeitet also frei & isoliert — nichts erreicht den Host unkontrolliert

## WORKFLOW

### Start: Autobridge

Wenn die CLI läuft die dinge schritt für schritt tut eg vm erstellen dann repo packen
etc ... 
Dann wird es zeit einen like-local worklfow zu Implementieren.
Ánstatt Claude oder OpenCode gibt es dann
> AutoBridge 
 
Mit ARGUMETEN: --directory: bei bedarf abweichender ordner und Optionen die insbesondere die VM die verwendet werden soll beschreiben. [Claude|OpenCode] [Python|Net] sowas.

---

Das command hat folgende schitte: 
1. Get or Create VM 
   Prüfen: gibt es schon eine vm für diesen Ordner?
  1.1. False: wenn nicht lege ich - nach nachfrage "darf ich" eine an 
  1.2. Wenn es eine gibt wird diese nun fortgesetzt
2. Sync Repo
   Arbeitsbereich für Agenten vorbereiten, alles aktuell
3. Launch Claude / OCode
4. Stelle eine SSH verbindung mit der CLI her
5. Es beginnt der Loop

Also: 
Isolierte 
VM statt Container — Claude hat keinen direkten Weg zum Host-Repo
Keine Passwörter in der Konfiguration — Env-Variablen für die Bridge(CORE_BRIDGE_[ALL,REPONAME,...]_PASSWORD)
Secrets werden vor jedem Sync gescannt
Reviewter merge/pull-request als einziges Einfalltor zurück

### Loop: Development like local but not local

Claude bekommt ein Plugin welches in das [Senden der Benutzeranfrage] und das [Ende der Bearbeitung] durch Claude als Events hat

Sobald die session läuft wird der Benutzer ha 

EINTRITSPUNKT: Der benutzer schreibt etwas an Claude/O-Code - Diese haben ein Plugin das getriggert wird - und zwar zwsichen [Benutzer sendet] und bevor [Agent bearbeitet anfrage]
1. ggf. erneutes sync des Lokalen Repo an die VM - diese macht eineen clone
2. Wie in anhang 2 Dargestellt wird das diff in die Session bzw. an die Message gehangen.
3. Der Agent wird nachdenken, vlt wird er schreiben ...
4. Event [bevor der Agent in idle geht] - erfordert Plugin 
  4.1. sollte der Agent wenn er was geändert hat einen commit mit einer sinvollen message machen 
  4.2. Wenn er keinen Commit gemacht hat wird einer generiert
5. Der Commit wird dem Benutzer gezeigt, mit der möglichkeit diesen Abzulehnen 
6. Egal wie es ausgeht wird wieder zu der Agenten SSH verbindunge geschaltet, fals der Benutzer etwas leesen will 
7. Nun hat der user ein akteulles verzeichnis und kann tun "was auch immer"
8. Wenn der Benutzer nun dem Agenten schreibt dann ist das das ende dieses durchlaufes und der EINTRITSPUNKT eines neuen.

ZU PUNKT2:
 Es wird ein Anhang an die Bentzer anfrage die er gesendet aber nich nicht vom Agent bearbeitet wurde informiert welche dateien geändert wurden. ggf einfach "git show"

#### FRAGE/KLÄRUNG:
Wie wird der Cmmit Generiert wenn Claude das nicth tut?
 mit schlüsselwörtern der der der anfrage ? der bearbeitung ? einfach uhrzeit ?
Macht es sinn das jede vm mit ihrem eigenen git kommt? oder lieber einen server auf dem host system. Die Agents sollten auf jeden fall nicht direkt mit dem repo des Host gelinkt werden.

Ist das ding schnell genug um für jeden repo oder jeden task eine vm einzurichten?
Braucht man einen Pool?

## PROJECT STRUTURE

USE nNET 10.0 !

src/
  CoreBridge.Core
  CoreBridge.Cli
  CoreBridge.Tui # später erst
test/
  MultiPass.Experiments # Not really test more like trying things out 
  CoreBridge.Core.Tests # each project has its own test project
  ..

USE: a config.Get<AppConfig>() in both CLI and TUI

## BRIDGE CLI

im gegensatz zur alten wird hier daraum gebeten einen Parser für die argumente zu nehen.
Bitte installiere: 
> dotnet add package CommandLineParser --version 2.9.1

## TESTING and VERIFICATION

ich möchte nicht wieder herumstochern. ich möchte am besten ein testprojekt was neben unit tests auch echte integrationstest fährt und guckt ob die vm antwortet.

Anstatt das CLAUDE oder OPENCODE immer wieder herumprobieren möchte ich das reproduzierbar machen.

> VERBOT FÜR DEN AGENT MULTIPASS DIREKT ZU VERWENDEN. - ES SOLL ALLES ÜBER TESTS LAUFEN!

## TERMINAL GUI

Der ansatz mit TUI war gut, wichtig direkt mit tests zu starte
> dotnet new tui --WithTests

Die gui soll aber erst noch kommen, erstmal schauen wir uns Multipass an. 

## ANHANG
Inhalte zu lang für inline

### 1. EXECUTION SHORTCUT
---
TODO: So einen Shortcut braucehn wir wieder, Sobald es ans ausführen geht solltest du dies anpassen/Übernehmen
Ich denke anstatt "sb" "cb" zur unterscheidung, und es braucht eine möglichkeit schnell commands zu debuggen also run aber mit debugger attched. 


---
Du kannst in deinem PowerShell-Profil eine Funktion anlegen, die `sandbox-bridge` direkt startet. Dann funktioniert etwa `sb vm list` statt `dotnet run --project ... -- vm list`.

### 1. PowerShell-Profil öffnen

```powershell
notepad $PROFILE
```

Falls die Datei bzw. der Ordner noch nicht existiert:

```powershell
New-Item -ItemType File -Path $PROFILE -Force
notepad $PROFILE
```

### 2. Funktion eintragen

Damit gilt:

Auf deinem Entwicklungsrechner, wenn das Projekt existiert: dotnet run --project ...
Auf anderen Rechnern oder ohne Checkout: der installierte Befehl sandbox-bridge
Nutzbar in PowerShell, auch im integrierten Visual-Studio-Terminal, sofern dieses PowerShell verwendet.


```powershell
function sb {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]] $Arguments
    )

    $cliProject = "src/SandboxBridge.Cli"
    $tuiProject = "src/SandboxBridge.Tui"

    if ($Arguments.Count -eq 0) {
        # Kein Argument angegeben -> TUI starten statt der reinen CLI-Hilfe.
        if (Test-Path $tuiProject) {
            dotnet run --project $tuiProject
        }
        else {
            # TODO: noch keine installierte TUI-Variante - siehe TODO.md,
            # Abschnitt "Echte Installation fuer sandbox-bridge einplanen".
            sandbox-bridge
        }
        return
    }

    if (Test-Path $cliProject) {
        # Lokales Entwicklungsprojekt vorhanden
        dotnet run --project $cliProject -- @Arguments
    }
    else {
        # Installierte/bereits gebaute Variante nutzen
        sandbox-bridge @Arguments
    }
}
```

Danach PowerShell neu öffnen oder das Profil direkt laden:

```powershell
. $PROFILE
```

Dann kannst du verwenden:

```powershell
sb vm list
sb vm status --name meine-vm
sb task start --repo mein-repo --prompt "Bitte prüfe den Code"
```

## 2. Beispiel Diff


> BENUTZERNACHRICHT hier

Dann kommt eine standard Message (immer gleich)

> [Below are the changes made outside of opencode since your last message.]
Inspect the diffs closely; they may help clarify the user’s intent.For entries without a diff, read the named file yourself if it is relevant.

Nun folgt der  dynamische teil- entweder git show oder was auch immer
>modified src/SandboxBridge.Core/Vm/VmProfileService.cs at 11:57
> ```diff
> --- a/src/SandboxBridge.Core/Vm/VmProfileService.cs
> +++ b/src/SandboxBridge.Core/Vm/VmProfileService.cs
> @@ -56,7 +56,7 @@ public record HyperVVmSpec
>    /// <summary>
>    /// Hostname bzw. DNS-Name des Gastbetriebssystems innerhalb der VM.
>    /// </summary>
> -  public string? GuestHostName { get; init; }
> +  public string? GuestHostIP { get; init; }
>  
>    /// <summary>
>    /// Name oder Adresse des physischen bzw. Hyper-V-Servers,
> 
> ```

Dann kommt eine standard Closer (immer gleich)
> [End of the changes made outside.]

modified src/SandboxBridge.Core/Vm/VmProfileService.cs at 11:57
```diff
--- a/src/SandboxBridge.Core/Vm/VmProfileService.cs
+++ b/src/SandboxBridge.Core/Vm/VmProfileService.cs
@@ -56,7 +56,7 @@ public record HyperVVmSpec
   /// <summary>
   /// Hostname bzw. DNS-Name des Gastbetriebssystems innerhalb der VM.
   /// </summary>
-  public string? GuestHostName { get; init; }
+  public string? GuestHostIP { get; init; }
 
   /// <summary>
   /// Name oder Adresse des physischen bzw. Hyper-V-Servers,

```
[End of the changes made outside.]