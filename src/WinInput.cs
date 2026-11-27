using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Rhino.Display;

namespace Promenade;

// IWalkInput through user32 hooks for Rhino's UI thread only (WH_KEYBOARD and WH_MOUSE with this thread's id, never a
// global hook, so other apps' input is never seen). Mouse look hides the cursor, clips it to the walk's viewport and
// puts it back in the middle every tick; the look delta is how far it got from there.
// ponytail: cursor recentring, not raw input: Windows pointer acceleration shapes the deltas and a flick faster than
// half the viewport per tick stops at the clip edge. RegisterRawInputDevices + WM_INPUT if that is ever felt.
[SupportedOSPlatform("windows")]
internal sealed class WinInput : IWalkInput
{
    const int WH_KEYBOARD = 2, WH_MOUSE = 7, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_ESCAPE = 0x1B;
    const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203,
        WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_RBUTTONDBLCLK = 0x206, WM_MOUSEWHEEL = 0x20A;
    const double WheelDelta = 120; // per notch

    // Windows virtual-key codes. The punctuation ones (VK_OEM_*) are named for the US layout.
    // ponytail: virtual keys follow the layout, macOS key codes the key position, so on AZERTY WASD are the letters
    // W A S D here but the keys at the WASD positions on a Mac. Map scan codes (lParam bits 16–24) to match.
    static int Vk(WalkKey key) => key switch
    {
        WalkKey.W => 'W', WalkKey.A => 'A', WalkKey.S => 'S', WalkKey.D => 'D', WalkKey.Q => 'Q', WalkKey.E => 'E',
        WalkKey.Space => 0x20, WalkKey.Tab => 0x09, WalkKey.Escape => VK_ESCAPE,
        WalkKey.Left => 0x25, WalkKey.Up => 0x26, WalkKey.Right => 0x27, WalkKey.Down => 0x28,
        WalkKey.Digit1 => '1', WalkKey.Digit2 => '2', WalkKey.Digit3 => '3', WalkKey.Digit4 => '4', WalkKey.Digit5 => '5',
        WalkKey.Minus => 0xBD, WalkKey.Equal => 0xBB, // VK_OEM_MINUS, VK_OEM_PLUS (the =/+ key)
        WalkKey.T => 'T', WalkKey.V => 'V', WalkKey.P => 'P', WalkKey.H => 'H', WalkKey.M => 'M', WalkKey.N => 'N',
        WalkKey.Comma => 0xBC, WalkKey.Period => 0xBE, // VK_OEM_COMMA, VK_OEM_PERIOD
        WalkKey.LeftBracket => 0xDB, WalkKey.RightBracket => 0xDD, // VK_OEM_4, VK_OEM_6
        WalkKey.F => 'F', WalkKey.G => 'G', WalkKey.B => 'B', WalkKey.F3 => 0x72, WalkKey.Grave => 0xC0, // VK_F3, VK_OEM_3
        WalkKey.R => 'R', WalkKey.X => 'X', WalkKey.PageUp => 0x21, WalkKey.PageDown => 0x22, // VK_PRIOR, VK_NEXT
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };
    static readonly Dictionary<int, WalkKey> ByVk = Enum.GetValues<WalkKey>().ToDictionary(Vk);

    delegate nint HookProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEHOOKSTRUCTEX { public POINT Pt; public nint Hwnd; public uint HitTestCode; public nuint ExtraInfo; public uint MouseData; }

