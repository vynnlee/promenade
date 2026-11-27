using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace Promenade;

// Recording a walk (R), playing it back (WalkPlay) and rendering it to frames or video (WalkRender).
// A recording is a curve along the eye path on the "Promenade Walks" layer (hidden), named "Walk recording NN",
// with its camera samples as text in a user string (RecordMath.Encode), so it lives in the .3dm.
internal static class RecordTools
{
    internal const string Key = "Promenade.WalkRecording", LayerName = "Promenade Walks", Prefix = "Walk recording ";

    static readonly List<WalkSample> _samples = [];
    static readonly Stopwatch Clock = new();
    static readonly RecConduit _conduit = new() { Enabled = true };
    static bool _recording;
    static double _next;
    static int _shownSecond = -1;
    static int _fps = 30, _longSide = 1920; // WalkRender options, kept for the next render

    internal record Recording(RhinoObject Object, string Name, int Number);

    // Called every walk tick before the walker moves; takes only this file's keys.
    internal static void OnTick(WalkSession s)
    {
        if (s.Input.TakePress(WalkKey.R))
        {
            if (_recording) Finish(s);
            else
            {
                (_recording, _next, _shownSecond) = (true, 0, -1);
                _samples.Clear();
                Clock.Restart();
                _conduit.Enabled = true;
                s.View.ShowToast("Recording (R to stop)");
            }
        }
        // The REC clock ticks on screen even when standing still.
        if (_recording && _conduit.Enabled && (int)Clock.Elapsed.TotalSeconds != _shownSecond) s.View.Redraw();
    }

    // Right after the camera moved, every tick: one sample per 1/30 s.
    internal static void AfterMove(WalkSession s)
    {
        if (!_recording) return;
        double t = Clock.Elapsed.TotalSeconds;
        if (t < _next) return;
        _next = Math.Max(_next + 1 / RecordMath.SampleRate, t);
        var vp = s.View.ActiveViewport;
        var eye = s.Eye;
        _samples.Add(new WalkSample(t, eye.X, eye.Y, eye.Z, s.Yaw, s.Pitch, ViewTools.Lens(vp), vp.IsTwoPointPerspectiveProjection));
    }

    internal static void OnStop(WalkSession s)
    {
        if (_recording) Finish(s);
    }

    // Never throws: it runs inside the walk's tick or its Stop.
    static void Finish(WalkSession s)
    {
        _recording = false;
        string toast;
        try
        {
            double seconds = _samples.Count < 2 ? 0 : _samples[^1].T - _samples[0].T;
            if (s.Doc.IsClosing) toast = "Recording not saved (document closing)"; // its save prompt has passed
            else if (seconds < RecordMath.MinSeconds) toast = "Recording too short, not saved";
            else
            {
                toast = $"Recording saved: {Save(s.Doc, _samples)} ({RecordMath.Clock(seconds)})";
                RhinoApp.WriteLine($"Promenade: {toast}. WalkPlay plays it, WalkRender renders it.");
            }
        }
        catch (Exception ex)
        {
            toast = "Recording not saved";
            RhinoApp.WriteLine($"Promenade: could not save the recording: {ex.Message}");
        }
        finally
        {
            _samples.Clear();
        }
        try
        {
            s.View.Redraw(); // clears the REC mark
            s.View.ShowToast(toast);
        }
        catch { } // the walk may be ending because its view is gone
    }

    static string Save(RhinoDoc doc, List<WalkSample> samples)
    {
        int layer = doc.Layers.FindName(LayerName)?.Index ?? -1;
        if (layer < 0) layer = doc.Layers.Add(new Layer { Name = LayerName, Color = Color.FromArgb(0xf0, 0x3e, 0x3e), IsVisible = false });
        if (layer < 0) throw new InvalidOperationException("could not add the layer " + LayerName);
        var list = Recordings(doc);
        string name = $"{Prefix}{(list.Count == 0 ? 1 : list[^1].Number + 1):00}";
        var attributes = new ObjectAttributes { LayerIndex = layer, Name = name };
        attributes.SetUserString(Key, RecordMath.Encode(samples));

        // The eye path, without repeats (standing still); a point if it never moved.
        double tolerance = 0.001 * RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
        var path = new Polyline();
        foreach (var p in samples.Select(p => new Point3d(p.X, p.Y, p.Z)))
            if (path.Count == 0 || path[^1].DistanceTo(p) > tolerance) path.Add(p);
        var id = path.Count >= 2 ? doc.Objects.AddPolyline(path, attributes) : doc.Objects.AddPoint(path[0], attributes);
        if (id == Guid.Empty) throw new InvalidOperationException("Rhino did not add the curve");
        return name;
    }

