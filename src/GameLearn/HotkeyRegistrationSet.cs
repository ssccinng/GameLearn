namespace GameLearn;

public interface IHotkeyRegistrar
{
    // Zero means success; otherwise return the Win32 error code.
    int Register(int id, uint modifiers, uint key);
    void Unregister(int id);
}

public record HotkeyRegistration(int Id, string Requested, string? Active, int Error)
{
    public bool MatchesRequest => Error == 0 && Active is not null;
}

/// <summary>A conflict affects one action only, never other registered shortcuts.</summary>
public sealed class HotkeyRegistrationSet(IHotkeyRegistrar registrar) : IDisposable
{
    private Dictionary<int, string> registered = new();
    public IReadOnlyDictionary<int, string> Active => registered;

    public IReadOnlyList<HotkeyRegistration> Apply(IReadOnlyDictionary<int, string> requested)
    {
        // Validate before releasing any working bindings.
        var parsed = requested.ToDictionary(p => p.Key, p => HotkeyManager.Parse(p.Value));
        if (parsed.Values.Distinct().Count() != parsed.Count) throw new ArgumentException("三个快捷键不能重复。");
        var previous = registered;
        foreach (var id in previous.Keys) registrar.Unregister(id);
        registered = new();
        var errors = new Dictionary<int, int>();
        foreach (var pair in parsed)
        {
            var error = registrar.Register(pair.Key, pair.Value.Modifiers, pair.Value.Key);
            errors[pair.Key] = error;
            if (error == 0) registered[pair.Key] = requested[pair.Key];
        }
        // Restore only failed actions, after all requested bindings have had a chance.
        foreach (var id in requested.Keys.Where(id => errors[id] != 0))
        {
            if (!previous.TryGetValue(id, out var old)) continue;
            var key = HotkeyManager.Parse(old);
            if (registrar.Register(id, key.Modifiers, key.Key) == 0) registered[id] = old;
        }
        return requested.Select(p => new HotkeyRegistration(p.Key, p.Value, registered.GetValueOrDefault(p.Key), errors[p.Key])).ToArray();
    }
    public void Dispose()
    {
        foreach (var id in registered.Keys) registrar.Unregister(id);
        registered.Clear();
    }
}
