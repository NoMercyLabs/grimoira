namespace Grimora.Server.Data;

/// <summary>
/// The ids (<c>X-Grimora-Call</c>) of the last <see cref="Capacity"/> calls this service started, recorded at request
/// entry before any work, for <see cref="CleanExitRecord"/>. When older ids have been dropped, <see cref="Wrapped"/>
/// is set and the record says from when on the ring is complete (the start time of the oldest id it kept).
/// </summary>
public sealed class CallRing(Func<DateTime>? now = null)
{
    public const int Capacity = 256;

    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    private readonly Queue<(string Id, DateTime At)> _calls = new();
    private readonly Lock _gate = new();
    private bool _wrapped;

    public void Record(string callId)
    {
        lock (_gate)
        {
            if (_calls.Count == Capacity)
            {
                _calls.Dequeue();
                _wrapped = true;
            }
            _calls.Enqueue((callId, _now()));
        }
    }

    /// <summary>The ids kept, whether older ones were dropped, and the start time of the oldest id kept.</summary>
    public (IReadOnlyList<string> Ids, bool Wrapped, DateTime OldestKeptStartUtc) Snapshot()
    {
        lock (_gate)
            return ([.. _calls.Select(c => c.Id)], _wrapped, _calls.Count > 0 ? _calls.Peek().At : DateTime.MaxValue);
    }
}
