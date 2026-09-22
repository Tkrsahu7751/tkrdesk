using ApnaRemote.Core;

namespace ApnaRemote.Windows.Lan;

/// <summary>View-only lab sink — input path not wired in this milestone.</summary>
internal sealed class NullInputSink : IInputSink
{
    public void Apply(InputCommand command, DisplayBounds display)
        => throw new InvalidOperationException("Remote input is not enabled in the LAN JPEG lab.");

    public void Release(HeldInput input)
    {
        // No held state.
    }
}
