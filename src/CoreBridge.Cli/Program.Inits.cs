using CoreBridge.Interaction;
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
    var entries = InitComponentStore.List(context.Config.Inits, InitStore.UserSettingsFile);
    if (!string.IsNullOrWhiteSpace(options.Name))
    {
      var entry = entries.FirstOrDefault(e => string.Equals(e.Name, options.Name, StringComparison.OrdinalIgnoreCase))
          ?? throw new ProgramExecutionException(1, $"Unbekannte Komponente '{options.Name}'. Übersicht: `cb component-list`.");
      await events.Emit(events.Std(InitComponentStore.ToJson(entry.Component)));
      return;
    }

    if (entries.Count == 0)
    {
      await events.Emit(events.Std("Keine Komponenten konfiguriert."));
      return;
    }

    await events.Emit(events.Std($"{"Name",-16} {"Herkunft",-12} {"Snap",-18} {"Script",-6} Pakete"));
    foreach (var entry in entries)
    {
      var component = entry.Component;
      await events.Emit(events.Std(
          $"{entry.Name,-16} {(entry.Source == ComponentSource.User ? "benutzer" : "mitgeliefert"),-12} "
          + $"{component.Snap ?? "-",-18} {(string.IsNullOrWhiteSpace(component.Script) ? "-" : "ja"),-6} "
          + (component.Packages.Length == 0 ? "-" : string.Join(",", component.Packages))));
    }

    await events.Emit(events.Std(string.Empty));
    await events.Emit(events.Std($"Benutzerkomponenten: {InitStore.UserSettingsFile}"));
  }

  private static async Task RunComponentAdd(CommandContext context, ComponentAddOptions options)
  {
    var events = context.Events;
    var (value, format) = (options.Json, options.Yaml) switch
    {
      ({ } json, null) => (json, ComponentFormat.Json),
      (null, { } yaml) => (yaml, ComponentFormat.Yaml),
      _ => throw new ProgramExecutionException(1, "Genau eine Definition angeben: --json oder --yaml (Dateipfad oder Text).")
    };

    var component = InitComponentStore.ParseArgument(value, format);
    var userFile = InitStore.UserSettingsFile;
    var replace = options.Force;
    if (!replace && InitComponentStore.ReadUser(userFile).ContainsKey(options.Name))
    {
      if (!await context.ConfirmAsync($"Komponente '{options.Name}' existiert bereits — ersetzen?"))
      {
        throw new ProgramExecutionException(1, $"Abgebrochen — Komponente '{options.Name}' bleibt unverändert (--force ersetzt ohne Rückfrage).");
      }

      replace = true;
    }

    var saved = InitComponentStore.Add(context.Config.Inits, userFile, options.Name, component, replace);
    await events.Emit(events.Std($"Komponente '{options.Name}' gespeichert: {userFile}"));
    await events.Emit(events.Std(InitComponentStore.ToJson(saved)));
    await events.Emit(events.Std(string.Empty));
    await events.Emit(events.Std($"Verwenden: cb init-configure -n <profil> -c {options.Name} oder cb vm-install -n <vm> -c {options.Name}"));
    if (replace)
    {
      await events.Emit(events.Std("Bestehende Profile behalten ihre gerenderte cloud-init — bei Bedarf `cb init-configure -f` erneut ausführen."));
    }
  }

  private static async Task RunComponentRemove(CommandContext context, ComponentRemoveOptions options)
  {
    InitComponentStore.Remove(context.Config.Inits, InitStore.UserSettingsFile, options.Name);
    await context.Events.Emit(context.Events.Std($"Komponente '{options.Name}' entfernt: {InitStore.UserSettingsFile}"));
    await context.Events.Emit(context.Events.Std("Bestehende Profile behalten ihre gerenderte cloud-init; neue init-configure/vm-install kennen die Komponente nicht mehr."));
  }
}
