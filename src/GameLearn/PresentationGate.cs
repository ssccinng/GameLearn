namespace GameLearn;

/// <summary>Independent interactions may overlap; refreshing resumes only after the last one ends.</summary>
public sealed class PresentationGate
{
    private readonly HashSet<object> owners = new();
    public bool IsHeld => owners.Count > 0;
    public event Action? Changed;
    public void SetHeld(object owner, bool held)
    {
        var before = IsHeld;
        if (held) owners.Add(owner); else owners.Remove(owner);
        if (before != IsHeld) Changed?.Invoke();
    }
    public IDisposable Hold()
    {
        var owner = new object(); SetHeld(owner, true); return new Lease(this, owner);
    }
    private sealed class Lease(PresentationGate gate, object owner) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (disposed) return; disposed = true; gate.SetHeld(owner, false); }
    }
}
