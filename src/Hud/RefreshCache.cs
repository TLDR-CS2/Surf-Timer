namespace SurfTimer.Hud;

// Game-thread owned. Empty results and failures have deadlines just like successful lookups.
public sealed class RefreshCache<TKey, TValue> where TKey : notnull where TValue : class
{
    private readonly Dictionary<TKey, (TValue? Value, DateTimeOffset RetryAt)> _values = [];
    private readonly Dictionary<TKey, long> _requests = [];
    private long _generation;
    public TValue? Get(TKey key) => _values.TryGetValue(key, out var entry) ? entry.Value : null;
    public bool TryBegin(TKey key, DateTimeOffset now, out long generation)
    {
        generation = _generation;
        if (_requests.ContainsKey(key) || (_values.TryGetValue(key, out var entry) && now < entry.RetryAt)) return false;
        _requests[key] = generation;
        return true;
    }
    public void Complete(TKey key, long generation, TValue? value, DateTimeOffset retryAt)
    {
        if (!_requests.TryGetValue(key, out var active) || active != generation) return;
        _requests.Remove(key);
        _values[key] = (value, retryAt);
    }
    public void Invalidate(Func<TKey, bool> predicate)
    {
        _generation++;
        foreach (var key in _values.Keys.Where(predicate).ToArray()) _values.Remove(key);
        foreach (var key in _requests.Keys.Where(predicate).ToArray()) _requests.Remove(key);
    }
}
