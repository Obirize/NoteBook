using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace Notlar;

// Photos and videos are kept as separate files next to the vault, each encrypted with its own random key.
// The key lives only inside the (encrypted) notebook, so a copied file alone is unreadable, and the file
// never has to be re-encrypted to travel: a backup or another device receives it byte for byte.
//
// File layout (version 1):
//   header  "NLA1" | chunk size (int32 LE) | plaintext length (int64 LE) | 8 random nonce prefix bytes
//   chunks  AES-256-GCM ciphertext + 16-byte tag, one per chunk of plaintext
// Nonce = prefix + chunk index; the header, attachment id, index and a "last" flag are authenticated
// with every chunk, so chunks cannot be reordered, dropped, truncated or swapped between files.
public sealed class AttachmentStore(string directory)
{
    public const int ChunkSize = 1024 * 1024;
    private const int HeaderSize = 24, TagSize = 16;
    private static readonly byte[] Magic = "NLA1"u8.ToArray();
    public static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".heif", ".tif", ".tiff"];
    public static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".webm", ".mkv", ".avi", ".3gp"];
    public string Directory { get; } = directory;

    public static bool IsSupported(string path) => MediaTypeFor(path) != null;
    public static string? MediaTypeFor(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ImageExtensions.Contains(ext)) return ext switch { ".jpg" or ".jpeg" => "image/jpeg", ".tif" or ".tiff" => "image/tiff", ".heic" or ".heif" => "image/heic", _ => "image/" + ext[1..] };
        if (VideoExtensions.Contains(ext)) return ext switch { ".mov" => "video/quicktime", ".m4v" => "video/mp4", ".mkv" => "video/x-matroska", ".avi" => "video/x-msvideo", ".3gp" => "video/3gpp", _ => "video/" + ext[1..] };
        return null;
    }
    public string PathFor(Attachment attachment) => Path.Combine(Directory, attachment.Id + ".bin");
    public bool Exists(Attachment attachment) => File.Exists(PathFor(attachment));

    public Attachment Import(string sourcePath)
    {
        string? mediaType = MediaTypeFor(sourcePath) ?? throw new InvalidDataException(L10n.T("AttachmentSelectFile"));
        var dimensions = (0, 0);
        if (mediaType.StartsWith("image/", StringComparison.Ordinal))
        {
            // The codec gets its own handle: the encryption pass below must see the untouched file.
            using var probe = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            dimensions = ReadDimensions(probe);
        }
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Import(source, Path.GetFileName(sourcePath), mediaType, dimensions);
    }
    // Encrypts the stream into the store and returns the metadata the note must keep (including the key).
    public Attachment Import(Stream source, string name, string mediaType, (int Width, int Height) dimensions = default)
    {
        var attachment = new Attachment { Name = name, MediaType = mediaType, Key = RandomNumberGenerator.GetBytes(32), Width = dimensions.Width, Height = dimensions.Height };
        System.IO.Directory.CreateDirectory(Directory);
        string target = PathFor(attachment), temp = target + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            { (attachment.Size, attachment.Sha256) = Encrypt(source, output, attachment.Key, attachment.Id); output.Flush(true); }
            File.Move(temp, target, true);
        }
        catch { if (File.Exists(temp)) File.Delete(temp); throw; }
        return attachment;
    }
    public void Export(Attachment attachment, string targetPath)
    {
        string temp = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16)) Decrypt(attachment, output);
            if (File.Exists(targetPath)) File.Replace(temp, targetPath, null); else File.Move(temp, targetPath);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public byte[] ReadAll(Attachment attachment)
    {
        using var buffer = new MemoryStream(attachment.Size > int.MaxValue ? 0 : (int)attachment.Size);
        Decrypt(attachment, buffer);
        return buffer.ToArray();
    }
    // A decrypted copy in the temp folder for the video player, which cannot read from memory. The viewer deletes it
    // when it closes; anything a crash leaves behind is removed at the next start.
    private const string TemporaryPrefix = "Notlar-media-";
    public string WriteTemporary(Attachment attachment)
    {
        string path = Path.Combine(Path.GetTempPath(), TemporaryPrefix + Guid.NewGuid().ToString("N") + Path.GetExtension(attachment.Name));
        try { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16); Decrypt(attachment, stream); }
        catch { DeleteTemporary(path); throw; }
        return path;
    }
    public static bool DeleteTemporary(string path)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try { File.Delete(path); return true; }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { Thread.Sleep(100); }
        }
        return false;
    }
    public static void CleanTemporary()
    {
        try { foreach (string file in System.IO.Directory.GetFiles(Path.GetTempPath(), TemporaryPrefix + "*")) { try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public void Decrypt(Attachment attachment, Stream destination)
    {
        using var input = new FileStream(PathFor(attachment), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        Decrypt(input, destination, attachment.Key, attachment.Id);
    }
    // Removes files no note (including notes in Recently deleted) refers to any more, and half-written imports.
    public int Sweep(Notebook book)
    {
        if (!System.IO.Directory.Exists(Directory)) return 0;
        var referenced = book.Notes.SelectMany(n => n.Attachments).Select(a => a.Id + ".bin").ToHashSet(StringComparer.OrdinalIgnoreCase);
        int removed = 0;
        foreach (string file in System.IO.Directory.GetFiles(Directory, "*.bin").Concat(System.IO.Directory.GetFiles(Directory, "*.bin.tmp")))
        {
            if (referenced.Contains(Path.GetFileName(file))) continue;
            try { File.Delete(file); removed++; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }
    // Copies the encrypted files of every attachment in the notebook that this store lacks from another store.
    public int CopyMissing(Notebook book, AttachmentStore from)
    {
        int copied = 0;
        foreach (var attachment in book.Notes.SelectMany(n => n.Attachments))
        {
            if (Exists(attachment) || !from.Exists(attachment)) continue;
            System.IO.Directory.CreateDirectory(Directory);
            File.Copy(from.PathFor(attachment), PathFor(attachment), false); copied++;
        }
        return copied;
    }

    private static byte[] Aad(ReadOnlySpan<byte> header, string id, uint index, bool last)
    {
        var idBytes = Encoding.UTF8.GetBytes(id);
        var aad = new byte[header.Length + idBytes.Length + 5];
        header.CopyTo(aad); idBytes.CopyTo(aad, header.Length);
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(header.Length + idBytes.Length), index);
        aad[^1] = last ? (byte)1 : (byte)0;
        return aad;
    }
    private static void Nonce(ReadOnlySpan<byte> prefix, uint index, Span<byte> nonce) { prefix.CopyTo(nonce); BinaryPrimitives.WriteUInt32BigEndian(nonce[8..], index); }
    public static (long Size, byte[] Sha256) Encrypt(Stream source, Stream destination, byte[] key, string id)
    {
        if (key.Length != 32) throw new ArgumentException("Attachment key must be 256 bits.");
        long length = source.CanSeek ? source.Length - source.Position : throw new ArgumentException("Source must be seekable.");
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), ChunkSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), length);
        RandomNumberGenerator.Fill(header.AsSpan(16, 8));
        destination.Write(header);
        using var aes = new AesGcm(key, TagSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var plain = new byte[ChunkSize]; var cipher = new byte[ChunkSize]; var tag = new byte[TagSize]; var nonce = new byte[12];
        long remaining = length; uint index = 0;
        do
        {
            int size = (int)Math.Min(ChunkSize, remaining);
            source.ReadExactly(plain, 0, size);
            hash.AppendData(plain, 0, size);
            remaining -= size;
            bool last = remaining == 0;
            Nonce(header.AsSpan(16, 8), index, nonce);
            aes.Encrypt(nonce, plain.AsSpan(0, size), cipher.AsSpan(0, size), tag, Aad(header, id, index, last));
            destination.Write(cipher, 0, size); destination.Write(tag);
            index++;
        } while (remaining > 0);
        CryptographicOperations.ZeroMemory(plain);
        return (length, hash.GetHashAndReset());
    }
    public static void Decrypt(Stream source, Stream destination, byte[] key, string id)
    {
        if (key.Length != 32) throw new CryptographicException("Attachment key missing.");
        var header = new byte[HeaderSize];
        try { source.ReadExactly(header); } catch (EndOfStreamException) { throw new CryptographicException("Attachment file is damaged."); }
        int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        long length = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
        if (!header.AsSpan(0, 4).SequenceEqual(Magic) || chunkSize <= 0 || chunkSize > 64 * ChunkSize || length < 0) throw new CryptographicException("Unsupported attachment format.");
        using var aes = new AesGcm(key, TagSize);
        var cipher = new byte[chunkSize]; var plain = new byte[chunkSize]; var tag = new byte[TagSize]; var nonce = new byte[12];
        long remaining = length; uint index = 0;
        try
        {
            do
            {
                int size = (int)Math.Min(chunkSize, remaining);
                try { source.ReadExactly(cipher, 0, size); source.ReadExactly(tag); }
                catch (EndOfStreamException) { throw new CryptographicException("Attachment file is truncated."); }
                remaining -= size;
                bool last = remaining == 0;
                Nonce(header.AsSpan(16, 8), index, nonce);
                aes.Decrypt(nonce, cipher.AsSpan(0, size), tag, plain.AsSpan(0, size), Aad(header, id, index, last));
                destination.Write(plain, 0, size);
                index++;
            } while (remaining > 0);
            if (source.ReadByte() != -1) throw new CryptographicException("Attachment file has trailing data.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    // Pixel size without decoding the whole picture; (0, 0) when the format has no installed codec.
    public static (int Width, int Height) ReadDimensions(Stream stream)
    {
        try
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            return (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException or InvalidOperationException) { return (0, 0); }
    }
}
