namespace CoreBridge.Core.Diff
{
  internal class Tracked<T> where T : ITrackable
  {
    public required T Item { get; set; }
    public required HashSnapshot Snapshot { get; set; }
  }
}
