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
    static readonly ushort KeyEscape = KeyMap.MacCode("Escape");

    readonly Dictionary<WalkKey, ushort> _codes; // positional: see KeyMap
    readonly HashSet<ushort> _handled; // swallowed while walking

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

    public MacInput(KeyMap keys)
    {
        _codes = Enum.GetValues<WalkKey>().ToDictionary(k => k, k => KeyMap.MacCode(keys[k]));
        _handled = [.. _codes.Values];
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

    public bool IsDown(WalkKey key) => _held.Contains(_codes[key]);

    public bool TakePress(WalkKey key) => _pressed.Remove(_codes[key]);

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
                if ((e.ModifierFlags & NSEventModifierMask.CommandKeyMask) != 0 || !_handled.Contains(e.KeyCode))
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
