using System.Buffers.Binary;
using System.Text;
using ApnaRemote.Core;

var checks = new List<(string Name, Action Run)>();
void Check(string name, Action run) => checks.Add((name, run));
void Require(bool result, string message = "Condition was false")
{ if (!result) throw new InvalidOperationException(message); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
(HostSessionGate Gate, RecordingSink Sink, ManualClock Clock, Guid Id) Approved(bool control = true)
{
    var sink = new RecordingSink();
    var clock = new ManualClock();
    var gate = new HostSessionGate(sink, clock);
    gate.EnableReceiving();
    Guid id = gate.RequestFromAuthenticatedPeer(new("peer-a", "Office PC"));
    Require(gate.ApproveLocally(id, "display-1", new(-1920, 0, 1920, 1080), control));
    return (gate, sink, clock, id);
}
InputCommand Key(Guid id, long seq, int epoch = 1, InputKind kind = InputKind.KeyDown, int code = 0x41)
    => new(1, id, epoch, seq, kind, Code: code);
InputCommand ReadFrame(byte[] data) => ProtocolFraming.ReadAsync(new MemoryStream(data), CancellationToken.None)
    .AsTask().GetAwaiter().GetResult();
byte[] JsonFrame(string text)
{
    byte[] bytes = Encoding.UTF8.GetBytes(text), result = new byte[bytes.Length + 4];
    BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(0, 4), bytes.Length);
    bytes.CopyTo(result, 4);
    return result;
}

