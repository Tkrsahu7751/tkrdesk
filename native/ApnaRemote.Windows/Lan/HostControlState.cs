using ApnaRemote.Core;

namespace ApnaRemote.Windows.Lan;

/// <summary>Local permission decisions, plus acknowledgement before renewed input.</summary>
internal sealed class HostControlState(HostSessionGate gate)
{
    private readonly object _sync = new();
    private int _acknowledgedEpoch = 1; // Initial Approved is explicit and revision-negotiated.
    public SessionSnapshot Snapshot => gate.Snapshot;
    public bool Change(bool allow) { lock (_sync) return gate.ChangeControlLocally(allow); }
    public void Pause() { lock (_sync) gate.PauseLocally(); }
    public void Acknowledge(int epoch, bool control, bool paused)
    {
        lock (_sync)
        {
            var state = gate.Snapshot;
            if (epoch == state.PermissionEpoch && control == (state.Phase == SessionPhase.Controlling) && paused == (state.Phase == SessionPhase.Paused))
                _acknowledgedEpoch = epoch;
        }
    }
    public CommandResult Apply(string peer, InputCommand command)
    {
        lock (_sync)
        {
            if (command.PermissionEpoch != _acknowledgedEpoch)
                return CommandResult.Reject("Permission update not acknowledged.");
            return gate.TryApply(peer, command);
        }
    }
}
