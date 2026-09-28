namespace CoreBridge.Core.Authenticity
{
  using System.Collections.Generic;

  /// <summary>
  /// Datenobjekt für das <see cref="ITrackable"/> Interface und seine Extensions (<see cref="TrackableExtensions"/>).
  /// </summary>
  public class HashSnapshot
  {
    internal Dictionary<string, int> Hashes { get; } = new Dictionary<string, int>();
  }
}