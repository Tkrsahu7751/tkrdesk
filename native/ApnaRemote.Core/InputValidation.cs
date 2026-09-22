namespace ApnaRemote.Core;

public static class InputValidation
{
    public const int ProtocolVersion = 1;
    public const int MaxTextCharacters = 512;

    public static bool IsValid(InputCommand command)
    {
        if (command.Version != ProtocolVersion || command.SessionId == Guid.Empty ||
            command.PermissionEpoch <= 0 || command.Sequence <= 0 || !Enum.IsDefined(command.Kind)) return false;
        if (!double.IsFinite(command.X) || !double.IsFinite(command.Y)) return false;

        return command.Kind switch
        {
            InputKind.PointerMove => command.X is >= 0 and <= 1 && command.Y is >= 0 and <= 1 &&
                command.Code == 0 && command.Delta == 0 && command.Text is null,
            InputKind.ButtonDown or InputKind.ButtonUp => Enum.IsDefined((PointerButton)command.Code) &&
                command.X == 0 && command.Y == 0 && command.Delta == 0 && command.Text is null,
            InputKind.KeyDown or InputKind.KeyUp => IsSupportedVirtualKey(command.Code) &&
                command.X == 0 && command.Y == 0 && command.Delta == 0 && command.Text is null,
            InputKind.Scroll => command.Delta is >= -1200 and <= 1200 && command.Delta != 0 &&
                command.X == 0 && command.Y == 0 && command.Code == 0 && command.Text is null,
            InputKind.Text => command.Code == 0 && command.Delta == 0 && command.X == 0 && command.Y == 0 &&
                IsValidText(command.Text),
            _ => false
        };
    }

    // Mouse codes, undefined/reserved VK codes, injected packet/process keys are excluded.
    private static bool IsSupportedVirtualKey(int key) => key is 0x08 or 0x09 or 0x0D or
        >= 0x10 and <= 0x14 or 0x1B or >= 0x20 and <= 0x28 or 0x2C or 0x2D or 0x2E or
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5D or >= 0x60 and <= 0x6F or
        >= 0x70 and <= 0x87 or 0x90 or 0x91 or >= 0xA0 and <= 0xA5 or
        >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2;

    private static bool IsValidText(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxTextCharacters) return false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsControl(c) && c is not '\n' and not '\r' and not '\t') return false;
            if (char.IsHighSurrogate(c))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false;
            }
            else if (char.IsLowSurrogate(c)) return false;
        }
        return true;
    }
}