    [DllImport("user32.dll", SetLastError = true)] static extern nint SetWindowsHookExW(int idHook, HookProc proc, nint module, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool ClipCursor(ref RECT rect);
    [DllImport("user32.dll", EntryPoint = "ClipCursor")] static extern bool Unclip(nint none);
    [DllImport("user32.dll")] static extern int ShowCursor(bool show);
    [DllImport("user32.dll")] static extern bool GetClientRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(nint hwnd, ref POINT point);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    readonly nint _view; // the walk view's window
    readonly HookProc _keyProc, _mouseProc; // held here so the GC never collects them while user32 calls them
    readonly nint _keyHook, _mouseHook;
    readonly HashSet<WalkKey> _held = [];
    readonly HashSet<WalkKey> _pressed = []; // key-down edges not yet taken
    double _scroll;
    bool _cursorHidden, _disposed, _clicked, _placed, _swallowLeftUp, _blocks;

    public bool Shift => Down(VK_SHIFT);
    public bool MouseLook { get; private set; }
    public bool LeftHeld { get; private set; }
    public bool AppActive => GetWindowThreadProcessId(GetForegroundWindow(), out uint pid) != 0 && pid == (uint)Environment.ProcessId;

    public bool Blocks
    {
        get => _blocks;
        set => (_blocks, LeftHeld, _placed, _clicked) = (value, false, false, false);
    }

    public WinInput(RhinoView view)
    {
        _view = view.Handle;
        _keyProc = OnKey;
        _mouseProc = OnMouse;
        uint thread = GetCurrentThreadId(); // Rhino's UI thread: Walk and the walk's timer both run on it
        _keyHook = SetWindowsHookExW(WH_KEYBOARD, _keyProc, 0, thread);
        _mouseHook = SetWindowsHookExW(WH_MOUSE, _mouseProc, 0, thread);
        try
        {
            if (_keyHook == 0 || _mouseHook == 0)
                throw new InvalidOperationException($"could not hook keyboard and mouse (error {Marshal.GetLastWin32Error()})");
            SetMouseLook(true);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool IsDown(WalkKey key) => _held.Contains(key);

    public bool TakePress(WalkKey key) => _pressed.Remove(key);

    public bool TakeClick()
    {
        bool clicked = _clicked;
        _clicked = false;
        return clicked;
    }

    public bool TakePlace()
    {
        bool placed = _placed;
        _placed = false;
        return placed;
    }

    // Pixels moved since the last tick, divided by the view's scale so the feel matches macOS points.
    public (double X, double Y) TakeMouseDelta()
    {
        if (!MouseLook || !ClipToView(out var centre) || !GetCursorPos(out var p)) return (0, 0);
        SetCursorPos(centre.X, centre.Y);
        double scale = Math.Max(GetDpiForWindow(_view), 96u) / 96.0;
        return ((p.X - centre.X) / scale, (p.Y - centre.Y) / scale);
    }

    public int TakeScrollNotches()
    {
        int notches = (int)_scroll;
        _scroll -= notches;
        return notches;
    }

    static bool Down(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    // Clips the cursor to the view's client area on screen and gives its centre. Done every tick: the view can move
    // or resize, and Windows drops a clip when activation changes.
    bool ClipToView(out POINT centre)
    {
        centre = default;
        if (!GetClientRect(_view, out var client)) return false;
        var (min, max) = (new POINT { X = client.Left, Y = client.Top }, new POINT { X = client.Right, Y = client.Bottom });
        if (!ClientToScreen(_view, ref min) || !ClientToScreen(_view, ref max)) return false;
        var clip = new RECT { Left = min.X, Top = min.Y, Right = max.X, Bottom = max.Y };
        ClipCursor(ref clip);
        centre = new POINT { X = (min.X + max.X) / 2, Y = (min.Y + max.Y) / 2 };
        return true;
    }

    void SetMouseLook(bool on)
    {
        MouseLook = on;
        _scroll = 0;
        _clicked = _placed = LeftHeld = false;
        if (on && ClipToView(out var centre)) SetCursorPos(centre.X, centre.Y); // the first delta starts at rest
        if (!on) Unclip(0);
        if (on != _cursorHidden) ShowCursor(!on); // a display counter: one hide, one show
        _cursorHidden = on;
    }

    // Both hooks are called with HC_ACTION (0) or HC_NOREMOVE (3, a PM_NOREMOVE peek). A message we swallow is
    // removed from the queue either way, so it never comes twice; what a passed one changes is safe to repeat.
    // Returning nonzero swallows the message. An exception must never unwind into user32, so it passes instead.
    nint OnKey(int code, nint wParam, nint lParam)
    {
        try { if (code >= 0 && Key((int)wParam, (long)lParam)) return 1; }
        catch { }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    nint OnMouse(int code, nint wParam, nint lParam)
    {
        try { if (code >= 0 && Mouse((int)wParam, lParam)) return 1; }
        catch { }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    // flags: bit 31 key up, bit 30 already down (auto-repeat), bit 29 Alt held.
    bool Key(int vk, long flags)
    {
        if (!ByVk.TryGetValue(vk, out var key)) return false;
        bool up = (flags & (1L << 31)) != 0;
        if (up) _held.Remove(key); // also under Ctrl/Alt, so a key released with one held never stays "down"
        if (Down(VK_CONTROL) || (flags & (1L << 29)) != 0) return false; // Ctrl/Alt shortcuts are Rhino's (Cmd on macOS)
        // A command is prompting (WalkLens, WalkSun, ...): its digits and Esc are Rhino's. Walk itself
        // returns as soon as the session starts, so walking alone is never "in a command".
        if (Rhino.Commands.Command.InCommand())
        {
            _held.Clear(); // a key released during the prompt must not stay "down"
            _pressed.Clear();
            return false;
        }
        if (!up && (flags & (1L << 30)) == 0)
        {
            _held.Add(key);
            _pressed.Add(key);
        }
        return true; // so WASD doesn't type into the command line (nor Space repeat the last command)
    }

    // Same click mapping as MacInput. Double-click messages are second presses (CS_DBLCLKS windows get them instead).
    bool Mouse(int message, nint info)
    {
        switch (message)
        {
            case WM_RBUTTONDOWN:
            case WM_RBUTTONDBLCLK:
                if (!MouseLook) SetMouseLook(true);
                else if (_blocks) _placed = true;
                else _clicked = true;
                return true; // no context menu / repeat-last-command
            case WM_RBUTTONUP:
                return true;

            case WM_LBUTTONDOWN:
            case WM_LBUTTONDBLCLK:
                if (!MouseLook) return false;
                if (_blocks) LeftHeld = true;
                else SetMouseLook(false);
                _swallowLeftUp = true; // its mouse-up must not reach Rhino on its own
                return true;
            case WM_LBUTTONUP:
                LeftHeld = false;
                if (!_swallowLeftUp) return false;
                _swallowLeftUp = false;
                return true;

            case WM_MOUSEMOVE:
                return MouseLook; // looking reads the cursor itself; a move with a button down would drag Rhino's view

            case WM_MOUSEWHEEL:
                if (!MouseLook) return false;
                _scroll += (short)(Marshal.PtrToStructure<MOUSEHOOKSTRUCTEX>(info).MouseData >> 16) / WheelDelta;
                return true; // otherwise Rhino zooms

            default:
                return false;
        }
    }

    internal static Action WatchEscape(Action onEscape)
    {
        HookProc proc = (code, wParam, lParam) =>
        {
            if (code >= 0 && (int)wParam == VK_ESCAPE && ((long)lParam & (1L << 31)) == 0) onEscape();
            return CallNextHookEx(0, code, wParam, lParam); // Rhino may abort the move in progress too
        };
        nint hook = SetWindowsHookExW(WH_KEYBOARD, proc, 0, GetCurrentThreadId());
        return () =>
        {
            UnhookWindowsHookEx(hook);
            GC.KeepAlive(proc); // alive until unhooked
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { SetMouseLook(false); } // unclips and shows the cursor
        finally
        {
            UnhookWindowsHookEx(_keyHook);
            UnhookWindowsHookEx(_mouseHook);
        }
    }
}
