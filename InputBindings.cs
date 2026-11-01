using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace D2AbilityTracker;

[Flags]
internal enum InputModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8
}

internal enum InputDevice
{
    Keyboard,
    Mouse
}

internal static class MouseInputCode
{
    public const int Left = 1;
    public const int Right = 2;
    public const int Middle = 3;
    public const int X1 = 4;
    public const int X2 = 5;
    public const int WheelUp = 6;
    public const int WheelDown = 7;
    public const int WheelLeft = 8;
    public const int WheelRight = 9;
}

internal readonly record struct InputBinding(InputDevice Device, int Code, InputModifiers Modifiers)
{
    public static InputBinding DefaultPause => new(InputDevice.Keyboard, (int)Keys.F8, InputModifiers.None);

    public override string ToString()
    {
        var modifiers = new List<string>(4);
        if (Modifiers.HasFlag(InputModifiers.Control)) modifiers.Add("Ctrl");
        if (Modifiers.HasFlag(InputModifiers.Alt)) modifiers.Add("Alt");
        if (Modifiers.HasFlag(InputModifiers.Shift)) modifiers.Add("Shift");
        if (Modifiers.HasFlag(InputModifiers.Windows)) modifiers.Add("Win");
        var input = Device == InputDevice.Keyboard
            ? Enum.IsDefined((Keys)Code) ? ((Keys)Code).ToString() : $"Key 0x{Code:X2}"
            : Code switch
            {
                MouseInputCode.Left => "Mouse Left",
                MouseInputCode.Right => "Mouse Right",
                MouseInputCode.Middle => "Mouse Middle",
                MouseInputCode.X1 => "Mouse X1",
                MouseInputCode.X2 => "Mouse X2",
                MouseInputCode.WheelUp => "Wheel Up",
                MouseInputCode.WheelDown => "Wheel Down",
                MouseInputCode.WheelLeft => "Wheel Left",
                MouseInputCode.WheelRight => "Wheel Right",
                _ => $"Mouse {Code}"
            };
        return modifiers.Count == 0 ? input : $"{string.Join("+", modifiers)}+{input}";
    }
}

internal sealed class InputBindingEventArgs(InputBinding binding) : EventArgs
{
    public InputBinding Binding { get; } = binding;
}

