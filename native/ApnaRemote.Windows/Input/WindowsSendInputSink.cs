using System.Runtime.InteropServices;
using ApnaRemote.Core;

namespace ApnaRemote.Windows.Input;

/// <summary>
/// Host-side SendInput adapter. Only called through <see cref="HostSessionGate"/> after control approval.
/// </summary>
internal sealed class WindowsSendInputSink : IInputSink
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseWheel = 0x0800;
    private const uint Absolute = 0x8000;
    private const uint VirtualDesk = 0x4000;
    private const uint KeyUp = 0x0002;
    private const uint Unicode = 0x0004;
    private const uint ExtendedKey = 0x0001;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    public void Apply(InputCommand command, DisplayBounds display)
    {
        switch (command.Kind)
        {
            case InputKind.PointerMove:
                MoveAbsolute(command.X, command.Y, display);
                break;
            case InputKind.ButtonDown:
                SendMouseButton((PointerButton)command.Code, down: true);
                break;
            case InputKind.ButtonUp:
                SendMouseButton((PointerButton)command.Code, down: false);
                break;
            case InputKind.KeyDown:
                SendKey(command.Code, down: true);
                break;
            case InputKind.KeyUp:
                SendKey(command.Code, down: false);
                break;
            case InputKind.Scroll:
                SendWheel(command.Delta);
                break;
            case InputKind.Text:
                SendText(command.Text ?? "");
                break;
            default:
                throw new InvalidOperationException("Unsupported input kind.");
        }
    }

    public void Release(HeldInput input)
    {
        if (input.DownKind == InputKind.KeyDown)
        {
            SendKey(input.Code, down: false);
        }
        else if (input.DownKind == InputKind.ButtonDown)
        {
            SendMouseButton((PointerButton)input.Code, down: false);
        }
    }

    private static void MoveAbsolute(double x, double y, DisplayBounds display)
    {
        PixelPoint pixel = PointerMapping.ToPhysical(x, y, display);
        int vx = GetSystemMetrics(SmXVirtualScreen);
        int vy = GetSystemMetrics(SmYVirtualScreen);
        int vw = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen));
        int vh = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen));

        // SendInput absolute uses 0..65535 across the virtual desktop.
        int absX = (int)Math.Round((pixel.X - vx) * 65535.0 / Math.Max(1, vw - 1));
        int absY = (int)Math.Round((pixel.Y - vy) * 65535.0 / Math.Max(1, vh - 1));
        absX = Math.Clamp(absX, 0, 65535);
        absY = Math.Clamp(absY, 0, 65535);

        var input = new Input
        {
            type = InputMouse,
            U = new InputUnion
            {
                mi = new MouseInput
                {
                    dx = absX,
                    dy = absY,
                    dwFlags = MouseMove | Absolute | VirtualDesk,
                }
            }
        };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
        {
            throw new InvalidOperationException("SendInput mouse move failed.");
        }
    }

    private static void SendMouseButton(PointerButton button, bool down)
    {
        uint flags = button switch
        {
            PointerButton.Left => down ? MouseLeftDown : MouseLeftUp,
            PointerButton.Right => down ? MouseRightDown : MouseRightUp,
            PointerButton.Middle => down ? MouseMiddleDown : MouseMiddleUp,
            _ => throw new InvalidOperationException("Unknown button.")
        };
        var input = new Input
        {
            type = InputMouse,
            U = new InputUnion { mi = new MouseInput { dwFlags = flags } }
        };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
        {
            throw new InvalidOperationException("SendInput mouse button failed.");
        }
    }

    private static void SendWheel(int delta)
    {
        var input = new Input
        {
            type = InputMouse,
            U = new InputUnion
            {
                mi = new MouseInput
                {
                    mouseData = (uint)delta,
                    dwFlags = MouseWheel,
                }
            }
        };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
        {
            throw new InvalidOperationException("SendInput wheel failed.");
        }
    }

    private static void SendKey(int virtualKey, bool down)
    {
        uint flags = down ? 0u : KeyUp;
        if (IsExtended(virtualKey))
        {
            flags |= ExtendedKey;
        }

        var input = new Input
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KeyboardInput
                {
                    wVk = (ushort)virtualKey,
                    dwFlags = flags,
                }
            }
        };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
        {
            throw new InvalidOperationException("SendInput key failed.");
        }
    }

    private static void SendText(string text)
    {
        foreach (char ch in text)
        {
            SendUnicode(ch, down: true);
            SendUnicode(ch, down: false);
        }
    }

    private static void SendUnicode(char ch, bool down)
    {
        var input = new Input
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KeyboardInput
                {
                    wScan = ch,
                    dwFlags = Unicode | (down ? 0u : KeyUp),
                }
            }
        };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
        {
            throw new InvalidOperationException("SendInput unicode failed.");
        }
    }

    private static bool IsExtended(int vk) => vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
        or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or >= 0xA0 and <= 0xA5;

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput mi;
        [FieldOffset(0)] public KeyboardInput ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
