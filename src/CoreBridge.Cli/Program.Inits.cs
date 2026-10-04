using CoreBridge.Interaction;
using CoreBridge.Core.Authenticity;
using CoreBridge.Core.Inits;
using CoreBridge.Core.Vm;

namespace CoreBridge.Cli;

public static partial class Program
{
  private static async Task RunInitConfigure(CommandContext context, InitConfigureOptions options)
  {
    var events = context.Events;
    var config = context.Config;
    var components = options.Components.Split(',', StringSplitOptions.RemoveEmptyEntries);
    var resources = ParseResources(options.Cpus, options.Memory, options.Disk);
    await events.Emit(events.Std($"Erzeuge Profil '{options.Name}' ({string.Join(", ", components)}) ..."));
    await ReportComponentTrust(context, components);
    var inits = new InitService(config.Inits);
    var result = await Task.Run(
        () => inits.Configure(
            options.Name,
            components,
            force: options.Force,
            runtimeImage: options.RuntimeImage,
            resources: resources.IsEmpty ? null : resources),
        context.CancellationToken);

    await events.Emit(events.Std($"Profil '{result.Name}' erzeugt."));
    await events.Emit(events.Std($"  Config:       {result.ConfigPath}"));
    await events.Emit(events.Std($"  Runtime-Image: {result.RuntimeImage}"));
    if (result.Resources is { IsEmpty: false } defaults)
    {
      await events.Emit(events.Std($"  Ressourcen:   {defaults} (Vorgabe für vm-create)"));
    }

    await events.Emit(events.Std($"  Snaps:        {(result.Snaps.Length == 0 ? "keine" : string.Join(", ", result.Snaps))}"));
    await events.Emit(events.Std(result.CloudInitPath is null
        ? "  cloud-init: nicht nötig (keine Gast-Provisionierung)."
        : $"  cloud-init: {result.CloudInitPath}"));

    // Die Profilsignatur bürgt für die eingebetteten Skripte — nur, wenn jede Komponente selbst vertraut ist.
    var untrusted = components
        .Select(name => name.Trim())
        .Where(name => context.Catalog.OriginOf(name) is not { } origin || !IsTrusted(origin.Signature))
        .ToList();
    if (untrusted.Count > 0)
    {
      await events.Emit(events.Err(
          $"Profil-Signatur: unsigniert — nicht vertrauenswürdig signiert: {string.Join(", ", untrusted)}. Erst die Komponenten signieren, dann init-configure -f."));
    }
    else
    {
      await SignWithUserKey(context, "Profil-Signatur", ProfileFiles(result.Name));
    }

    await events.Emit(events.Std(string.Empty));
    await events.Emit(events.Std($"VM anlegen: cb vm-create -n <vm> --profile {result.Name}"));
  }

  private static async Task RunInitList(CommandContext context)
  {
    var events = context.Events;
    var entries = await Task.Run(() => new InitService(context.Config.Inits).List(), context.CancellationToken);
    if (entries.Count == 0)
    {
      await events.Emit(events.Std("Keine konfigurierten Profile."));
      return;
    }

    await events.Emit(events.Std($"{"Name",-20} {"Komponenten",-28} {"Snaps",-20} Ressourcen"));
    foreach (var entry in entries)
    {
      var resources = entry.Resources is { IsEmpty: false } r ? r.ToString() : "-";
      await events.Emit(events.Std($"{entry.Name,-20} {string.Join(",", entry.Components),-28} {(entry.Snaps.Length == 0 ? "-" : string.Join(",", entry.Snaps)),-20} {resources}"));
    }
  }

  private static async Task RunInitDelete(CommandContext context, InitDeleteOptions options)
  {
    var result = await Task.Run(
        () => new InitService(context.Config.Inits).Delete(options.Name),
        context.CancellationToken);
    await context.Events.Emit(context.Events.Std($"Konfiguration gelöscht: {result.ProfileDir}"));
  }

