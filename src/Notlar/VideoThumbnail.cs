using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Notlar;

// Grabs one frame of a video through WPF's media pipeline (Media Foundation) and returns it as a small JPEG.
// Null when Windows cannot decode the file or nothing arrives within a few seconds.
public static class VideoThumbnail
{
    private const int Width = 320;
    public static async Task<byte[]?> FirstFrameAsync(string path)
    {
        var player = new MediaPlayer { Volume = 0, ScrubbingEnabled = true };
        var opened = new TaskCompletionSource<bool>();
        player.MediaOpened += (_, _) => opened.TrySetResult(true);
        player.MediaFailed += (_, _) => opened.TrySetResult(false);
        try
        {
            player.Open(new Uri(path));
            if (await Task.WhenAny(opened.Task, Task.Delay(5000)) != opened.Task || !opened.Task.Result || player.NaturalVideoWidth == 0) return null;
            player.Position = TimeSpan.FromMilliseconds(Math.Min(300, player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalMilliseconds / 2 : 300));
            await Task.Delay(400);
            int height = Math.Max(1, (int)(player.NaturalVideoHeight * ((double)Width / player.NaturalVideoWidth)));
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawVideo(player, new Rect(0, 0, Width, height));
            RenderTargetBitmap Shot() { var b = new RenderTargetBitmap(Width, height, 96, 96, PixelFormats.Pbgra32); b.Render(visual); return b; }
            // The frame arrives when it arrives: look every 60 ms rather than sleeping for the worst case.
            var bitmap = Shot();
            for (int waited = 0; waited < 1200 && IsBlank(bitmap); waited += 60) { await Task.Delay(60); bitmap = Shot(); }
            if (IsBlank(bitmap)) return null;
            var encoder = new JpegBitmapEncoder { QualityLevel = 70 }; encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
        }
        finally { player.Close(); }
    }
    // A frame that has not arrived yet renders fully transparent or black. Every sixteenth pixel of one row in
    // sixteen says that as well as all of them, at a sixteenth of the copying.
    private static bool IsBlank(BitmapSource bitmap)
    {
        int stride = bitmap.PixelWidth * 4; var row = new byte[stride]; long sum = 0, counted = 0;
        for (int y = 0; y < bitmap.PixelHeight; y += 16)
        {
            bitmap.CopyPixels(new Int32Rect(0, y, bitmap.PixelWidth, 1), row, stride, 0);
            for (int i = 0; i < stride; i += 64) { sum += row[i] + row[i + 1] + row[i + 2]; counted++; }
        }
        return sum < counted * 6;
    }
}