    // Every recording in doc (hidden ones and those on hidden layers too), oldest number first.
    internal static List<Recording> Recordings(RhinoDoc doc)
    {
        var settings = new ObjectEnumeratorSettings { HiddenObjects = true, LockedObjects = true, NormalObjects = true };
        var list = new List<Recording>();
        foreach (var obj in doc.Objects.GetObjectList(settings))
        {
            if (string.IsNullOrEmpty(obj.Attributes.GetUserString(Key))) continue;
            string name = obj.Attributes.Name ?? "";
            int n = name.StartsWith(Prefix, StringComparison.Ordinal) &&
                    int.TryParse(name.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int k) ? k : 0;
            list.Add(new Recording(obj, name, n));
        }
        return list.OrderBy(r => r.Number).ToList();
    }

    // Asks for a recording by name or number (Enter: the newest) and reads its samples.
    internal static Result Pick(RhinoDoc doc, string what, out Recording? recording, out List<WalkSample> samples)
    {
        (recording, samples) = (null, []);
        var list = Recordings(doc);
        if (list.Count == 0)
        {
            RhinoApp.WriteLine("Promenade: no walk recordings yet. Press R while walking to start and stop one.");
            return Result.Nothing;
        }
        RhinoApp.WriteLine("Recordings: " + string.Join(", ", list.Select(r => r.Name)));
        var gs = new GetString();
        gs.SetCommandPrompt($"Recording to {what} (name or number; Enter for the newest)");
        gs.SetDefaultString(list[^1].Name);
        gs.AcceptNothing(true);
        var got = gs.Get();
        if (got != GetResult.String && got != GetResult.Nothing) return gs.CommandResult();
        string text = got == GetResult.String ? gs.StringResult().Trim() : "";
        recording = text.Length == 0 ? list[^1]
            : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? list.LastOrDefault(r => r.Number == n)
            : list.LastOrDefault(r => string.Equals(r.Name, text, StringComparison.OrdinalIgnoreCase));
        if (recording is null) { RhinoApp.WriteLine($"Promenade: no recording named \"{text}\"."); return Result.Failure; }
        samples = RecordMath.Decode(recording.Object.Attributes.GetUserString(Key));
        RecordMath.Unwrap(samples);
        if (samples.Count < 2) { RhinoApp.WriteLine($"Promenade: {recording.Name} has no readable samples."); return Result.Failure; }
        return Result.Success;
    }

    // Puts a (smoothed) recorded pose on view, as WalkSession.Apply does, plus its lens and two-point state.
    static void ApplyPose(RhinoView view, WalkSample p, double unit)
    {
        var vp = view.ActiveViewport;
        if (p.TwoPoint && !vp.IsTwoPointPerspectiveProjection) vp.ChangeToTwoPointPerspectiveProjection(50.0);
        else if (!p.TwoPoint && (vp.IsTwoPointPerspectiveProjection || !vp.IsPerspectiveProjection)) vp.ChangeToPerspectiveProjection(true, 50.0);
        var (x, y, z) = WalkPhysics.LookDirection(p.Yaw, p.TwoPoint ? 0 : p.Pitch);
        var eye = new Point3d(p.X, p.Y, p.Z);
        vp.SetCameraLocations(eye + new Vector3d(x, y, z) * unit, eye);
        vp.CameraUp = Vector3d.ZAxis;
        if (p.TwoPoint) ViewTools.ShiftFrame(vp, p.Pitch);
        if (Math.Abs(ViewTools.Lens(vp) - p.Lens) > 0.01) ViewTools.SetLens(view, p.Lens, quiet: true);
    }

    // Plays samples in real time in the active view; Esc stops. The camera stays where it ended (like WalkTour).
    internal static Result Play(RhinoDoc doc, Recording recording, List<WalkSample> samples)
    {
        WalkSession.Current?.Stop();
        if (doc.Views.ActiveView is not { } view) return Result.Failure;
        double unit = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
        double t0 = samples[0].T, length = samples[^1].T - t0;
        bool cancel = false;
        var stopWatching = IWalkInput.WatchEscape(() => cancel = true);
        try
        {
            RhinoApp.WriteLine($"WalkPlay: {recording.Name} ({RecordMath.Clock(length)}), Esc to stop.");
            var clock = Stopwatch.StartNew();
            for (double next = 0; !cancel;)
            {
                double t = clock.Elapsed.TotalSeconds;
                if (t >= next)
                {
                    next = t + 1 / 60.0;
                    ApplyPose(view, RecordMath.At(samples, t0 + Math.Min(t, length)), unit);
                    view.Redraw();
                    if (t >= length) return Result.Success;
                }
                RhinoApp.Wait(); // keeps events (and Esc) coming
                Thread.Sleep(1);
            }
            RhinoApp.WriteLine("WalkPlay: stopped.");
            return Result.Cancel;
        }
        finally
        {
            stopWatching();
        }
    }