Check("receiving starts disabled", () =>
{
    var gate = new HostSessionGate(new RecordingSink());
    Require(gate.Snapshot.Phase == SessionPhase.Disabled);
    Throws<InvalidOperationException>(() => gate.RequestFromAuthenticatedPeer(new("a", "PC")));
});
Check("request grants neither video nor input", () =>
{
    var gate = new HostSessionGate(new RecordingSink()); gate.EnableReceiving();
    Guid id = gate.RequestFromAuthenticatedPeer(new("a", "PC"));
    Require(!gate.CanShareFrames(id, "a"));
    Require(!gate.TryApply("a", Key(id, 1)).Accepted);
});
Check("host defaults to view only", () =>
{
    var gate = new HostSessionGate(new RecordingSink()); gate.EnableReceiving();
    Guid id = gate.RequestFromAuthenticatedPeer(new("a", "PC"));
    Require(gate.ApproveLocally(id, "display-1", new(0, 0, 1920, 1080)));
    Require(gate.Snapshot.Phase == SessionPhase.Viewing && gate.CanShareFrames(id, "a"));
    Require(!gate.TryApply("a", Key(id, 1)).Accepted);
});
Check("only one authenticated request can be pending", () =>
{
    var gate = new HostSessionGate(new RecordingSink()); gate.EnableReceiving();
    gate.RequestFromAuthenticatedPeer(new("a", "PC"));
    Throws<InvalidOperationException>(() => gate.RequestFromAuthenticatedPeer(new("b", "PC B")));
});
Check("expired request cannot be approved", () =>
{
    var clock = new ManualClock(); var gate = new HostSessionGate(new RecordingSink(), clock);
    gate.EnableReceiving(); Guid id = gate.RequestFromAuthenticatedPeer(new("a", "PC"));
    clock.Advance(HostSessionGate.ApprovalTimeout);
    Require(!gate.ApproveLocally(id, "display-1", new(0, 0, 1, 1)));
    Require(gate.Snapshot.Phase == SessionPhase.Listening);
});
Check("reject invalidates the request", () =>
{
    var gate = new HostSessionGate(new RecordingSink()); gate.EnableReceiving();
    Guid id = gate.RequestFromAuthenticatedPeer(new("a", "PC")); gate.RejectLocally(id);
    Require(!gate.ApproveLocally(id, "display-1", new(0, 0, 1, 1)));
});
Check("peer identity must match for video and input", () =>
{
    var (gate, sink, _, id) = Approved();
    Require(!gate.CanShareFrames(id, "attacker"));
    Require(!gate.TryApply("attacker", Key(id, 1)).Accepted && sink.Commands.Count == 0);
});
Check("accepted sequence cannot be replayed", () =>
{
    var (gate, sink, _, id) = Approved();
    Require(gate.TryApply("peer-a", Key(id, 1)).Accepted);
    Require(!gate.TryApply("peer-a", Key(id, 1)).Accepted && sink.Commands.Count == 1);
});
Check("pause releases keys and mouse but keeps viewing", () =>
{
    var (gate, sink, _, id) = Approved();
    Require(gate.TryApply("peer-a", Key(id, 1, code: 0x11)).Accepted);
    Require(gate.TryApply("peer-a", new(1, id, 1, 2, InputKind.ButtonDown, Code: 1)).Accepted);
    gate.PauseLocally();
    Require(sink.Releases.Count == 2 && gate.CanShareFrames(id, "peer-a"));
    Require(!gate.TryApply("peer-a", Key(id, 3)).Accepted);
});
Check("resume invalidates queued input from previous permission epoch", () =>
{
    var (gate, _, _, id) = Approved(); gate.PauseLocally(); gate.ChangeControlLocally(true);
    Require(!gate.TryApply("peer-a", Key(id, 50)).Accepted);
    Require(gate.TryApply("peer-a", Key(id, 1, gate.Snapshot.PermissionEpoch)).Accepted);
});
Check("unheld key-up cannot release an unrelated local key", () =>
{
    var (gate, sink, _, id) = Approved();
    Require(!gate.TryApply("peer-a", Key(id, 1, kind: InputKind.KeyUp)).Accepted);
    Require(sink.Commands.Count == 0);
});
Check("normal key-up removes held state", () =>
{
    var (gate, sink, _, id) = Approved();
    gate.TryApply("peer-a", Key(id, 1)); gate.TryApply("peer-a", Key(id, 2, kind: InputKind.KeyUp));
    gate.Disconnect(); Require(sink.Releases.Count == 0);
});
Check("timeout releases held input and ends video", () =>
{
    var (gate, sink, clock, id) = Approved(); gate.TryApply("peer-a", Key(id, 1));
    clock.Advance(HostSessionGate.IdleTimeout); gate.Tick();
    Require(sink.Releases.Count == 1 && !gate.CanShareFrames(id, "peer-a"));
    Require(gate.Snapshot.SessionId is null);
});
Check("unauthenticated heartbeat cannot extend a session", () =>
{
    var (gate, _, clock, id) = Approved(); clock.Advance(TimeSpan.FromSeconds(9));
    Require(!gate.Heartbeat(id, "attacker")); clock.Advance(TimeSpan.FromSeconds(1)); gate.Tick();
    Require(gate.Snapshot.SessionId is null);
});
Check("valid heartbeat keeps a live session", () =>
{
    var (gate, _, clock, id) = Approved(); clock.Advance(TimeSpan.FromSeconds(9));
    Require(gate.Heartbeat(id, "peer-a")); clock.Advance(TimeSpan.FromSeconds(9));
    Require(gate.CanShareFrames(id, "peer-a"));
});
Check("old session ID rejected after new approval", () =>
{
    var (gate, _, _, oldId) = Approved(); gate.Disconnect();
    Guid next = gate.RequestFromAuthenticatedPeer(new("peer-a", "PC"));
    gate.ApproveLocally(next, "display-1", new(0, 0, 100, 100), true);
    Require(next != oldId && !gate.TryApply("peer-a", Key(oldId, 1)).Accepted);
});
Check("rate overflow ends session and releases held input", () =>
{
    var (gate, sink, _, id) = Approved();
    for (int i = 1; i <= 500; i++) Require(gate.TryApply("peer-a", Key(id, i)).Accepted);
    Require(!gate.TryApply("peer-a", Key(id, 501)).Accepted);
    Require(gate.Snapshot.SessionId is null && sink.Releases.Count == 1);
});
Check("partial adapter failure releases tracked key", () =>
{
    var (gate, sink, _, id) = Approved(); sink.FailApply = true;
    Require(!gate.TryApply("peer-a", Key(id, 1)).Accepted);
    Require(sink.Releases.Count == 1 && gate.Snapshot.SessionId is null);
});
Check("cleanup failure disables receiving", () =>
{
    var (gate, sink, _, id) = Approved(); gate.TryApply("peer-a", Key(id, 1)); sink.FailRelease = true;
    gate.PauseLocally(); Require(gate.Snapshot.CleanupFault && gate.Snapshot.Phase == SessionPhase.Disabled);
    Throws<InvalidOperationException>(gate.EnableReceiving);
});
Check("invalid input never reaches adapter", () =>
{
    var (gate, sink, _, id) = Approved();
    Require(!gate.TryApply("peer-a", new(1, id, 1, 1, InputKind.PointerMove, X: double.NaN)).Accepted);
    Require(!gate.TryApply("peer-a", Key(id, 2, code: 0xE7)).Accepted);
    Require(sink.Commands.Count == 0);
});
Check("unicode accepts Hindi and emoji; rejects malformed UTF-16", () =>
{
    Guid id = Guid.NewGuid();
    Require(InputValidation.IsValid(new(1, id, 1, 1, InputKind.Text, Text: "नमस्ते 👋")));
    Require(!InputValidation.IsValid(new(1, id, 1, 1, InputKind.Text, Text: "\uD800")));
    Require(!InputValidation.IsValid(new(1, id, 1, 1, InputKind.Text, Text: "\0")));
    Require(!InputValidation.IsValid(new(1, id, 1, 1, InputKind.Text, Text: new string('a', 513))));
});
Check("pointer maps negative monitor origin and final pixel", () =>
{
    var bounds = new DisplayBounds(-1920, -200, 1920, 1080);
    Require(PointerMapping.ToPhysical(0, 0, bounds) == new PixelPoint(-1920, -200));
    Require(PointerMapping.ToPhysical(1, 1, bounds) == new PixelPoint(-1, 879));
});
Check("letterbox clicks are ignored and center maps correctly", () =>
{
    Require(!PointerMapping.TryFromViewport(10, 10, 1000, 1000, 1920, 1080, out _));
    Require(PointerMapping.TryFromViewport(500, 500, 1000, 1000, 1920, 1080, out var point));
    Require(Math.Abs(point.X - .5) < .00001 && Math.Abs(point.Y - .5) < .00001);
});
Check("invalid display or viewport rejected", () =>
{
    Throws<ArgumentOutOfRangeException>(() => PointerMapping.ToPhysical(0, 0, new(0, 0, 0, 1080)));
    Throws<ArgumentOutOfRangeException>(() => PointerMapping.ToPhysical(0, 0, new(int.MaxValue, 0, 2, 1)));
    Require(!PointerMapping.TryFromViewport(0, 0, double.PositiveInfinity, 10, 10, 10, out _));
});
Check("framing round trip", () =>
{
    var command = Key(Guid.NewGuid(), 12); Require(ReadFrame(ProtocolFraming.Encode(command)) == command);
});
Check("oversized length rejected before reading payload", () =>
{
    byte[] header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, int.MaxValue);
    Throws<InvalidDataException>(() => ReadFrame(header));
});
Check("negative frame length rejected", () =>
{
    byte[] header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, -1);
    Throws<InvalidDataException>(() => ReadFrame(header));
});
Check("truncated payload rejected", () =>
{
    byte[] header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, 100);
    Throws<EndOfStreamException>(() => ReadFrame(header));
});
Check("duplicate JSON fields rejected", () =>
{
    Throws<InvalidDataException>(() => ReadFrame(JsonFrame("{\"Version\":1,\"Version\":1}")));
});
Check("unknown JSON field rejected", () =>
{
    byte[] good = ProtocolFraming.Encode(Key(Guid.NewGuid(), 1));
    string text = Encoding.UTF8.GetString(good, 4, good.Length - 4);
    Throws<InvalidDataException>(() => ReadFrame(JsonFrame(text[..^1] + ",\"ShellCommand\":\"no\"}")));
});

int failed = 0;
foreach (var check in checks)
{
    try { check.Run(); Console.WriteLine("PASS " + check.Name); }
    catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + check.Name + ": " + error.Message); }
}
Console.WriteLine($"{checks.Count - failed}/{checks.Count} checks passed. No network, capture or OS input was performed.");
return failed == 0 ? 0 : 1;

sealed class RecordingSink : IInputSink
{
    public List<InputCommand> Commands { get; } = [];
    public List<HeldInput> Releases { get; } = [];
    public bool FailApply { get; set; }
    public bool FailRelease { get; set; }
    public void Apply(InputCommand command, DisplayBounds display)
    { if (FailApply) throw new InvalidOperationException("Test failure"); Commands.Add(command); }
    public void Release(HeldInput input)
    { if (FailRelease) throw new InvalidOperationException("Test release failure"); Releases.Add(input); }
}

sealed class ManualClock : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(_ticks);
    public void Advance(TimeSpan time) => _ticks += time.Ticks;
}
