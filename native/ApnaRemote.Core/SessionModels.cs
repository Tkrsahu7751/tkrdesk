namespace ApnaRemote.Core;

public enum SessionPhase { Disabled, Listening, AwaitingApproval, Viewing, Controlling, Paused }
public enum InputKind { PointerMove, ButtonDown, ButtonUp, KeyDown, KeyUp, Scroll, Text }
public enum PointerButton { Left = 1, Right = 2, Middle = 3 }

/// <summary>
/// Identity supplied by the future authenticated transport. Constructing this record is NOT
/// authentication. Only an identity cryptographically bound to the connection may reach the gate.
/// </summary>
public sealed record PeerIdentity(string Id, string DisplayName);

public sealed record SessionSnapshot(
    SessionPhase Phase, Guid? SessionId, string? PeerName, string? PeerId,
    int PermissionEpoch, string? DisplayId, DateTimeOffset? RequestExpiresAt,
    string Status, bool CleanupFault);

/// <summary>Envelope for one ordered reliable input channel. Coordinates are display-normalized.</summary>
public sealed record InputCommand(
    int Version, Guid SessionId, int PermissionEpoch, long Sequence, InputKind Kind,
    double X = 0, double Y = 0, int Code = 0, int Delta = 0, string? Text = null);

public readonly record struct HeldInput(InputKind DownKind, int Code);
public readonly record struct PixelPoint(int X, int Y);
public readonly record struct DisplayBounds(int Left, int Top, int Width, int Height);

/// <summary>
/// Synchronous, non-reentrant adapter contract. Apply must either inject the command or throw.
/// Release is idempotent. The gate serializes calls with permission transitions, so a returned
/// pause/disconnect cannot race with a subsequent accepted input. No network awaits in a sink.
/// </summary>
public interface IInputSink
{
    void Apply(InputCommand command, DisplayBounds display);
    void Release(HeldInput input);
}

public sealed record CommandResult(bool Accepted, string Reason)
{
    public static readonly CommandResult Ok = new(true, "Accepted");
    public static CommandResult Reject(string reason) => new(false, reason);
}
