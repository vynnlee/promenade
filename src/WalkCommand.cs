using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Rhino;
using Rhino.Commands;
using Rhino.PlugIns;

[assembly: Guid("EA7BAEA5-2D20-4842-A9C5-1B12A6CF4E3A")]
[assembly: PlugInDescription(DescriptionType.Organization, "Seongbeen Lee")]
[assembly: PlugInDescription(DescriptionType.WebSite, "https://github.com/vynnlee/promenade")]
[assembly: PlugInDescription(DescriptionType.Country, "South Korea")]
[assembly: PlugInDescription(DescriptionType.Icon, "Promenade.EmbeddedResources.plugin.ico")]
[assembly: SupportedOSPlatform("macos"), SupportedOSPlatform("windows")]

namespace Promenade;

public sealed class PromenadePlugIn : PlugIn
{
    protected override void OnShutdown()
    {
        WalkSession.Current?.Stop();
        base.OnShutdown();
    }
}

public sealed class WalkCommand : Command
{
    public override string EnglishName => "Walk";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        // Running Walk again exits (and restores the cursor) instead of starting a second session.
        if (WalkSession.Current is { } running)
        {
            running.Stop();
            return Result.Success;
        }

        var view = doc.Views.ActiveView;
        if (view is null)
        {
            RhinoApp.WriteLine("Promenade: no active view.");
            return Result.Failure;
        }

        if (!view.ActiveViewport.IsPerspectiveProjection)
        {
            view.ActiveViewport.ChangeToPerspectiveProjection(true, 50.0);
            RhinoApp.WriteLine("Promenade: switched the view to perspective.");
        }

        try
        {
            WalkSession.Start(doc, view);
        }
        catch (Exception ex) // e.g. the Jolt native library failed to load
        {
            RhinoApp.WriteLine($"Promenade: could not start: {ex.Message}");
            return Result.Failure;
        }
        return Result.Success;
    }
}

public sealed class WalkKeysCommand : Command
{
    public override string EnglishName => "WalkKeys";

    // Writes the default keymap.json if there is none and opens it; the next walk reads it.
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        string path = KeyMap.FilePath;
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, KeyMap.DefaultJson());
            }
            RhinoApp.WriteLine($"Promenade: keymap {path} (read at every walk start)");
            using var editor = OperatingSystem.IsMacOS() ? Process.Start("open", ["-t", path]) // the default text editor
                : Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"Promenade: keymap {path}: {ex.Message}");
            return Result.Failure;
        }
        return Result.Success;
    }
}
