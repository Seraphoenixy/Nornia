namespace Nornia.Desktop.Configuration;

/// <summary>Rejects stale asynchronous applications while allowing independent settings contexts.</summary>
public sealed class RevisionGate
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _latest = new(StringComparer.OrdinalIgnoreCase);
    public bool TryAccept(SettingsSnapshot snapshot)
    {
        var key = $"{snapshot.Context.NormalizedWorkspacePath ?? "<user>"}|{snapshot.Context.LanguageId ?? "<all>"}";
        lock (_gate)
        {
            if (_latest.TryGetValue(key, out var revision) && revision > snapshot.Revision) return false;
            _latest[key] = snapshot.Revision;
            return true;
        }
    }
}
