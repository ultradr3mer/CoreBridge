namespace CoreBridge.Core.Authenticity
{
  internal class Tracked<T> where T : ITrackable
  {
    public required T Item { get; set; }
    public required HashSnapshot Snapshot { get; set; }
  }
}
