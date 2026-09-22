using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Notlar;

// Grabs one frame of a video through WPF's media pipeline (Media Foundation) and returns it as a small JPEG.
// Null when Windows cannot decode the file or nothing arrives within a few seconds.
public static class VideoThumbnail
{
    public static async Task<byte[]?> FirstFrameAsync(string path, int width = 320)
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
            double scale = (double)width / player.NaturalVideoWidth; int height = Math.Max(1, (int)(player.NaturalVideoHeight * scale));
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawVideo(player, new Rect(0, 0, width, height));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            if (IsBlank(bitmap)) { await Task.Delay(600); bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); if (IsBlank(bitmap)) return null; }
            var encoder = new JpegBitmapEncoder { QualityLevel = 70 }; encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
        }
        finally { player.Close(); }
    }
    // A frame that has not arrived yet renders fully transparent or black.
    private static bool IsBlank(BitmapSource bitmap)
    {
        int stride = bitmap.PixelWidth * 4; var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
        long sum = 0; for (int i = 0; i < pixels.Length; i += 4) sum += pixels[i] + pixels[i + 1] + pixels[i + 2];
        return sum < pixels.Length / 4 * 3 * 2;
    }
}
