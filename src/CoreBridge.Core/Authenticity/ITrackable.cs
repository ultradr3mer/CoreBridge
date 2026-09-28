namespace CoreBridge.Core.Authenticity
{
  /// <summary>
  /// Markierungs-Interface für Objekte, deren Eigenschaftsänderungen überwacht werden sollen.
  /// Es definiert keine eigenen Member, sondern signalisiert, dass die implementierende (z.B. <see cref="EinsatzDaten"/>  Klasse für das Diff-Management 
  /// geeignet ist. Über zugehörige Erweiterungsmethoden können Hash-basierte Snapshots (Mementos) des Objektzustands 
  /// erstellt und später verglichen werden, um geänderte Felder zu identifizieren.
  /// </summary>
  public interface ITrackable { }
}
