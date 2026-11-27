using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AppKit;
using Foundation;

namespace Promenade;

// IWalkInput through an NSEvent local monitor (no Accessibility permission needed).
[SupportedOSPlatform("macos")]
internal sealed class MacInput : IWalkInput
{
    // macOS virtual key codes (ANSI positions).
    const ushort KeyA = 0, KeyS = 1, KeyD = 2, KeyQ = 12, KeyW = 13, KeyE = 14,
        KeySpace = 49, KeyTab = 48, KeyEscape = 53, KeyLeft = 123, KeyRight = 124, KeyDown = 125, KeyUp = 126;
    const ushort Key1 = 18, Key2 = 19, Key3 = 20, Key4 = 21, Key5 = 23, KeyMinus = 27, KeyEqual = 24,
        KeyT = 17, KeyV = 9, KeyP = 35, KeyH = 4, KeyM = 46, KeyN = 45, KeyComma = 43, KeyPeriod = 47,
        KeyLeftBracket = 33, KeyRightBracket = 30, KeyF = 3, KeyG = 5, KeyB = 11, KeyR = 15, KeyX = 7,
        KeyPageUp = 116, KeyPageDown = 121;
    // F3 needs "Use F1, F2, etc. keys as standard function keys", or fn; ` is the ANSI grave.
    const ushort KeyF3 = 99, KeyGrave = 50;

    static ushort Code(WalkKey key) => key switch
    {
        WalkKey.W => KeyW, WalkKey.A => KeyA, WalkKey.S => KeyS, WalkKey.D => KeyD, WalkKey.Q => KeyQ, WalkKey.E => KeyE,
        WalkKey.Space => KeySpace, WalkKey.Tab => KeyTab, WalkKey.Escape => KeyEscape,
        WalkKey.Left => KeyLeft, WalkKey.Right => KeyRight, WalkKey.Up => KeyUp, WalkKey.Down => KeyDown,
        WalkKey.Digit1 => Key1, WalkKey.Digit2 => Key2, WalkKey.Digit3 => Key3, WalkKey.Digit4 => Key4, WalkKey.Digit5 => Key5,
        WalkKey.Minus => KeyMinus, WalkKey.Equal => KeyEqual, WalkKey.T => KeyT, WalkKey.V => KeyV, WalkKey.P => KeyP,
        WalkKey.H => KeyH, WalkKey.M => KeyM, WalkKey.N => KeyN, WalkKey.Comma => KeyComma, WalkKey.Period => KeyPeriod,
        WalkKey.LeftBracket => KeyLeftBracket, WalkKey.RightBracket => KeyRightBracket,
        WalkKey.F => KeyF, WalkKey.G => KeyG, WalkKey.B => KeyB, WalkKey.F3 => KeyF3, WalkKey.Grave => KeyGrave,
        WalkKey.R => KeyR, WalkKey.X => KeyX, WalkKey.PageUp => KeyPageUp, WalkKey.PageDown => KeyPageDown,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };
    static readonly HashSet<ushort> HandledKeys = [.. Enum.GetValues<WalkKey>().Select(Code)];

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    static extern int CGAssociateMouseAndMouseCursorPosition(int connected);

    readonly HashSet<ushort> _held = [];
    readonly HashSet<ushort> _pressed = []; // key-down edges not yet taken
    readonly NSObject _monitor;
    readonly NSWindow? _window;
    readonly bool _windowAcceptedMouseMoved;
    double _dx, _dy, _scroll;
    bool _cursorHidden, _disposed, _clicked, _placed, _swallowLeftUp, _blocks;

    public bool Shift { get; private set; }
    public bool MouseLook { get; private set; }
    public bool LeftHeld { get; private set; }
    public bool AppActive => NSApplication.SharedApplication.Active;

    public bool Blocks
    {
        get => _blocks;
        set => (_blocks, LeftHeld, _placed, _clicked) = (value, false, false, false);
    }

    public MacInput()
    {
        Shift = (NSEvent.CurrentModifierFlags & NSEventModifierMask.ShiftKeyMask) != 0;

        // Plain mouse moves are only delivered to windows that ask for them.
        _window = NSApplication.SharedApplication.KeyWindow;
        if (_window is not null)
        {
            _windowAcceptedMouseMoved = _window.AcceptsMouseMovedEvents;
            _window.AcceptsMouseMovedEvents = true;
        }

        const NSEventMask mask = NSEventMask.KeyDown | NSEventMask.KeyUp | NSEventMask.FlagsChanged |
            NSEventMask.MouseMoved | NSEventMask.LeftMouseDragged | NSEventMask.RightMouseDragged |
            NSEventMask.OtherMouseDragged | NSEventMask.RightMouseDown | NSEventMask.RightMouseUp |
            NSEventMask.LeftMouseDown | NSEventMask.LeftMouseUp |
            NSEventMask.ScrollWheel;
        _monitor = NSEvent.AddLocalMonitorForEventsMatchingMask(mask, OnEvent);
        SetMouseLook(true);
    }

