using System.Runtime.InteropServices;
using System.Diagnostics;
using System.ComponentModel;
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
    private Control? _hookDispatcher;

    public GlobalInputListener()
    {
        _keyboardProcedure = KeyboardHook;
        _mouseProcedure = MouseHook;
    }

    public event EventHandler<InputBindingEventArgs>? BindingPressed;
    public event Action<string>? Failed;

    public void Start()
    {
        if (_hookDispatcher is not null)
        {
            return;
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookThread = new Thread(() =>
        {
            try
            {
                using var dispatcher = new Control();
                dispatcher.CreateControl();
                _hookDispatcher = dispatcher;
                InstallHooks();
                ready.SetResult();
                Application.Run();
            }
            catch (Exception exception)
            {
                if (!ready.TrySetException(exception))
                {
                    Trace.TraceError($"Global input listener stopped: {exception}");
                    Failed?.Invoke(exception.Message);
                }
            }
            finally
            {
                UninstallHooks();
                _hookDispatcher = null;
            }
        })
        {
            IsBackground = true,
            Name = "D2 input hooks"
        };
        hookThread.SetApartmentState(ApartmentState.STA);
        hookThread.Start();
        ready.Task.GetAwaiter().GetResult();
    }

    private void InstallHooks()
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
        if (_pressedKeys.Contains((int)Keys.ControlKey) ||
            _pressedKeys.Contains((int)Keys.LControlKey) ||
            _pressedKeys.Contains((int)Keys.RControlKey))
            modifiers |= InputModifiers.Control;
        if (_pressedKeys.Contains((int)Keys.Menu) ||
            _pressedKeys.Contains((int)Keys.LMenu) ||
            _pressedKeys.Contains((int)Keys.RMenu))
            modifiers |= InputModifiers.Alt;
        if (_pressedKeys.Contains((int)Keys.ShiftKey) ||
            _pressedKeys.Contains((int)Keys.LShiftKey) ||
            _pressedKeys.Contains((int)Keys.RShiftKey))
            modifiers |= InputModifiers.Shift;
        if (_pressedKeys.Contains((int)Keys.LWin) || _pressedKeys.Contains((int)Keys.RWin))
            modifiers |= InputModifiers.Windows;
        return modifiers;
    }

    private static bool IsModifier(int key) => key is
        (int)Keys.ControlKey or (int)Keys.LControlKey or (int)Keys.RControlKey or
        (int)Keys.Menu or (int)Keys.LMenu or (int)Keys.RMenu or
        (int)Keys.ShiftKey or (int)Keys.LShiftKey or (int)Keys.RShiftKey or
        (int)Keys.LWin or (int)Keys.RWin;

    public void Dispose()
    {
        CancelCapture();
        var dispatcher = _hookDispatcher;
        if (dispatcher is not null && !dispatcher.IsDisposed)
        {
            try
            {
                dispatcher.BeginInvoke((Action)Application.ExitThread);
            }
            catch (InvalidOperationException) when (dispatcher.IsDisposed)
            {
            }
        }
    }

    private void UninstallHooks()
    {
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

internal static class InputBindingSender
{
    private const uint InputKeyboard = 1;
    private const uint InputMouse = 0;
    private const uint KeyUp = 0x0002;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseXDown = 0x0080;
    private const uint MouseXUp = 0x0100;
    private const uint MouseWheel = 0x0800;
    private const uint MouseHorizontalWheel = 0x1000;

    public static bool IsDestiny2Foreground()
    {
        var foregroundWindow = InputNativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        InputNativeMethods.GetWindowThreadProcessId(foregroundWindow, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.ProcessName.Equals("destiny2", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public static void PressAndRelease(InputBinding binding)
    {
        var inputs = new List<InputEvent>(6);
        var modifierKeys = new List<ushort>(4);
        if (binding.Modifiers.HasFlag(InputModifiers.Control)) modifierKeys.Add(0x11);
        if (binding.Modifiers.HasFlag(InputModifiers.Alt)) modifierKeys.Add(0x12);
        if (binding.Modifiers.HasFlag(InputModifiers.Shift)) modifierKeys.Add(0x10);
        if (binding.Modifiers.HasFlag(InputModifiers.Windows)) modifierKeys.Add(0x5B);
        foreach (var modifier in modifierKeys)
        {
            inputs.Add(new InputEvent
            {
                Type = InputKeyboard,
                Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = modifier } }
            });
        }

        if (binding.Device == InputDevice.Keyboard)
        {
            if (binding.Code is <= 0 or > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(binding), "The recorded keyboard key is invalid.");
            }

            inputs.Add(new InputEvent { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = (ushort)binding.Code } } });
            inputs.Add(new InputEvent { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = (ushort)binding.Code, Flags = KeyUp } } });
        }
        else
        {
            var (downFlags, upFlags, mouseData) = binding.Code switch
            {
                MouseInputCode.Left => (MouseLeftDown, MouseLeftUp, 0u),
                MouseInputCode.Right => (MouseRightDown, MouseRightUp, 0u),
                MouseInputCode.Middle => (MouseMiddleDown, MouseMiddleUp, 0u),
                MouseInputCode.X1 => (MouseXDown, MouseXUp, 1u),
                MouseInputCode.X2 => (MouseXDown, MouseXUp, 2u),
                MouseInputCode.WheelUp => (MouseWheel, 0u, 120u),
                MouseInputCode.WheelDown => (MouseWheel, 0u, unchecked((uint)-120)),
                MouseInputCode.WheelRight => (MouseHorizontalWheel, 0u, 120u),
                MouseInputCode.WheelLeft => (MouseHorizontalWheel, 0u, unchecked((uint)-120)),
                _ => throw new ArgumentOutOfRangeException(nameof(binding), "The recorded mouse input is unsupported.")
            };
            inputs.Add(new InputEvent { Type = InputMouse, Data = new InputUnion { Mouse = new MouseInput { Flags = downFlags, MouseData = mouseData } } });
            if (upFlags != 0)
            {
                inputs.Add(new InputEvent { Type = InputMouse, Data = new InputUnion { Mouse = new MouseInput { Flags = upFlags, MouseData = mouseData } } });
            }
        }

        for (var index = modifierKeys.Count - 1; index >= 0; index--)
        {
            inputs.Add(new InputEvent
            {
                Type = InputKeyboard,
                Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = modifierKeys[index], Flags = KeyUp } }
            });
        }

        var sent = InputNativeMethods.SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<InputEvent>());
        if (sent != inputs.Count)
        {
            throw new InvalidOperationException($"Windows could not send the ability input (error {Marshal.GetLastWin32Error()}).");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct InputEvent
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
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

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(
        uint inputCount,
        [In] InputBindingSender.InputEvent[] inputs,
        int inputSize);

}
