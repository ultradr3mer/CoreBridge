using CoreBridge.Interaction;
using CoreBridge.Core.Authenticity;
using CoreBridge.Core.Inits;

namespace CoreBridge.Cli;

public static partial class Program
{
  /// <summary>
  /// Zeigt bei jeder Verwendung, wer die Komponenten signiert hat. Ungültig (Inhalt nach dem
  /// Signieren geändert, Signatur kaputt) bricht mit Exit 3 ab; unsigniert oder unbekannter
  /// Schlüssel lädt, aber mit einem gerahmten Warnblock auf stderr (an der Konsole rot).
  /// <paramref name="fromProfile"/>: Komponenten eines bestehenden Profils, die der Katalog nicht
  /// mehr kennt, brechen mit Exit 1 ab (sonst lehnt der Aufrufer unbekannte Namen ohnehin ab) —
  /// außer das Profil ist vertrauenswürdig signiert (<paramref name="profileSigned"/>): dann bürgt
  /// die Profilsignatur für die gerenderte cloud-init, und der heutige Stand der Komponenten ist
  /// nur noch Auskunft.
  /// </summary>
  private static async Task ReportComponentTrust(CommandContext context, IEnumerable<string> components, bool fromProfile = false, bool profileSigned = false)
  {
    var events = context.Events;
    var all = components
        .Select(name => name.Trim())
        .Where(name => name.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(name => (Name: name, Origin: context.Catalog.OriginOf(name)))
        .ToList();
    var origins = all
        .Where(entry => entry.Origin is not null)
        .Select(entry => (entry.Name, Origin: entry.Origin!))
        .ToList();

    // Ein Profil behält seine gerenderte cloud-init, auch wenn die Komponente inzwischen aus
    // dem Katalog verschwunden ist — ihr eingebettetes Skript liefe ohne prüfbare Herkunft.
    var orphaned = all.Where(entry => entry.Origin is null).Select(entry => entry.Name).ToList();
    if (fromProfile && profileSigned)
    {
      foreach (var (name, origin) in all)
      {
        await events.Emit(events.Std($"  Komponente {name}: {origin?.Signature.Describe() ?? "nicht mehr im Katalog"} (gedeckt durch die Profilsignatur)"));
      }

      return;
    }

    if (fromProfile && orphaned.Count > 0)
    {
      throw new ProgramExecutionException(
          1,
          $"Das Profil enthält Komponenten, die es nicht mehr gibt: {string.Join(", ", orphaned)} — und es ist nicht vertrauenswürdig signiert, "
              + "ihr Skript steckt aber noch in seiner cloud-init. Nichts wurde angelegt. "
              + "Profil neu erzeugen (cb init-configure -n <profil> -c … -f, mit eigenem Schlüssel) oder die Komponente wieder hinzufügen.");
    }

    foreach (var (name, origin) in origins)
    {
      await events.Emit(events.Std($"  Komponente {name}: {origin.Signature.Describe()}"));
    }

    var invalid = origins.Where(entry => entry.Origin.Signature.Trust == ComponentTrust.Invalid).ToList();
    if (invalid.Count > 0)
    {
      throw new ProgramExecutionException(
          3,
          "Komponente nicht vertrauenswürdig signiert:" + Environment.NewLine
          + string.Join(Environment.NewLine, invalid.Select(entry => $"  {entry.Name}: {entry.Origin.Signature.Reason} ({entry.Origin.Signature.File})"))
          + Environment.NewLine + "Nichts wurde verwendet. Datei aus der Quelle neu holen oder neu signieren (prüfen: cb settings-verify).");
    }

    var warn = origins.Where(entry => entry.Origin.Signature.NeedsWarning).ToList();
    if (warn.Count > 0)
    {
      foreach (var line in WarningBlock(warn))
      {
        await events.Emit(events.Err(line));
      }
    }
  }

  /// <summary>Der laute Warnblock: gerahmt, damit er zwischen Fortschrittszeilen nicht untergeht.</summary>
  private static IEnumerable<string> WarningBlock(IReadOnlyList<(string Name, ComponentOrigin Origin)> entries)
  {
    const string Rule = "════════════════════════════════════════════════════════════════════";
    yield return "╔" + Rule;
    yield return "║ WARNUNG: Komponenten ohne vertrauenswürdige Signatur — ihr Skript läuft als root im Gast.";
    foreach (var (name, origin) in entries)
    {
      yield return $"║   {name}: {origin.Signature.Describe()}";
      yield return $"║     Datei: {origin.File}";
    }

    yield return "║ Prüfen: cb component-list / cb settings-verify. Vertrauen: cb key-trust -f <öffentlicher Schlüssel>.";
    yield return "║ Signieren: cb sign -f <datei> mit eigenem Schlüssel (Signing:KeyFile oder Signing:GpgKey; Bundles: cb component-bundle).";
    yield return "╚" + Rule;
  }

  private static async Task RunKeyTrust(CommandContext context, KeyTrustOptions options)
  {
    var store = new TrustedKeyStore(InitStore.TrustedKeysFolder);
    foreach (var entry in store.Trust(options.File))
    {
      await context.Events.Emit(context.Events.Std(
          $"Vertraut (nur Komponenten und Bundles): {entry.UserId ?? "?"} — Fingerprint {entry.Fingerprint}{Expiry(entry)}"));
      await context.Events.Emit(context.Events.Std($"  {entry.File}"));
    }

    await context.Events.Emit(context.Events.Std("Signaturen dieses Schlüssels heißen jetzt „Benutzer-vertraut“; die mitgelieferten appsettings prüft weiter nur der CoreBridge-Schlüssel."));
  }

  private static async Task RunKeyList(CommandContext context)
  {
    var events = context.Events;
    await events.Emit(events.Std($"{"Vertrauen",-16} {"Fingerprint",-41} Benutzer-ID"));
    foreach (Org.BouncyCastle.Bcpg.OpenPgp.PgpPublicKeyRing ring in TrustedKeys.CoreBridge.GetKeyRings())
    {
      var key = ring.GetPublicKey();
      await events.Emit(events.Std($"{"eingebettet",-16} {Convert.ToHexString(key.GetFingerprint()),-41} {key.GetUserIds().FirstOrDefault()} (alles)"));
    }

    var entries = new TrustedKeyStore(InitStore.TrustedKeysFolder).List();
    foreach (var entry in entries)
    {
      await events.Emit(events.Std($"{"Benutzer",-16} {entry.Fingerprint,-41} {entry.UserId ?? "?"} (nur Komponenten){Expiry(entry)}"));
    }

    if (entries.Count == 0)
    {
      await events.Emit(events.Std($"Keine Benutzer-vertrauten Schlüssel ({InitStore.TrustedKeysFolder}) — hinzufügen mit `cb key-trust -f <pub.asc>`."));
    }
  }

  private static async Task RunKeyUntrust(CommandContext context, KeyUntrustOptions options)
  {
    var removed = new TrustedKeyStore(InitStore.TrustedKeysFolder).Untrust(options.Key);
    await context.Events.Emit(context.Events.Std($"Kein Vertrauen mehr: {removed.UserId ?? "?"} — Fingerprint {removed.Fingerprint}"));
  }

  private static string Expiry(TrustedKeyEntry entry) =>
      entry.Expires is { } expires ? $", läuft ab {expires:yyyy-MM-dd}" : string.Empty;

  /// <summary>
  /// Signaturen der Komponenten für <c>settings-verify</c>: jede Datei einmal (Definition,
  /// Bundle, Skript), geprüft mit CoreBridge plus Vertrauensordner.
  /// </summary>
  private static IReadOnlyList<(SignatureCheck Check, ComponentSignature Signature)> ComponentChecks(CommandContext context)
  {
    var trusted = ComponentTrustBundle();
    var now = DateTime.UtcNow;
    var files = new List<string>();
    files.AddRange(ComponentCatalog.Bundles(Path.Combine(context.Sources.AppDirectory, ComponentCatalog.ShippedBundleFolder)));
    files.AddRange(ComponentCatalog.UserFiles(context.Sources.UserFolder));
    files.AddRange(context.Catalog.Inits.Components.Values
        .Where(component => string.IsNullOrWhiteSpace(component.ScriptContent) && !string.IsNullOrWhiteSpace(component.Script))
        .Select(component => InitStore.ResolvePath(component.Script!)));
    return files
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(file => SettingsSignatures.VerifyFile(file, trusted, now))
        .Select(check => (check, ComponentSignature.From(check)))
        .ToList();
  }
}