    public bool IsDown(WalkKey key) => _held.Contains(Code(key));

    public bool TakePress(WalkKey key) => _pressed.Remove(Code(key));

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

    public (double X, double Y) TakeMouseDelta()
    {
        var delta = (_dx, _dy);
        _dx = _dy = 0;
        return delta;
    }

    // Trackpad fractions accumulate.
    public int TakeScrollNotches()
    {
        int notches = (int)_scroll;
        _scroll -= notches;
        return notches;
    }

    void SetMouseLook(bool on)
    {
        MouseLook = on;
        _dx = _dy = _scroll = 0;
        _clicked = _placed = LeftHeld = false;
        CGAssociateMouseAndMouseCursorPosition(on ? 0 : 1);
        if (on && !_cursorHidden) NSCursor.Hide();
        if (!on && _cursorHidden) NSCursor.Unhide();
        _cursorHidden = on;
    }

    // Rhino takes the event when this returns it; returning null swallows it
    // (the binding's delegate is not annotated nullable, hence null!).
    NSEvent OnEvent(NSEvent e)
    {
        switch (e.Type)
        {
            case NSEventType.KeyDown:
            case NSEventType.KeyUp:
                if ((e.ModifierFlags & NSEventModifierMask.CommandKeyMask) != 0 || !HandledKeys.Contains(e.KeyCode))
                    return e;
                // A command is prompting (WalkLens, WalkSun, ...): its digits and Esc are Rhino's. Walk itself
                // returns as soon as the session starts, so walking alone is never "in a command".
                if (Rhino.Commands.Command.InCommand())
                {
                    _held.Clear(); // a key released during the prompt must not stay "down"
                    _pressed.Clear();
                    return e;
                }
                if (e.Type == NSEventType.KeyUp)
                    _held.Remove(e.KeyCode);
                else if (!e.IsARepeat)
                {
                    _held.Add(e.KeyCode);
                    _pressed.Add(e.KeyCode);
                }
                return null!; // so WASD doesn't type into the command line (nor Space repeat the last command)

            case NSEventType.FlagsChanged:
                Shift = (e.ModifierFlags & NSEventModifierMask.ShiftKeyMask) != 0;
                return e;

            // MouseLook: right click = "there" (teleport to the crosshair), left click = let go of the mouse;
            // with Blocks on, right click = place and the left button breaks while held.
            // FreeCursor: left clicks are Rhino's (toolbars, panels), right click takes the mouse back.
            // A stray left click therefore never moves the walker.
            case NSEventType.RightMouseDown:
                if (!MouseLook) SetMouseLook(true);
                else if (_blocks) _placed = true;
                else _clicked = true;
                return null!; // no context menu / repeat-last-command
            case NSEventType.RightMouseUp:
                return null!;

            case NSEventType.LeftMouseDown:
                if (!MouseLook) return e;
                if (_blocks) LeftHeld = true;
                else SetMouseLook(false);
                _swallowLeftUp = true; // its mouse-up must not reach Rhino on its own
                return null!;
            case NSEventType.LeftMouseUp:
                LeftHeld = false;
                if (!_swallowLeftUp) return e;
                _swallowLeftUp = false;
                return null!;

            case NSEventType.MouseMoved:
            case NSEventType.LeftMouseDragged:
            case NSEventType.RightMouseDragged:
            case NSEventType.OtherMouseDragged:
                if (!MouseLook) return e;
                _dx += e.DeltaX;
                _dy += e.DeltaY;
                return e.Type == NSEventType.MouseMoved ? e : null!; // drags would rotate Rhino's view too

            case NSEventType.ScrollWheel:
                if (!MouseLook) return e;
                _scroll += e.DeltaY;
                return null!; // otherwise Rhino zooms

            default:
                return e;
        }
    }

    internal static Action WatchEscape(Action onEscape)
    {
        var monitor = NSEvent.AddLocalMonitorForEventsMatchingMask(NSEventMask.KeyDown, e =>
        {
            if (e.KeyCode == KeyEscape) onEscape();
            return e; // Rhino may abort the move in progress too
        });
        return () => NSEvent.RemoveMonitor(monitor);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { SetMouseLook(false); }
        finally
        {
            NSEvent.RemoveMonitor(_monitor);
            if (_window is not null) _window.AcceptsMouseMovedEvents = _windowAcceptedMouseMoved;
        }
    }
}