internal sealed class GlobalInputListener : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int HcAction = 0;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmMouseHWheel = 0x020E;
    private const uint LlkhfInjected = 0x10;
    private const uint LlmhfInjected = 0x01;
    private readonly HookProcedure _keyboardProcedure;
    private readonly HookProcedure _mouseProcedure;
    private readonly HashSet<int> _pressedKeys = [];
    private readonly HashSet<int> _pressedMouseButtons = [];
    private readonly object _captureLock = new();
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private Action<InputBinding>? _captureCallback;

    public GlobalInputListener()
    {
        _keyboardProcedure = KeyboardHook;
        _mouseProcedure = MouseHook;
    }

    public event EventHandler<InputBindingEventArgs>? BindingPressed;

    public void Start()
    {
        if (_keyboardHook != IntPtr.Zero || _mouseHook != IntPtr.Zero)
        {
            return;
        }

        var module = InputNativeMethods.GetModuleHandle(null);
        _keyboardHook = InputNativeMethods.SetWindowsHookEx(WhKeyboardLl, _keyboardProcedure, module, 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Could not install the global keyboard hook (error {Marshal.GetLastWin32Error()}).");
        }

        _mouseHook = InputNativeMethods.SetWindowsHookEx(WhMouseLl, _mouseProcedure, module, 0);
        if (_mouseHook == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            InputNativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
            throw new InvalidOperationException($"Could not install the global mouse hook (error {error}).");
        }
    }

    public void BeginCapture(Action<InputBinding> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_captureLock)
        {
            _captureCallback = callback;
        }
    }

    public void CancelCapture()
    {
        lock (_captureLock)
        {
            _captureCallback = null;
        }
    }

    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code == HcAction)
        {
            var hookData = Marshal.PtrToStructure<KeyboardHookData>(data);
            if ((hookData.Flags & LlkhfInjected) == 0)
            {
                var key = checked((int)hookData.VirtualKey);
                var messageId = message.ToInt32();
                if (messageId is WmKeyUp or WmSysKeyUp)
                {
                    _pressedKeys.Remove(key);
                }
                else if (messageId is WmKeyDown or WmSysKeyDown && _pressedKeys.Add(key) && !IsModifier(key))
                {
                    Publish(new InputBinding(InputDevice.Keyboard, key, CurrentModifiers()));
                }
            }
        }

        return InputNativeMethods.CallNextHookEx(_keyboardHook, code, message, data);
    }

    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code == HcAction)
        {
            var hookData = Marshal.PtrToStructure<MouseHookData>(data);
            if ((hookData.Flags & LlmhfInjected) == 0)
            {
                var messageId = message.ToInt32();
                var (button, isDown) = messageId switch
                {
                    WmLButtonDown => (MouseInputCode.Left, true),
                    WmLButtonUp => (MouseInputCode.Left, false),
                    WmRButtonDown => (MouseInputCode.Right, true),
                    WmRButtonUp => (MouseInputCode.Right, false),
                    WmMButtonDown => (MouseInputCode.Middle, true),
                    WmMButtonUp => (MouseInputCode.Middle, false),
                    WmXButtonDown or WmXButtonUp => ((hookData.MouseData >> 16) switch
                    {
                        1 => MouseInputCode.X1,
                        2 => MouseInputCode.X2,
                        _ => 0
                    }, messageId == WmXButtonDown),
                    _ => (0, false)
                };

                if (button != 0)
                {
                    if (isDown && _pressedMouseButtons.Add(button))
                    {
                        Publish(new InputBinding(InputDevice.Mouse, button, CurrentModifiers()));
                    }
                    else if (!isDown)
                    {
                        _pressedMouseButtons.Remove(button);
                    }
                }
                else if (messageId is WmMouseWheel or WmMouseHWheel)
                {
                    var delta = (short)(hookData.MouseData >> 16);
                    var codeValue = messageId == WmMouseWheel
                        ? delta > 0 ? MouseInputCode.WheelUp : MouseInputCode.WheelDown
                        : delta > 0 ? MouseInputCode.WheelRight : MouseInputCode.WheelLeft;
                    Publish(new InputBinding(InputDevice.Mouse, codeValue, CurrentModifiers()));
                }
            }
        }

        return InputNativeMethods.CallNextHookEx(_mouseHook, code, message, data);
    }

    private void Publish(InputBinding binding)
    {
        Action<InputBinding>? captureCallback;
        lock (_captureLock)
        {
            captureCallback = _captureCallback;
            _captureCallback = null;
        }

        if (captureCallback is not null)
        {
            captureCallback(binding);
        }
        else
        {
            BindingPressed?.Invoke(this, new InputBindingEventArgs(binding));
        }
    }

    private InputModifiers CurrentModifiers()
    {
        var modifiers = InputModifiers.None;
        if (_pressedKeys.Contains((int)Keys.LControlKey) || _pressedKeys.Contains((int)Keys.RControlKey))
            modifiers |= InputModifiers.Control;
        if (_pressedKeys.Contains((int)Keys.LMenu) || _pressedKeys.Contains((int)Keys.RMenu))
            modifiers |= InputModifiers.Alt;
        if (_pressedKeys.Contains((int)Keys.LShiftKey) || _pressedKeys.Contains((int)Keys.RShiftKey))
            modifiers |= InputModifiers.Shift;
        if (_pressedKeys.Contains((int)Keys.LWin) || _pressedKeys.Contains((int)Keys.RWin))
            modifiers |= InputModifiers.Windows;
        return modifiers;
    }

    private static bool IsModifier(int key) => key is
        (int)Keys.LControlKey or (int)Keys.RControlKey or
        (int)Keys.LMenu or (int)Keys.RMenu or
        (int)Keys.LShiftKey or (int)Keys.RShiftKey or
        (int)Keys.LWin or (int)Keys.RWin;

    public void Dispose()
    {
        CancelCapture();
        if (_keyboardHook != IntPtr.Zero)
        {
            InputNativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            InputNativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    internal delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public Point Position;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }
}

internal static class InputNativeMethods
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(
        int hookType,
        GlobalInputListener.HookProcedure hookProcedure,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
}