    // Renders samples frame by frame (smoothed) to PNGs at the WalkFrame aspect (else the viewport's) with
    // the long side longSide; on macOS then encodes them to an H.264 .mp4 and deletes them if that worked.
    internal static Result Render(RhinoDoc doc, Recording recording, List<WalkSample> samples, int fps, int longSide)
    {
        WalkSession.Current?.Stop();
        if (doc.Views.ActiveView is not { } view) return Result.Failure;
        double unit = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
        double t0 = samples[0].T, length = samples[^1].T - t0;
        int frames = (int)Math.Floor(length * fps) + 1;
        var vpSize = view.ActiveViewport.Size;
        var (w, h) = FrameTools.Frame(doc) is { } f ? RecordMath.VideoSize(f.Width, f.Height, longSide)
                                                  : RecordMath.VideoSize(vpSize.Width, vpSize.Height, longSide);
        var image = new Size(w, h);

        // <document folder>/<document> - Walk recording 03/, or ~/Pictures/Promenade/Walk recording 03/; "-2", …
        // if that folder (or its video) is already there.
        string stem = ViewTools.OutputFolder(doc, recording.Name);
        if (string.IsNullOrEmpty(doc.Path)) stem = Path.Combine(stem, recording.Name);
        string folder = stem;
        for (int k = 2; Directory.Exists(folder) || File.Exists(folder + ".mp4"); k++) folder = $"{stem}-{k}";
        string video = folder + ".mp4";
        Directory.CreateDirectory(folder);

        bool cancel = false;
        var stopWatching = IWalkInput.WatchEscape(() => cancel = true);
        var files = new List<string>();
        Rhino.UI.StatusBar.ShowProgressMeter(0, frames, "WalkRender", true, true);
        try
        {
            RhinoApp.WriteLine($"WalkRender: {recording.Name}, {frames} frames at {fps} fps, {w}×{h}, into {folder}. Esc to stop.");
            for (int i = 0; i < frames && !cancel; i++)
            {
                ApplyPose(view, RecordMath.At(samples, t0 + (double)i / fps), unit);
                using (var bitmap = ViewTools.Capture(view, image))
                {
                    if (bitmap is null) { RhinoApp.WriteLine($"WalkRender: Rhino could not capture frame {i + 1}."); break; }
                    string file = Path.Combine(folder, $"frame_{i + 1:00000}.png");
                    bitmap.Save(file, ImageFormat.Png);
                    files.Add(file);
                }
                if (i % 10 == 0) view.Redraw(); // a glimpse of where it is, without paying a redraw per frame
                Rhino.UI.StatusBar.UpdateProgressMeter(i + 1, true);
                RhinoApp.SetCommandPrompt($"WalkRender: frame {i + 1} of {frames} (Esc to stop)");
                RhinoApp.Wait();
            }
            view.Redraw();

            string ffmpeg = $"ffmpeg -framerate {fps} -i \"{Path.Combine(folder, "frame_%05d.png")}\" -c:v libx264 -pix_fmt yuv420p -crf 18 \"{video}\"";
            if (files.Count < frames)
            {
                RhinoApp.WriteLine($"WalkRender: stopped after {files.Count} of {frames} frames; they are in {folder}");
                if (files.Count > 0) RhinoApp.WriteLine($"WalkRender: to make a video of them: {ffmpeg}");
                return cancel ? Result.Cancel : Result.Failure;
            }
            if (!OperatingSystem.IsMacOS())
            {
                RhinoApp.WriteLine($"WalkRender: {frames} frames saved in {folder}");
                RhinoApp.WriteLine($"WalkRender: to make a video: {ffmpeg}");
                return Result.Success;
            }

            Rhino.UI.StatusBar.UpdateProgressMeter(0, true);
            string? error = EncodeMac(files, w, h, fps, video, i =>
            {
                Rhino.UI.StatusBar.UpdateProgressMeter(i + 1, true);
                RhinoApp.SetCommandPrompt($"WalkRender: encoding frame {i + 1} of {frames} (Esc to stop)");
                RhinoApp.Wait();
                return !cancel;
            });
            if (error is not null)
            {
                RhinoApp.WriteLine($"WalkRender: video not made ({error}); the {frames} frames are in {folder}");
                RhinoApp.WriteLine($"WalkRender: to make one with ffmpeg: {ffmpeg}");
                return cancel ? Result.Cancel : Result.Failure;
            }
            foreach (var file in files) File.Delete(file);
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            view.ShowToast($"Saved {Path.GetFileName(video)}");
            RhinoApp.WriteLine($"WalkRender: saved {video} ({w}×{h}, {fps} fps, {RecordMath.Clock(length)})");
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"WalkRender: failed: {ex.Message}. Frames done so far are in {folder}");
            return Result.Failure;
        }
        finally
        {
            stopWatching();
            Rhino.UI.StatusBar.HideProgressMeter();
            RhinoApp.SetCommandPrompt("Command");
        }
    }

    // Out of line, so no method compiled on Windows mentions MacVideo (its code loads Microsoft.macOS).
    [SupportedOSPlatform("macos"), MethodImpl(MethodImplOptions.NoInlining)]
    static string? EncodeMac(List<string> files, int w, int h, int fps, string path, Func<int, bool> progress) =>
        MacVideo.Encode(files, w, h, fps, path, progress);

    internal static Result RenderCommand(RhinoDoc doc)
    {
        var picked = Pick(doc, "render", out var recording, out var samples);
        if (picked != Result.Success) return picked;
        var go = new GetOption();
        go.SetCommandPrompt("Video settings (Enter to start rendering)");
        go.AcceptNothing(true);
        var fps = new OptionInteger(_fps, 1, 120);
        var longSide = new OptionInteger(_longSide, 256, 4096); // H.264 in AVFoundation stops at 4096
        go.AddOptionInteger("FramesPerSecond", ref fps, "Frames per second (24, 30 or 60)");
        go.AddOptionInteger("LongSide", ref longSide, "Long side of the video in pixels");
        while (true)
        {
            var got = go.Get();
            if (got == GetResult.Option) continue;
            if (got != GetResult.Nothing) return go.CommandResult();
            break;
        }
        (_fps, _longSide) = (fps.CurrentValue, longSide.CurrentValue);
        return Render(doc, recording!, samples, _fps, _longSide);
    }

    // The REC mark in the walk view's bottom-left corner while recording; never in captures.
    sealed class RecConduit : DisplayConduit
    {
        static readonly Color Red = Color.FromArgb(0xf0, 0x3e, 0x3e), Back = Color.FromArgb(140, 0, 0, 0);
        const string Font = "Helvetica Neue";

        protected override void DrawForeground(DrawEventArgs e)
        {
            try
            {
                if (!_recording || e.Display.IsInViewCapture || e.Display.IsPrinting) return;
                if (WalkSession.Current is not { } s || e.Viewport.Id != s.View.ActiveViewportID) return;
                float k = (float)Math.Max(e.Display.DpiScale, 1); // points to backing pixels
                _shownSecond = (int)Clock.Elapsed.TotalSeconds;
                string text = "REC " + RecordMath.Clock(_shownSecond);
                int height = (int)Math.Round(12 * k);
                var box = e.Display.Measure2dText(text, Point2d.Origin, false, 0, height, Font);
                float r = 4 * k, pad = 6 * k, pillH = Math.Max(box.Height, 2 * r) + pad, pillW = pad + 2 * r + pad + box.Width + pad;
                float left = 10 * k, cy = e.Viewport.Size.Height - 10 * k - pillH / 2;
                e.Display.DrawRoundedRectangle(new PointF(left + pillW / 2, cy), pillW, pillH, pillH / 2, Color.Transparent, 0, Back);
                e.Display.DrawRoundedRectangle(new PointF(left + pad + r, cy), 2 * r, 2 * r, r, Color.Transparent, 0, Red);
                float tx = left + pad + 2 * r + pad;
                e.Display.Draw2dText(text, Color.White, new Point2d(tx - box.Left, cy - box.Height / 2f - box.Top), false, height, Font);
            }
            catch (Exception ex) // never throw (or spam) from a draw callback
            {
                Enabled = false;
                RhinoApp.WriteLine($"Promenade: REC mark turned off after an error: {ex.Message}");
            }
        }
    }
}

public sealed class WalkPlayCommand : Command
{
    public override string EnglishName => "WalkPlay";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var picked = RecordTools.Pick(doc, "play", out var recording, out var samples);
        return picked == Result.Success ? RecordTools.Play(doc, recording!, samples) : picked;
    }
}

public sealed class WalkRenderCommand : Command
{
    public override string EnglishName => "WalkRender";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => RecordTools.RenderCommand(doc);
}
