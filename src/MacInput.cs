using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AppKit;
using Foundation;

namespace Promenade;

// Keyboard and mouse through an NSEvent local monitor (no Accessibility permission needed).
// Swallows the keys we use. MouseLook (cursor locked + hidden): left click frees the cursor, right click teleports;
// with Blocks on, left button held = break, right click = place instead. FreeCursor: left clicks go to Rhino,
// right click returns to MouseLook.
internal sealed class MacInput : IDisposable
{
    // macOS virtual key codes. Move: WASD/arrows, turn Q/E, jump Space, fly Tab, exit Esc.
    public const ushort KeyA = 0, KeyS = 1, KeyD = 2, KeyQ = 12, KeyW = 13, KeyE = 14,
        KeySpace = 49, KeyTab = 48, KeyEscape = 53, KeyLeft = 123, KeyRight = 124, KeyDown = 125, KeyUp = 126;
    // Feature keys. View: lens 1–5, lens zoom -/=, two-point T, display mode V, screenshot P.
    // Body: eye height H, measure M (teleport is a right click in MouseLook). Scene: save view N,
    // previous/next saved view , and ., sun time [ and ]. Frame: aspect F, composition guides G.
    public const ushort Key1 = 18, Key2 = 19, Key3 = 20, Key4 = 21, Key5 = 23, KeyMinus = 27, KeyEqual = 24,
        KeyT = 17, KeyV = 9, KeyP = 35, KeyH = 4, KeyM = 46, KeyN = 45, KeyComma = 43, KeyPeriod = 47,
        KeyLeftBracket = 33, KeyRightBracket = 30, KeyF = 3, KeyG = 5;
    // Block mode on/off (BlockMode).
    public const ushort KeyB = 11;
    // Debug HUD: F3 (needs "Use F1, F2, etc. keys as standard function keys", or fn) and ` (ANSI grave).
    public const ushort KeyF3 = 99, KeyGrave = 50;
    static readonly HashSet<ushort> HandledKeys =
        [KeyA, KeyS, KeyD, KeySpace, KeyQ, KeyW, KeyE, KeyTab, KeyEscape, KeyLeft, KeyRight, KeyDown, KeyUp,
         Key1, Key2, Key3, Key4, Key5, KeyMinus, KeyEqual, KeyT, KeyV, KeyP, KeyH, KeyM, KeyN,
         KeyComma, KeyPeriod, KeyLeftBracket, KeyRightBracket, KeyF, KeyG, KeyF3, KeyGrave, KeyB];

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
    public bool LeftHeld { get; private set; } // Blocks + MouseLook: the left button is down (breaking)

    // Block mode's click mapping in MouseLook (FreeCursor is the same either way).
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

    public bool IsDown(ushort keyCode) => _held.Contains(keyCode);

    public bool TakePress(ushort keyCode) => _pressed.Remove(keyCode);

    // A right click made in MouseLook (the cursor is hidden, so it means "there, at the crosshair").
    public bool TakeClick()
    {
        bool clicked = _clicked;
        _clicked = false;
        return clicked;
    }

    // A right click made in MouseLook with Blocks on: place a block at the crosshair.
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

    // Whole scroll notches since the last call (+ = wheel up). Trackpad fractions accumulate.
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
