namespace ApnaRemote.Core;

/// <summary>
/// Host-side permission and input gate, not a transport/authentication implementation.
/// The transport must close on a rejected session and call Tick at least once per second.
/// All permission changes are local-host operations; never expose them as remote commands.
/// </summary>
public sealed class HostSessionGate
{
    public static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(10);
    private readonly object _sync = new();
    private readonly TimeProvider _clock;
    private readonly IInputSink _sink;
    private readonly HashSet<HeldInput> _held = [];
    private SessionPhase _phase;
    private PeerIdentity? _peer;
    private Guid? _sessionId;
    private DateTimeOffset? _expiresAt;
    private long _approvalStarted, _lastActivity, _rateStarted, _lastSequence;
    private int _epoch, _rateCount;
    private DisplayBounds _display;
    private string? _displayId;
    private string _status = "Receiving is disabled.";
    private bool _cleanupFault;

    public HostSessionGate(IInputSink sink, TimeProvider? clock = null)
    { _sink = sink; _clock = clock ?? TimeProvider.System; }

    public SessionSnapshot Snapshot
    {
        get { lock (_sync) return new(_phase, _sessionId, _peer?.DisplayName, _peer?.Id,
            _epoch, _displayId, _expiresAt, _status, _cleanupFault); }
    }

    public void EnableReceiving()
    {
        lock (_sync)
        {
            if (_cleanupFault) throw new InvalidOperationException("Input cleanup failed. Restart after investigating.");
            if (_phase != SessionPhase.Disabled) return;
            _phase = SessionPhase.Listening;
            _status = "Ready for an authenticated request.";
        }
    }

    public void DisableReceiving()
    {
        lock (_sync) { EndInternal("Receiving is disabled."); _phase = SessionPhase.Disabled; }
    }

    public Guid RequestFromAuthenticatedPeer(PeerIdentity peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (!ValidIdentityPart(peer.Id, 256) || !ValidIdentityPart(peer.DisplayName, 64))
            throw new ArgumentException("Invalid peer identity.", nameof(peer));
        lock (_sync)
        {
            TickInternal();
            if (_phase != SessionPhase.Listening) throw new InvalidOperationException("Host is unavailable.");
            _peer = peer;
            _sessionId = Guid.NewGuid();
            _epoch = 0;
            _lastSequence = 0;
            _approvalStarted = _clock.GetTimestamp();
            _expiresAt = _clock.GetUtcNow() + ApprovalTimeout;
            _phase = SessionPhase.AwaitingApproval;
            _status = "Waiting for local host approval. No screen or input access.";
            return _sessionId.Value;
        }
    }

    public bool ApproveLocally(Guid requestId, string displayId, DisplayBounds display, bool allowControl = false)
    {
        PointerMapping.Validate(display);
        if (!ValidIdentityPart(displayId, 256)) throw new ArgumentException("Select a display.", nameof(displayId));
        lock (_sync)
        {
            TickInternal();
            if (_phase != SessionPhase.AwaitingApproval || _sessionId != requestId) return false;
            _display = display;
            _displayId = displayId;
            _epoch = 1;
            _expiresAt = null;
            _lastActivity = _rateStarted = _clock.GetTimestamp();
            _rateCount = 0;
            _phase = allowControl ? SessionPhase.Controlling : SessionPhase.Viewing;
            _status = allowControl ? "Screen and input approved." : "View-only approved.";
            return true;
        }
    }

    public void RejectLocally(Guid requestId)
    {
        lock (_sync)
            if (_phase == SessionPhase.AwaitingApproval && _sessionId == requestId)
                EndInternal("Host rejected the request.");
    }

    public bool ChangeControlLocally(bool allow)
    {
        lock (_sync)
        {
            TickInternal();
            if (!IsActive()) return false;
            // Epoch rotation invalidates input queued before this local permission decision.
            if (!ReleaseHeld()) { EndInternal("Input cleanup failed; session stopped."); return false; }
            _epoch = checked(_epoch + 1);
            _lastSequence = 0;
            _phase = allow ? SessionPhase.Controlling : SessionPhase.Viewing;
            _status = allow ? "Remote control approved locally." : "Remote input revoked; view-only continues.";
            return true;
        }
    }

    public void PauseLocally()
    {
        lock (_sync)
        {
            if (!IsActive()) return;
            if (!ReleaseHeld()) { EndInternal("Input cleanup failed; session stopped."); return; }
            _epoch = checked(_epoch + 1);
            _lastSequence = 0;
            _phase = SessionPhase.Paused;
            _status = "Input paused by host. Screen sharing remains approved.";
        }
    }

