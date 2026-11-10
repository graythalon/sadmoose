using System.Runtime.InteropServices;
using System.Text.Json;
using System.Collections.Generic;

namespace Drift.Windows;

internal sealed class WindowsInputAdapter : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    private const int WmMouseMove = 0x0200, WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, WmRButtonDown = 0x0204, WmRButtonUp = 0x0205, WmMButtonDown = 0x0207, WmMButtonUp = 0x0208, WmMouseWheel = 0x020A;
    private const uint LlMouseInjected = 0x00000001, LlKeyboardInjected = 0x00000010;
    private static readonly IntPtr Module = GetModuleHandle(null);
    private readonly HookProc _keyboardProc;
    private readonly HookProc _mouseProc;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private Point _previous;
    private string? _focusedPeer;
    private bool _sharing, _mouseEnabled = true, _keyboardEnabled = true;
    private bool _escapeDown;
    private bool _remoteCursorMode;
    private int _showCursorCalls;
    private readonly HashSet<int> _forwardedKeys = new();
    private readonly HashSet<int> _physicalButtonsDown = new();
    public static WindowsInputAdapter? Current { get; set; }
    public event EventHandler<JsonElement>? EventCaptured;

    public WindowsInputAdapter()
    {
        _keyboardProc = KeyboardHook;
        _mouseProc = MouseHook;
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, Module, 0);
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, Module, 0);
        GetCursorPos(out _previous);
    }

    public void SetSharing(bool sharing, bool mouse, bool keyboard)
    {
        _sharing = sharing;
        _mouseEnabled = mouse;
        _keyboardEnabled = keyboard;
        if (!sharing || !mouse) _physicalButtonsDown.Clear();
        UpdateCursorMode();
    }
    public void SetFocusedPeer(string? peerId)
    {
        if (peerId is null && _focusedPeer is not null) ReleaseRemoteKeys();
        _focusedPeer = peerId;
        UpdateCursorMode();
    }

    private void UpdateCursorMode()
    {
        var shouldHideCursor = _sharing && _mouseEnabled && _focusedPeer is not null;
        if (shouldHideCursor && !_remoteCursorMode)
        {
            _remoteCursorMode = true;
            var result = ShowCursor(false);
            _showCursorCalls = 1;
            while (result >= 0) { result = ShowCursor(false); _showCursorCalls++; }
            var centerX = GetSystemMetrics(76) + GetSystemMetrics(78) / 2;
            var centerY = GetSystemMetrics(77) + GetSystemMetrics(79) / 2;
            SetCursorPos(centerX, centerY);
            _previous = new Point { X = centerX, Y = centerY };
        }
        else if (!shouldHideCursor && _remoteCursorMode)
        {
            _remoteCursorMode = false;
            for (var i = 0; i < _showCursorCalls; i++) ShowCursor(true);
            _showCursorCalls = 0;
        }
    }

    public void ClearForwardedKeys() => _forwardedKeys.Clear();

    public void ReleaseRemoteKeys()
    {
        foreach (var vk in _forwardedKeys.ToArray())
            EventCaptured?.Invoke(this, JsonSerializer.SerializeToElement(new { kind = "key", vk, down = false }));
        _forwardedKeys.Clear();
    }

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || !_sharing) return CallNextHookEx(_keyboardHook, code, wParam, lParam);
        var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        if ((data.Flags & LlKeyboardInjected) != 0) return CallNextHookEx(_keyboardHook, code, wParam, lParam);
        var msg = unchecked((int)wParam.ToInt64());
        var down = msg is WmKeyDown or WmSysKeyDown;
        var up = msg is WmKeyUp or WmSysKeyUp;
        if (!down && !up) return CallNextHookEx(_keyboardHook, code, wParam, lParam);
        var vk = (int)data.VkCode;
        var ctrlAltShift = (GetAsyncKeyState(0x11) & 0x8000) != 0 && (GetAsyncKeyState(0x12) & 0x8000) != 0 && (GetAsyncKeyState(0x10) & 0x8000) != 0;
        if (ctrlAltShift && vk == 0x08 && down)
        {
            ReleaseRemoteKeys();
            _focusedPeer = null;
            _escapeDown = true;
            EventCaptured?.Invoke(this, JsonSerializer.SerializeToElement(new { kind = "edge", edge = "return" }));
            return new IntPtr(1);
        }
        if (vk == 0x08 && up && _escapeDown) { _escapeDown = false; return new IntPtr(1); }
        if (_focusedPeer is null) return CallNextHookEx(_keyboardHook, code, wParam, lParam);
        if (!_keyboardEnabled) return CallNextHookEx(_keyboardHook, code, wParam, lParam);
        if (down) _forwardedKeys.Add(vk); else _forwardedKeys.Remove(vk);
        EventCaptured?.Invoke(this, JsonSerializer.SerializeToElement(new { kind = "key", vk, down, extended = (data.Flags & 1) != 0 }));
        return new IntPtr(1);
    }

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || !_sharing || !_mouseEnabled) return CallNextHookEx(_mouseHook, code, wParam, lParam);
        var data = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
        if ((data.Flags & LlMouseInjected) != 0) return CallNextHookEx(_mouseHook, code, wParam, lParam);
        var message = unchecked((int)wParam.ToInt64());
        var x = data.Point.X; var y = data.Point.Y;
        var buttonCode = message switch
        {
            WmLButtonDown or WmLButtonUp => 1,
            WmRButtonDown or WmRButtonUp => 2,
            WmMButtonDown or WmMButtonUp => 3,
            _ => 0
        };
        var buttonPressed = message is WmLButtonDown or WmRButtonDown or WmMButtonDown;
        var buttonReleased = message is WmLButtonUp or WmRButtonUp or WmMButtonUp;
        if (buttonPressed) _physicalButtonsDown.Add(buttonCode);
        if (_focusedPeer is null)
        {
            var left = GetSystemMetrics(76);
            var top = GetSystemMetrics(77);
            var right = left + GetSystemMetrics(78);
            var bottom = top + GetSystemMetrics(79);
            if (_physicalButtonsDown.Count == 0 && (x >= right - 1 || y >= bottom - 1 || x <= left || y <= top))
            {
                var peer = CurrentTargetPeer();
                if (peer is not null)
                {
                    SetFocusedPeer(peer);
                    EventCaptured?.Invoke(this, JsonSerializer.SerializeToElement(new { kind = "focus", target = peer, x = .02, y = .5 }));
                    return new IntPtr(1);
                }
            }
            _previous = data.Point;
            if (buttonReleased) _physicalButtonsDown.Remove(buttonCode);
            return CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        if (message == WmMouseMove)
        {
            if (_remoteCursorMode)
            {
                var centerX = GetSystemMetrics(76) + GetSystemMetrics(78) / 2;
                var centerY = GetSystemMetrics(77) + GetSystemMetrics(79) / 2;
                if (x == centerX && y == centerY)
                {
                    _previous = data.Point;
                    return new IntPtr(1);
                }
            }
            var dx = x - _previous.X; var dy = y - _previous.Y;
            _previous = data.Point;
            if (_remoteCursorMode)
            {
                var centerX = GetSystemMetrics(76) + GetSystemMetrics(78) / 2;
                var centerY = GetSystemMetrics(77) + GetSystemMetrics(79) / 2;
                SetCursorPos(centerX, centerY);
                _previous = new Point { X = centerX, Y = centerY };
            }
            if (dx != 0 || dy != 0) EventCaptured?.Invoke(this, JsonSerializer.SerializeToElement(new { kind = "pointer", action = "move", dx, dy }));
            return new IntPtr(1);
        }
        var button = message switch { WmLButtonDown or WmLButtonUp => "left", WmRButtonDown or WmRButtonUp => "right", WmMButtonDown or WmMButtonUp => "middle", WmMouseWheel => "wheel", _ => null };
        if (button is null) return CallNextHookEx(_mouseHook, code, wParam, lParam);
        var down = message is WmLButtonDown or WmRButtonDown or WmMButtonDown;
        var wheel = message == WmMouseWheel ? unchecked((short)(data.MouseData >> 16)) : 0;
        EventCaptured?.Invoke(this, JsonSerializer.SerializeToElement(new { kind = "pointer", action = button == "wheel" ? "wheel" : "button", button, down, wheel }));
        if (buttonReleased) _physicalButtonsDown.Remove(buttonCode);
        return new IntPtr(1);
    }

    private static string? CurrentTargetPeer()
    {
        var window = System.Windows.Application.Current?.MainWindow as MainWindow;
        return window?.SelectedPeerId;
    }

    public void Inject(JsonElement message)
    {
        var kind = message.GetProperty("kind").GetString();
        if (kind == "pointer")
        {
            var action = message.GetProperty("action").GetString();
            if (action == "move")
            {
                GetCursorPos(out var point);
                var dx = message.GetProperty("dx").GetInt32(); var dy = message.GetProperty("dy").GetInt32();
                var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
                var right = left + GetSystemMetrics(78); var bottom = top + GetSystemMetrics(79);
                SetCursorPos(Math.Clamp(point.X + dx, left, right - 1), Math.Clamp(point.Y + dy, top, bottom - 1));
            }
            else if (action == "button")
            {
                var button = message.GetProperty("button").GetString();
                var down = message.GetProperty("down").GetBoolean();
                var flags = (button, down) switch { ("left", true) => 0x0002, ("left", false) => 0x0004, ("right", true) => 0x0008, ("right", false) => 0x0010, ("middle", true) => 0x0020, ("middle", false) => 0x0040, _ => 0 };
                SendMouse((uint)flags, 0);
            }
            else if (action == "wheel") SendMouse(0x0800, unchecked((uint)message.GetProperty("wheel").GetInt32()));
        }
        else if (kind == "key")
        {
            var vk = (ushort)(message.TryGetProperty("vk", out var winVk) ? winVk.GetInt32() : AndroidToVirtualKey(message.GetProperty("keyCode").GetInt32()));
            var down = message.GetProperty("down").GetBoolean();
            var extended = vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B or 0x5C;
            var flags = (down ? 0u : 0x0002u) | (extended ? 0x0001u : 0u);
            var input = new Input { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vk, Flags = flags } } };
            SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
        }
        else if (kind == "text")
        {
            var text = message.GetProperty("text").GetString() ?? "";
            foreach (var c in text)
            {
                var down = new Input { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { Scan = c, Flags = 0x0004 } } };
                var up = new Input { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { Scan = c, Flags = 0x0004 | 0x0002 } } };
                SendInput(1, new[] { down }, Marshal.SizeOf<Input>());
                SendInput(1, new[] { up }, Marshal.SizeOf<Input>());
            }
        }
    }

    private static int AndroidToVirtualKey(int keyCode) => keyCode switch
    {
        >= 29 and <= 54 => 0x41 + keyCode - 29,
        >= 7 and <= 16 => keyCode == 7 ? 0x30 : 0x31 + keyCode - 8,
        66 => 0x0D, 61 => 0x09, 67 => 0x08, 111 or 4 => 0x1B, 21 => 0x25, 19 => 0x26, 22 => 0x27, 20 => 0x28,
        74 => 0xBA, 70 => 0xBB, 55 => 0xBC, 69 => 0xBD, 56 => 0xBE, 76 => 0xBF, 68 => 0xC0, 71 => 0xDB, 73 => 0xDC, 72 => 0xDD, 75 => 0xDE,
        59 or 60 => 0x10, 113 or 114 => 0x11, 57 or 58 => 0x12, 115 => 0x14, 117 => 0x5B, 118 => 0x5C, 62 => 0x20,
        122 => 0x24, 123 => 0x23, 92 => 0x21, 93 => 0x22, 112 => 0x2E, 124 => 0x2D, 120 => 0x2C, >= 131 and <= 142 => 0x70 + keyCode - 131, _ => 0
    };

    private static void SendMouse(uint flags, uint data)
    {
        var input = new Input { Type = 0, Union = new InputUnion { Mouse = new MouseInput { Flags = flags, MouseData = data } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
    }

    public void Dispose()
    {
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct KbdLlHookStruct { public uint VkCode, ScanCode, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MsLlHookStruct { public Point Point; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey; public ushort Scan; public uint Flags; public uint Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int Dx, Dy; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int ShowCursor(bool show);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
