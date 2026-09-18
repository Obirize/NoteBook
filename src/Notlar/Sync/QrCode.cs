using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Notlar.Sync;

// A small QR encoder (ISO/IEC 18004): byte mode, error correction level M, versions 1–15 (up to 412 bytes).
// Written here so the app keeps its "no dependencies" rule; the pairing screen is the only user.
public sealed class QrCode
{
    public int Size { get; }
    private readonly bool[,] modules;
    private readonly bool[,] reserved;
    public bool this[int x, int y] => modules[y, x];

    // Per version (index 1..15), level M: total codewords, EC codewords per block, block counts and data sizes of the two groups.
    private static readonly int[] TotalCodewords = [0, 26, 44, 70, 100, 134, 172, 196, 242, 292, 346, 404, 466, 532, 581, 655];
    private static readonly int[] EcPerBlock = [0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24];
    private static readonly int[] Group1Blocks = [0, 1, 1, 1, 2, 2, 4, 4, 2, 3, 4, 1, 6, 8, 4, 5];
    private static readonly int[] Group1Data = [0, 16, 28, 44, 32, 43, 27, 31, 38, 36, 43, 50, 36, 37, 40, 41];
    private static readonly int[] Group2Blocks = [0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 1, 4, 2, 1, 5, 5];
    private static readonly int[][] AlignmentCenters =
    [
        [], [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34], [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50], [6, 30, 54], [6, 32, 58], [6, 34, 62], [6, 26, 46, 66], [6, 26, 48, 70],
    ];

    public static QrCode Encode(string text) => Encode(System.Text.Encoding.UTF8.GetBytes(text));
    public static QrCode Encode(byte[] data)
    {
        int version = 1;
        while (version <= 15 && DataCapacity(version) < data.Length) version++;
        if (version > 15) throw new ArgumentException("Too much data for a QR code.");
        return new QrCode(version, data);
    }
    private static int DataCodewords(int version) => Group1Blocks[version] * Group1Data[version] + Group2Blocks[version] * (Group1Data[version] + 1);
    private static int DataCapacity(int version) => (DataCodewords(version) * 8 - 4 - (version >= 10 ? 16 : 8)) / 8;

    private QrCode(int version, byte[] data)
    {
        Size = version * 4 + 17;
        modules = new bool[Size, Size]; reserved = new bool[Size, Size];
        DrawPatterns(version);
        var codewords = Interleave(version, BuildData(version, data));
        PlaceData(codewords);
        int mask = ChooseMask();
        ApplyMask(mask); DrawFormat(mask);
    }

