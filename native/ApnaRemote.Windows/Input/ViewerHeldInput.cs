using ApnaRemote.Core;

namespace ApnaRemote.Windows.Input;

/// <summary>Dispatcher-owned ledger of transitions successfully queued to the current host.</summary>
internal sealed class ViewerHeldInput
{
    private readonly HashSet<HeldInput> _held = [];
    public void Sent(InputKind kind, int code)
    {
        if (kind is InputKind.KeyDown or InputKind.ButtonDown) _held.Add(new(kind, code));
        if (kind is InputKind.KeyUp) _held.Remove(new(InputKind.KeyDown, code));
        if (kind is InputKind.ButtonUp) _held.Remove(new(InputKind.ButtonDown, code));
    }
    public HeldInput[] TakeAll() { var result = _held.ToArray(); _held.Clear(); return result; }
    public void Clear() => _held.Clear();
}