  private static async Task RunComponentList(CommandContext context, ComponentListOptions options)
  {
    var events = context.Events;
    var entries = context.Catalog.Entries();
    if (!string.IsNullOrWhiteSpace(options.Name))
    {
      var entry = entries.FirstOrDefault(e => string.Equals(e.Name, options.Name, StringComparison.OrdinalIgnoreCase));
      if (entry.Name is null)
      {
        throw new ProgramExecutionException(1, $"Unbekannte Komponente '{options.Name}'. Übersicht: `cb component-list`.");
      }

      await events.Emit(events.Std(InitComponentStore.ToJson(entry.Component)));
      await events.Emit(events.Err($"Herkunft: {entry.Origin.File} — {entry.Origin.Signature.Describe()}"));
      return;
    }

    if (entries.Count == 0)
    {
      await events.Emit(events.Std("Keine Komponenten konfiguriert."));
      return;
    }

    await events.Emit(events.Std($"{"Name",-16} {"Herkunft",-12} {"Signatur",-22} {"Snap",-18} {"Script",-7} Pakete"));
    foreach (var (name, component, origin) in entries)
    {
      var script = !string.IsNullOrWhiteSpace(component.ScriptContent) ? "inline" : string.IsNullOrWhiteSpace(component.Script) ? "-" : "ja";
      await events.Emit(events.Std(
          $"{name,-16} {(origin.Source == ComponentSource.User ? "benutzer" : "mitgeliefert"),-12} {origin.Signature.Label,-22} "
          + $"{component.Snap ?? "-",-18} {script,-7} "
          + (component.Packages.Length == 0 ? "-" : string.Join(",", component.Packages))));
    }

    await events.Emit(events.Std(string.Empty));
    await events.Emit(events.Std($"Benutzerkomponenten: {InitStore.UserSettingsFile} und Bundles {Path.Combine(InitStore.Root, "*" + ComponentCatalog.BundleExtension)}"));
    var warn = entries.Where(entry => entry.Origin.Signature.Trust is not (ComponentTrust.CoreBridge or ComponentTrust.UserTrusted)).ToList();
    if (warn.Count > 0)
    {
      await events.Emit(events.Std($"Nicht vertrauenswürdig signiert: {string.Join(", ", warn.Select(entry => entry.Name))} — Einzelheiten: cb settings-verify."));
    }
  }

  /// <summary>Schreibt eine Komponente als Bundle (Definition plus Skript inline), bereit zum Signieren.</summary>
  private static async Task RunComponentBundle(CommandContext context, ComponentBundleOptions options)
  {
    var entry = context.Catalog.Entries().FirstOrDefault(e => string.Equals(e.Name, options.Name, StringComparison.OrdinalIgnoreCase));
    if (entry.Name is null)
    {
      throw new ProgramExecutionException(1, $"Unbekannte Komponente '{options.Name}'. Übersicht: `cb component-list`.");
    }

    var output = Path.GetFullPath(string.IsNullOrWhiteSpace(options.Output) ? entry.Name + ComponentCatalog.BundleExtension : options.Output);
    if (!output.EndsWith(ComponentCatalog.BundleExtension, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(ComponentCatalog.BundleName(output), entry.Name, StringComparison.OrdinalIgnoreCase))
    {
      throw new ProgramExecutionException(1, $"Ein Bundle heißt {entry.Name}{ComponentCatalog.BundleExtension} — der Name gehört zur Signatur.");
    }

    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, InitComponentStore.Bundle(entry.Name, entry.Component), context.CancellationToken);
    await context.Events.Emit(context.Events.Std($"Bundle geschrieben: {output}"));
    await context.Events.Emit(context.Events.Std($"Signieren: cb component-add --bundle {output} signiert mit dem eigenen Schlüssel (oder cb sign -f {output})."));
    await context.Events.Emit(context.Events.Std($"Übernehmen: cb component-add --bundle {output}"));
  }

  private static async Task RunComponentAdd(CommandContext context, ComponentAddOptions options)
  {
    var events = context.Events;
    if (options.Bundle is { } bundle)
    {
      await AddBundle(context, options, bundle);
      return;
    }

    if (string.IsNullOrWhiteSpace(options.Name))
    {
      throw new ProgramExecutionException(1, "Name fehlt: -n <name> (bei --bundle steht er im Bundle).");
    }

    var (value, format) = (options.Json, options.Yaml) switch
    {
      ({ } json, null) => (json, ComponentFormat.Json),
      (null, { } yaml) => (yaml, ComponentFormat.Yaml),
      _ => throw new ProgramExecutionException(1, "Genau eine Definition angeben: --json, --yaml (Dateipfad oder Text) oder --bundle.")
    };

    var component = InitComponentStore.ParseArgument(value, format);
    var replace = options.Force;
    if (!replace && context.Catalog.OriginOf(options.Name) is { Source: ComponentSource.User })
    {
      if (!await context.ConfirmAsync($"Komponente '{options.Name}' existiert bereits — ersetzen?"))
      {
        throw new ProgramExecutionException(1, $"Abgebrochen — Komponente '{options.Name}' bleibt unverändert (--force ersetzt ohne Rückfrage).");
      }

      replace = true;
    }

    var (saved, file) = InitComponentStore.AddAsBundle(context.Catalog, InitStore.Root, InitStore.UserSettingsFile, options.Name, component, replace);
    await events.Emit(events.Std($"Komponente '{options.Name}' gespeichert: {file} (Skript inline)"));
    await events.Emit(events.Std(InitComponentStore.ToJson(saved)));
    await SignWithUserKey(context, "Signatur", [file]);
    await events.Emit(events.Std(string.Empty));
    await events.Emit(events.Std($"Verwenden: cb init-configure -n <profil> -c {options.Name} oder cb vm-install -n <vm> -c {options.Name}"));
    if (replace)
    {
      await events.Emit(events.Std("Bestehende Profile behalten ihre gerenderte cloud-init — bei Bedarf `cb init-configure -f` erneut ausführen."));
    }
  }