    // ---- data ----
    private byte[] BuildData(int version, byte[] data)
    {
        var bits = new List<bool>();
        void Append(int value, int count) { for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) == 1); }
        Append(0b0100, 4);
        Append(data.Length, version >= 10 ? 16 : 8);
        foreach (byte b in data) Append(b, 8);
        int capacity = DataCodewords(version) * 8;
        for (int i = 0; i < 4 && bits.Count < capacity; i++) bits.Add(false);
        while (bits.Count % 8 != 0) bits.Add(false);
        for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Append(pad, 8);
        var bytes = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        return bytes;
    }
    private static byte[] Interleave(int version, byte[] data)
    {
        int ec = EcPerBlock[version];
        var blocks = new List<byte[]>(); var ecBlocks = new List<byte[]>();
        int offset = 0;
        for (int g = 0; g < 2; g++)
        {
            int count = g == 0 ? Group1Blocks[version] : Group2Blocks[version], size = Group1Data[version] + g;
            for (int b = 0; b < count; b++)
            {
                var block = data[offset..(offset + size)]; offset += size;
                blocks.Add(block); ecBlocks.Add(ReedSolomon(block, ec));
            }
        }
        var result = new List<byte>(TotalCodewords[version]);
        int longest = blocks.Max(b => b.Length);
        for (int i = 0; i < longest; i++) foreach (var block in blocks) if (i < block.Length) result.Add(block[i]);
        for (int i = 0; i < ec; i++) foreach (var block in ecBlocks) result.Add(block[i]);
        return result.ToArray();
    }
    private static readonly byte[] Exp = new byte[512], Log = new byte[256];
    static QrCode()
    {
        int x = 1;
        for (int i = 0; i < 255; i++) { Exp[i] = (byte)x; Log[x] = (byte)i; x <<= 1; if (x >= 256) x ^= 0x11D; }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }
    private static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];
    private static byte[] ReedSolomon(byte[] data, int ecCount)
    {
        var generator = new byte[] { 1 };
        for (int i = 0; i < ecCount; i++)
        {
            var next = new byte[generator.Length + 1];
            for (int j = 0; j < generator.Length; j++) { next[j] ^= generator[j]; next[j + 1] ^= Mul(generator[j], Exp[i]); }
            generator = next;
        }
        var remainder = new byte[ecCount];
        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ remainder[0]);
            Array.Copy(remainder, 1, remainder, 0, ecCount - 1); remainder[ecCount - 1] = 0;
            for (int j = 0; j < ecCount; j++) remainder[j] ^= Mul(generator[j + 1], factor);
        }
        return remainder;
    }

    // ---- patterns ----
    private void Set(int x, int y, bool dark) { modules[y, x] = dark; reserved[y, x] = true; }
    private void DrawPatterns(int version)
    {
        DrawFinder(0, 0); DrawFinder(Size - 7, 0); DrawFinder(0, Size - 7);
        for (int i = 8; i < Size - 8; i++) { Set(i, 6, i % 2 == 0); Set(6, i, i % 2 == 0); }
        var centers = AlignmentCenters[version];
        int last = centers.Length - 1;
        for (int i = 0; i < centers.Length; i++) for (int j = 0; j < centers.Length; j++)
        {
            // Every combination except the three that would sit on a finder pattern (they do overlap the timing lines).
            if ((i == 0 && j == 0) || (i == 0 && j == last) || (i == last && j == 0)) continue;
            int cx = centers[i], cy = centers[j];
            for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++) Set(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }
        // Format information areas (filled in later) and the dark module.
        for (int i = 0; i < 9; i++) { if (i != 6) { Set(i, 8, false); Set(8, i, false); } }
        for (int i = 0; i < 8; i++) { Set(Size - 1 - i, 8, false); Set(8, Size - 1 - i, false); }
        Set(8, Size - 8, true);
        if (version >= 7)
        {
            int info = version << 12, rem = version;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ (((rem >> 11) & 1) == 1 ? 0x1F25 : 0);
            info |= rem & 0xFFF;
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((info >> i) & 1) == 1;
                Set(i / 3, Size - 11 + i % 3, bit); Set(Size - 11 + i % 3, i / 3, bit);
            }
        }
    }
    private void DrawFinder(int x0, int y0)
    {
        for (int dy = -1; dy <= 7; dy++) for (int dx = -1; dx <= 7; dx++)
        {
            int x = x0 + dx, y = y0 + dy;
            if (x < 0 || y < 0 || x >= Size || y >= Size) continue;
            int d = Math.Max(Math.Abs(dx - 3), Math.Abs(dy - 3));
            Set(x, y, d != 2 && d != 4);
        }
    }
    private void PlaceData(byte[] codewords)
    {
        int bit = 0, total = codewords.Length * 8;
        bool upward = true;
        for (int right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right--;
            for (int step = 0; step < Size; step++)
            {
                int y = upward ? Size - 1 - step : step;
                for (int dx = 0; dx < 2; dx++)
                {
                    int x = right - dx;
                    if (reserved[y, x]) continue;
                    bool dark = bit < total && ((codewords[bit / 8] >> (7 - bit % 8)) & 1) == 1;
                    modules[y, x] = dark; bit++;
                }
            }
            upward = !upward;
        }
    }
    private static bool MaskBit(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => (y / 2 + x / 3) % 2 == 0,
        5 => x * y % 2 + x * y % 3 == 0,
        6 => (x * y % 2 + x * y % 3) % 2 == 0,
        _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
    };
    private void ApplyMask(int mask)
    {
        for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++) if (!reserved[y, x] && MaskBit(mask, x, y)) modules[y, x] = !modules[y, x];
    }
    private int ChooseMask()
    {
        int best = 0, bestScore = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask); DrawFormat(mask);
            int score = Penalty();
            ApplyMask(mask);
            if (score < bestScore) { bestScore = score; best = mask; }
        }
        return best;
    }
    private void DrawFormat(int mask)
    {
        int data = (0b00 << 3) | mask, rem = data;  // level M = 00
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ (((rem >> 9) & 1) == 1 ? 0x537 : 0);
        int bits = ((data << 10) | (rem & 0x3FF)) ^ 0x5412;
        for (int i = 0; i < 15; i++)
        {
            bool bit = ((bits >> i) & 1) == 1;
            if (i < 6) modules[i, 8] = bit; else if (i < 8) modules[i + 1, 8] = bit; else if (i == 8) modules[8, 7] = bit; else modules[8, 14 - i] = bit;
            if (i < 8) modules[8, Size - 1 - i] = bit; else modules[Size - 15 + i, 8] = bit;
        }
    }
    private int Penalty()
    {
        int score = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int a = 0; a < Size; a++)
            {
                int run = 0; bool last = false;
                for (int b = 0; b < Size; b++)
                {
                    bool dark = pass == 0 ? modules[a, b] : modules[b, a];
                    if (b > 0 && dark == last) { run++; if (run == 5) score += 3; else if (run > 5) score++; } else run = 1;
                    last = dark;
                }
                for (int b = 0; b + 10 < Size; b++)
                {
                    bool Dark(int i) => pass == 0 ? modules[a, b + i] : modules[b + i, a];
                    bool pattern = Dark(0) && !Dark(1) && Dark(2) && Dark(3) && Dark(4) && !Dark(5) && Dark(6);
                    if (pattern && ((!Dark(7) && !Dark(8) && !Dark(9) && !Dark(10)) || (b >= 4 && !(pass == 0 ? modules[a, b - 1] : modules[b - 1, a]) && !(pass == 0 ? modules[a, b - 2] : modules[b - 2, a]) && !(pass == 0 ? modules[a, b - 3] : modules[b - 3, a]) && !(pass == 0 ? modules[a, b - 4] : modules[b - 4, a])))) score += 40;
                }
            }
        }
        for (int y = 0; y + 1 < Size; y++) for (int x = 0; x + 1 < Size; x++)
            if (modules[y, x] == modules[y, x + 1] && modules[y, x] == modules[y + 1, x] && modules[y, x] == modules[y + 1, x + 1]) score += 3;
        int darkCount = 0;
        for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++) if (modules[y, x]) darkCount++;
        int percent = darkCount * 100 / (Size * Size);
        score += Math.Min(Math.Abs(percent - 50) / 5, Math.Abs(percent - 50 + 4) / 5) * 10;
        return score;
    }

    // A crisp bitmap: each module is `scale` pixels, with a 4-module quiet zone. Dark on light, as scanners expect.
    public BitmapSource ToBitmap(int scale = 6, int quiet = 4)
    {
        int pixels = (Size + quiet * 2) * scale;
        var buffer = new byte[pixels * pixels];
        Array.Fill(buffer, (byte)0xFF);
        for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++)
        {
            if (!modules[y, x]) continue;
            for (int py = 0; py < scale; py++) for (int px = 0; px < scale; px++) buffer[((y + quiet) * scale + py) * pixels + (x + quiet) * scale + px] = 0x10;
        }
        var bitmap = BitmapSource.Create(pixels, pixels, 96, 96, PixelFormats.Gray8, null, buffer, pixels);
        bitmap.Freeze();
        return bitmap;
    }
}
