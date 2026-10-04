using CoreBridge.Interaction;
using CoreBridge.Core.Authenticity;
using CoreBridge.Core.Inits;

namespace CoreBridge.Cli;

public static partial class Program
{
  /// <summary>Hinweis, wenn kein eigener Schlüssel eingetragen ist.</summary>
  private const string NoSigningKeyHint =
      "kein Signierschlüssel eingetragen — Signing:KeyFile (exportierter geheimer Schlüssel, ohne gpg) oder Signing:GpgKey in appsettings.local.json";

  /// <summary>
  /// Signiert <paramref name="files"/> mit dem eigenen Schlüssel und prüft sofort gegen die
  /// vertrauten Schlüssel. Ohne Schlüssel bleibt alles unsigniert (Meldung, kein Fehler).
  /// </summary>
  /// <returns>Die schwächste Signatur der Dateien; <c>null</c> ohne Schlüssel.</returns>
  private static async Task<ComponentSignature?> SignWithUserKey(CommandContext context, string what, IReadOnlyList<string> files)
  {
    var events = context.Events;
    if (FileSigning.Create(context.Config.Signing) is not { } signer)
    {
      await events.Emit(events.Err($"{what}: unsigniert — {NoSigningKeyHint}."));
      return null;
    }

    foreach (var file in files)
    {
      FileSigning.RemoveSignatures(file);
      await Task.Run(() => signer.ClearSign(file), context.CancellationToken);
    }

    var signature = SignatureOf(files);
    await events.Emit(events.Std($"{what}: {signature.Describe()}"));
    if (signature.Trust == ComponentTrust.UnknownKey)
    {
      await events.Emit(events.Err($"  Der Schlüssel ({signer.Label}) ist nicht vertraut — öffentlichen Teil aufnehmen: cb key-trust -f <öffentlicher Schlüssel>."));
    }

    return signature;
  }

  /// <summary>Die schwächste Signatur der Dateien gegen CoreBridge- und Benutzer-Schlüssel.</summary>
  private static ComponentSignature SignatureOf(IReadOnlyList<string> files)
  {
    var trusted = ComponentTrustBundle();
    var now = DateTime.UtcNow;
    return files
        .Select(file => ComponentSignature.From(SettingsSignatures.VerifyFile(file, trusted, now)))
        .Aggregate(ComponentSignature.Worst);
  }

  private static bool IsTrusted(ComponentSignature signature) =>
      signature.Trust is ComponentTrust.CoreBridge or ComponentTrust.UserTrusted;

  /// <summary>Die Dateien eines Profils, die zusammen signiert sind: Profil-JSON und, falls vorhanden, die cloud-init.</summary>
  private static IReadOnlyList<string> ProfileFiles(string name) =>
      new[] { InitStore.ConfigFile(name), InitStore.CloudInitFile(name) }.Where(File.Exists).ToList();

  /// <summary>
  /// Was eine VM formt, vor dem Start prüfen: ungültig (geändert nach dem Signieren, Schlüssel
  /// abgelaufen) bricht mit Exit 3 ab; unsigniert oder unbekannter Schlüssel startet mit lauter Warnung.
  /// </summary>
  private static async Task<ComponentSignature> CheckLaunchInput(CommandContext context, string what, IReadOnlyList<string> files)
  {
    var events = context.Events;
    var signature = SignatureOf(files);
    await events.Emit(events.Std($"{what}: {signature.Describe()}"));
    if (signature.Trust == ComponentTrust.Invalid)
    {
      throw new ProgramExecutionException(
          3,
          $"{what} ist ungültig signiert: {signature.Reason} ({signature.File}). Nichts wurde angelegt. "
              + "Neu erzeugen (init-configure -f) oder neu signieren (cb sign -f <datei>).");
    }

    if (signature.NeedsWarning)
    {
      const string Rule = "════════════════════════════════════════════════════════════════════";
      foreach (var line in new[]
               {
                 "╔" + Rule,
                 $"║ WARNUNG: {what} ist nicht vertrauenswürdig signiert — die cloud-init läuft als root im Gast.",
                 $"║   {signature.Describe()}",
                 $"║   Datei: {signature.File}",
                 "║ Signieren: cb sign -f <datei> bzw. init-configure -f (mit Signing:KeyFile oder Signing:GpgKey).",
                 "╚" + Rule
               })
      {
        await events.Emit(events.Err(line));
      }
    }

    return signature;
  }

  /// <summary><c>cb sign</c>: beliebige Dateien (eigene cloud-init, alte Profile) mit dem eigenen Schlüssel signieren.</summary>
  private static async Task RunSign(CommandContext context, SignOptions options)
  {
    var files = options.Files.Select(file => Path.GetFullPath(file.Trim())).Where(file => file.Length > 0).ToList();
    var missing = files.Where(file => !File.Exists(file)).ToList();
    if (missing.Count > 0)
    {
      throw new ProgramExecutionException(1, $"Datei nicht gefunden: {string.Join(", ", missing)}");
    }

    if (FileSigning.Create(context.Config.Signing) is null)
    {
      throw new ProgramExecutionException(1, $"Nichts signiert — {NoSigningKeyHint}.");
    }

    foreach (var file in files)
    {
      await SignWithUserKey(context, file, [file]);
    }
  }
}