  /// <summary>Übernimmt ein Bundle unverändert (samt Signatur) in den Benutzerordner.</summary>
  private static async Task AddBundle(CommandContext context, ComponentAddOptions options, string bundle)
  {
    var events = context.Events;
    var name = ComponentCatalog.BundleName(bundle);
    if (!string.IsNullOrWhiteSpace(options.Name) && !string.Equals(options.Name, name, StringComparison.OrdinalIgnoreCase))
    {
      throw new ProgramExecutionException(1, $"Das Bundle heißt '{name}', nicht '{options.Name}' — der Name gehört zur Signatur.");
    }

    var replace = options.Force;
    var target = Path.Combine(InitStore.Root, name + ComponentCatalog.BundleExtension);
    if (!replace && File.Exists(target))
    {
      if (!await context.ConfirmAsync($"Bundle '{name}' existiert bereits — ersetzen?"))
      {
        throw new ProgramExecutionException(1, $"Abgebrochen — Komponente '{name}' bleibt unverändert (--force ersetzt ohne Rückfrage).");
      }

      replace = true;
    }

    var (added, _, file) = InitComponentStore.AddBundle(context.Catalog, InitStore.Root, bundle, replace);
    var signature = ComponentSignature.From(SettingsSignatures.VerifyFile(file, ComponentTrustBundle(), DateTime.UtcNow));
    await events.Emit(events.Std($"Komponente '{added}' übernommen: {file}"));
    if (signature.Trust == ComponentTrust.Unsigned && FileSigning.Create(context.Config.Signing) is not null)
    {
      // Wer ein unsigniertes Bundle aufnimmt, bürgt dafür; eine fremde Signatur bleibt, wie sie ist.
      signature = await SignWithUserKey(context, "  Signatur", [file]) ?? signature;
    }
    else
    {
      await events.Emit(events.Std($"  Signatur: {signature.Describe()}"));
    }
    if (signature.Trust == ComponentTrust.Invalid)
    {
      InitComponentStore.RemoveBundle(file);
      throw new ProgramExecutionException(3, $"Bundle '{added}' ist ungültig signiert ({signature.Reason}) — nicht übernommen.");
    }

    if (signature.NeedsWarning)
    {
      foreach (var line in WarningBlock([(added, new ComponentOrigin(file, ComponentSource.User, signature))]))
      {
        await events.Emit(events.Err(line));
      }
    }

    await events.Emit(events.Std($"Verwenden: cb init-configure -n <profil> -c {added} oder cb vm-install -n <vm> -c {added}"));
  }

  private static async Task RunComponentRemove(CommandContext context, ComponentRemoveOptions options)
  {
    if (context.Catalog.OriginOf(options.Name) is { Source: ComponentSource.User } origin
        && origin.File.EndsWith(ComponentCatalog.BundleExtension, StringComparison.OrdinalIgnoreCase))
    {
      InitComponentStore.RemoveBundle(origin.File);
      await context.Events.Emit(context.Events.Std($"Komponente '{options.Name}' entfernt: {origin.File}"));
      await context.Events.Emit(context.Events.Std("Bestehende Profile behalten ihre gerenderte cloud-init; neue init-configure/vm-install kennen die Komponente nicht mehr."));
      return;
    }

    if (context.Catalog.OriginOf(options.Name) is { Source: ComponentSource.BuiltIn } shipped)
    {
      throw new ProgramExecutionException(1, $"Komponente '{options.Name}' ist mitgeliefert ({shipped.File}) und lässt sich nicht entfernen.");
    }

    InitComponentStore.Remove(context.Config.Inits, InitStore.UserSettingsFile, options.Name);
    await context.Events.Emit(context.Events.Std($"Komponente '{options.Name}' entfernt: {InitStore.UserSettingsFile}"));
    await context.Events.Emit(context.Events.Std("Bestehende Profile behalten ihre gerenderte cloud-init; neue init-configure/vm-install kennen die Komponente nicht mehr."));
  }
}
