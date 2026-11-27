using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Rhino.Display;

namespace Promenade;

// Keyboard and mouse for a running walk: MacInput on macOS, WinInput on Windows. Both swallow the WalkKeys' keys
// (the KeyMap's) while walking. MouseLook (cursor locked + hidden): left click frees the cursor, right click
// teleports; with Blocks on, left button held = break, right click = place instead. FreeCursor: left clicks go to
// Rhino, right click returns to MouseLook.
internal interface IWalkInput : IDisposable
{
    bool Shift { get; }
    bool MouseLook { get; }
    bool LeftHeld { get; } // Blocks + MouseLook: the left button is down (breaking)
    bool Blocks { get; set; } // Block mode's click mapping in MouseLook (FreeCursor is the same either way)
    bool AppActive { get; } // Rhino is the frontmost app; losing it ends the walk
    bool IsDown(WalkKey key);
    bool TakePress(WalkKey key);
    bool TakeClick(); // a right click made in MouseLook (the cursor is hidden, so it means "at the crosshair")
    bool TakePlace(); // a right click made in MouseLook with Blocks on: place a block at the crosshair
    (double X, double Y) TakeMouseDelta(); // in points, + = right / down
    int TakeScrollNotches(); // whole notches since the last call, + = wheel up

    static IWalkInput Create(RhinoView view, KeyMap keys) => OperatingSystem.IsWindows() ? new WinInput(view, keys) : Mac(keys);

    // Calls onEscape on every Esc key-down (the key still reaches Rhino) until the returned action is called.
    // For WalkTour, which runs outside a walk.
    static Action WatchEscape(Action onEscape) =>
        OperatingSystem.IsWindows() ? WinInput.WatchEscape(onEscape) : MacEscape(onEscape);

    // Out of line, so no method compiled on Windows mentions MacInput (its code loads Microsoft.macOS).
    [SupportedOSPlatform("macos"), MethodImpl(MethodImplOptions.NoInlining)]
    private static IWalkInput Mac(KeyMap keys) => new MacInput(keys);

    [SupportedOSPlatform("macos"), MethodImpl(MethodImplOptions.NoInlining)]
    private static Action MacEscape(Action onEscape) => MacInput.WatchEscape(onEscape);
}
