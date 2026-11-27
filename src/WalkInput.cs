using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Rhino.Display;

namespace Promenade;

// The keys a walk handles, whatever the OS calls them. Move: WASD/arrows, turn Q/E, jump Space, fly Tab, exit Esc.
// View: lens 1–5, lens zoom -/=, two-point T, display mode V, screenshot P. Body: eye height H, measure M.
// Scene: save view N, previous/next saved view , and ., sun time [ and ]. Frame: aspect F, guides G.
// Block mode B. Debug HUD F3 and ` (grave). Record R, clip X, floor up/down Page Up/Page Down.
internal enum WalkKey
{
    W, A, S, D, Q, E, Space, Tab, Escape, Left, Right, Up, Down,
    Digit1, Digit2, Digit3, Digit4, Digit5, Minus, Equal, T, V, P, H, M, N, Comma, Period,
    LeftBracket, RightBracket, F, G, B, F3, Grave, R, X, PageUp, PageDown,
}

// Keyboard and mouse for a running walk: MacInput on macOS, WinInput on Windows. Both swallow the keys above
// while walking. MouseLook (cursor locked + hidden): left click frees the cursor, right click teleports;
// with Blocks on, left button held = break, right click = place instead. FreeCursor: left clicks go to Rhino,
// right click returns to MouseLook.
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

    static IWalkInput Create(RhinoView view) => OperatingSystem.IsWindows() ? new WinInput(view) : Mac();

    // Calls onEscape on every Esc key-down (the key still reaches Rhino) until the returned action is called.
    // For WalkTour, which runs outside a walk.
    static Action WatchEscape(Action onEscape) =>
        OperatingSystem.IsWindows() ? WinInput.WatchEscape(onEscape) : MacEscape(onEscape);

    // Out of line, so no method compiled on Windows mentions MacInput (its code loads Microsoft.macOS).
    [SupportedOSPlatform("macos"), MethodImpl(MethodImplOptions.NoInlining)]
    private static IWalkInput Mac() => new MacInput();

    [SupportedOSPlatform("macos"), MethodImpl(MethodImplOptions.NoInlining)]
    private static Action MacEscape(Action onEscape) => MacInput.WatchEscape(onEscape);
}