    public bool CanShareFrames(Guid sessionId, string authenticatedPeerId)
    {
        lock (_sync) { TickInternal(); return Matches(sessionId, authenticatedPeerId) && IsActive(); }
    }

    public bool Heartbeat(Guid sessionId, string authenticatedPeerId)
    {
        lock (_sync)
        {
            TickInternal();
            if (!IsActive() || !Matches(sessionId, authenticatedPeerId)) return false;
            _lastActivity = _clock.GetTimestamp();
            return true;
        }
    }

    public CommandResult TryApply(string authenticatedPeerId, InputCommand command)
    {
        lock (_sync)
        {
            TickInternal();
            if (!Matches(command.SessionId, authenticatedPeerId)) return CommandResult.Reject("Unknown session or peer.");
            if (_phase != SessionPhase.Controlling) return CommandResult.Reject("Input is not approved.");
            if (command.PermissionEpoch != _epoch) return CommandResult.Reject("Stale permission epoch.");
            if (!InputValidation.IsValid(command)) return CommandResult.Reject("Invalid input payload.");
            if (command.Sequence <= _lastSequence) return CommandResult.Reject("Replayed or out-of-order input.");
            if (_clock.GetElapsedTime(_rateStarted) >= TimeSpan.FromSeconds(1))
            { _rateStarted = _clock.GetTimestamp(); _rateCount = 0; }
            if (++_rateCount > 500) { EndInternal("Input rate limit exceeded."); return CommandResult.Reject("Rate limit exceeded."); }

            var held = command.Kind switch
            {
                InputKind.KeyDown or InputKind.KeyUp => new HeldInput(InputKind.KeyDown, command.Code),
                InputKind.ButtonDown or InputKind.ButtonUp => new HeldInput(InputKind.ButtonDown, command.Code),
                _ => (HeldInput?)null
            };
            bool down = command.Kind is InputKind.KeyDown or InputKind.ButtonDown;
            bool up = command.Kind is InputKind.KeyUp or InputKind.ButtonUp;
            if (up && held.HasValue && !_held.Contains(held.Value)) return CommandResult.Reject("Input was not held.");
            if (down && held.HasValue && !_held.Contains(held.Value) && _held.Count >= 32)
            { EndInternal("Too many held inputs."); return CommandResult.Reject("Held-input limit exceeded."); }

            // Track before injecting: even an adapter that fails after a partial injection needs release.
            if (down && held.HasValue) _held.Add(held.Value);
            try { _sink.Apply(command, _display); }
            catch (Exception)
            {
                EndInternal("Input adapter failed; session stopped.");
                return CommandResult.Reject("Input adapter failed.");
            }
            if (up && held.HasValue) _held.Remove(held.Value);
            _lastSequence = command.Sequence;
            _lastActivity = _clock.GetTimestamp();
            return CommandResult.Ok;
        }
    }

    public void Disconnect(string reason = "Session ended.")
    { lock (_sync) EndInternal(reason); }

    public void Tick() { lock (_sync) TickInternal(); }

    private void TickInternal()
    {
        if (_phase == SessionPhase.AwaitingApproval && _clock.GetElapsedTime(_approvalStarted) >= ApprovalTimeout)
            EndInternal("Approval request expired.");
        else if (IsActive() && _clock.GetElapsedTime(_lastActivity) >= IdleTimeout)
            EndInternal("Connection heartbeat timed out.");
    }

    private bool Matches(Guid id, string peer) => _sessionId == id && _peer?.Id == peer;
    private bool IsActive() => _phase is SessionPhase.Viewing or SessionPhase.Controlling or SessionPhase.Paused;
    private static bool ValidIdentityPart(string? value, int limit) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= limit && !value.Any(char.IsControl);

    private bool ReleaseHeld()
    {
        bool success = true;
        foreach (HeldInput held in _held.ToArray())
        {
            try { _sink.Release(held); _held.Remove(held); }
            catch (Exception) { success = false; }
        }
        if (!success) _cleanupFault = true;
        return success;
    }

    private void EndInternal(string reason)
    {
        ReleaseHeld();
        _phase = _cleanupFault || _phase == SessionPhase.Disabled ? SessionPhase.Disabled : SessionPhase.Listening;
        _sessionId = null;
        _peer = null;
        _displayId = null;
        _expiresAt = null;
        _epoch = 0;
        _lastSequence = 0;
        _status = _cleanupFault ? "Input cleanup failed. Receiving disabled; investigate before restarting." : reason;
    }
}
