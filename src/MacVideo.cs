using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using AVFoundation;
using CoreGraphics;
using CoreMedia;
using CoreVideo;
using Foundation;

namespace Promenade;

// PNG frames → H.264 .mp4 with AVFoundation (AVAssetWriter + a pixel buffer adaptor). Each PNG is read back
// with CoreGraphics and drawn into a BGRA CVPixelBuffer, so no System.Drawing pixel access is needed.
[SupportedOSPlatform("macos")]
internal static class MacVideo
{
    // Null when the video was written; else why not (a half-written file is removed). progress(i) is called
    // after frame i and returns false to cancel.
    internal static string? Encode(IReadOnlyList<string> frames, int width, int height, int fps, string path, Func<int, bool> progress)
    {
        using var url = NSUrl.FromFilename(path);
        using var writer = new AVAssetWriter(url, AVFileTypes.Mpeg4.GetConstant()!, out var error);
        if (error is not null) return error.LocalizedDescription;
        var settings = new AVVideoSettingsCompressed
        {
            Codec = AVVideoCodec.H264,
            Width = width,
            Height = height,
            // ~9 Mbit/s at 1080p30: crisp lines and textures; AVFoundation's default is softer.
            CodecSettings = new AVVideoCodecSettings { AverageBitRate = (int)Math.Min(int.MaxValue, 0.15 * width * height * fps) },
        };
        using var input = new AVAssetWriterInput(AVMediaTypes.Video.GetConstant()!, settings) { ExpectsMediaDataInRealTime = false };
        using var adaptor = new AVAssetWriterInputPixelBufferAdaptor(input,
            new CVPixelBufferAttributes { PixelFormatType = CVPixelFormatType.CV32BGRA, Width = width, Height = height });
        if (!writer.CanAddInput(input)) return "the H.264 writer refused the settings";
        writer.AddInput(input);
        if (!writer.StartWriting()) return writer.Error?.LocalizedDescription ?? "could not start writing";
        writer.StartSessionAtSourceTime(CMTime.Zero);

        string? failed = null;
        using var space = CGColorSpace.CreateSrgb();
        for (int i = 0; i < frames.Count && failed is null; i++)
        {
            using var pool = new NSAutoreleasePool();
            for (var wait = System.Diagnostics.Stopwatch.StartNew(); !input.ReadyForMoreMediaData;)
            {
                if (wait.Elapsed.TotalSeconds > 10) { failed = "the encoder stopped taking frames"; break; }
                Thread.Sleep(2);
            }
            if (failed is not null) break;
            using var provider = new CGDataProvider(frames[i]);
            using var image = CGImage.FromPNG(provider, null, false, CGColorRenderingIntent.Default);
            using var buffer = adaptor.PixelBufferPool?.CreatePixelBuffer();
            if (image is null || buffer is null) { failed = $"could not read frame {i + 1}"; break; }
            buffer.Lock(CVPixelBufferLock.None);
            try
            {
                using var context = new CGBitmapContext(buffer.BaseAddress, width, height, 8, buffer.BytesPerRow, space,
                    CGBitmapFlags.PremultipliedFirst | CGBitmapFlags.ByteOrder32Little); // BGRA in memory
                context.DrawImage(new CGRect(0, 0, width, height), image);
            }
            finally
            {
                buffer.Unlock(CVPixelBufferLock.None);
            }
            if (!adaptor.AppendPixelBufferWithPresentationTime(buffer, new CMTime(i, fps)))
                failed = writer.Error?.LocalizedDescription ?? $"could not add frame {i + 1}";
            else if (!progress(i)) failed = "stopped";
        }

        if (failed is not null)
        {
            writer.CancelWriting(); // also removes the partial file
            return failed;
        }
        input.MarkAsFinished();
        using var done = new ManualResetEventSlim();
        writer.FinishWriting(() => done.Set()); // the handler runs on another queue
        if (!done.Wait(TimeSpan.FromMinutes(2))) return "the video did not finish";
        return writer.Status == AVAssetWriterStatus.Completed ? null : writer.Error?.LocalizedDescription ?? "the video did not finish";
    }
}
