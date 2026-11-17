using System.Buffers.Binary;
using System.Text;

namespace R2MM;

internal sealed class PakFormatException(string message) : Exception(message);

internal static class PakIndexReader
{
    private const uint PakMagic = 0x5A6F12E1;
    private const int MaxIndexBytes = 512 * 1024 * 1024;
    private const int MaxEntries = 10_000_000;
    private const int MaxStringChars = 8192;

    private const string GameRoot = "redout2";
    private const string ContentFolder = "content";

    public static HashSet<string> ReadFileList(string pakPath)
    {
        using var stream = new FileStream(
            pakPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        if (length < 44)
        {
            throw new PakFormatException("The file is too small to be a PAK.");
        }

        var tailLength = (int)Math.Min(length, 1024);
        var tail = new byte[tailLength];
        stream.Seek(length - tailLength, SeekOrigin.Begin);
        stream.ReadExactly(tail);

        var magicPosition = -1;
        for (var i = tailLength - 44; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == PakMagic)
            {
                magicPosition = i;
                break;
            }
        }
        if (magicPosition < 0)
        {
            throw new PakFormatException("No PAK footer was found.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(magicPosition + 4));
        var indexOffset = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(magicPosition + 8));
        var indexSize = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(magicPosition + 16));
        if (version is < 3 or > 11)
        {
            throw new PakFormatException($"PAK version {version} is not supported.");
        }

        // From version 4 the footer carries an "index is encrypted" flag right before the magic.
        if (version >= 4 && magicPosition >= 1 && tail[magicPosition - 1] != 0)
        {
            throw new PakFormatException("The PAK index is encrypted.");
        }

        var index = ReadBlock(stream, indexOffset, indexSize);
        return version >= 10
            ? ReadModernIndex(stream, index)
            : ReadLegacyIndex(index, version);
    }

    public static HashSet<string> ReadSidecarList(string listPath)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadLines(listPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var firstQuote = line.IndexOf('"');
            if (firstQuote >= 0)
            {
                var secondQuote = line.IndexOf('"', firstQuote + 1);
                if (secondQuote > firstQuote)
                {
                    line = line.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
                }
            }

            var key = NormalizeKey(line);
            var lastSlash = key.LastIndexOf('/');
            if (lastSlash < 0 || key.IndexOf('.', lastSlash) < 0)
            {
                continue; // not a file path (directory, log noise, "Mount point ..." lines)
            }

            if (key.StartsWith(GameRoot + "/", StringComparison.Ordinal))
            {
                files.Add(key);
            }
            else if (key.StartsWith(ContentFolder + "/", StringComparison.Ordinal))
            {
                files.Add(GameRoot + "/" + key);
            }
            else
            {
                files.Add(GameRoot + "/" + ContentFolder + "/" + key);
            }
        }
        return files;
    }

    public static string NormalizeKey(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder(path.Length);
        var leading = true;
        foreach (var part in parts)
        {
            if (part == "." || (leading && part == ".."))
            {
                continue;
            }
            leading = false;
            if (builder.Length > 0)
            {
                builder.Append('/');
            }
            builder.Append(part);
        }
        return builder.ToString().ToLowerInvariant();
    }

    private static byte[] ReadBlock(FileStream stream, long offset, long size)
    {
        if (offset < 0 || size <= 0 || size > MaxIndexBytes || offset > stream.Length - size)
        {
            throw new PakFormatException("The PAK index location is invalid.");
        }
        var block = new byte[size];
        stream.Seek(offset, SeekOrigin.Begin);
        stream.ReadExactly(block);
        return block;
    }

    private static HashSet<string> ReadLegacyIndex(byte[] index, uint version)
    {
        var reader = new IndexReader(index);
        var mountPoint = reader.ReadFString();
        var count = reader.ReadInt32();
        if (count < 0 || count > MaxEntries)
        {
            throw new PakFormatException("The PAK index entry count is invalid.");
        }

        var files = new HashSet<string>(Math.Min(count, 100_000), StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadFString();
            reader.Skip(8 + 8 + 8); // offset, size, uncompressed size
            var compression = reader.ReadInt32();
            reader.Skip(20); // SHA1
            if (compression != 0)
            {
                var blocks = reader.ReadInt32();
                if (blocks < 0)
                {
                    throw new PakFormatException("The PAK index contains an invalid entry.");
                }
                reader.Skip((long)blocks * 16);
            }
            reader.Skip(1 + 4); // encrypted flag, compression block size
            files.Add(NormalizeKey(mountPoint + "/" + name));
        }
        _ = version;
        return files;
    }

    private static HashSet<string> ReadModernIndex(FileStream stream, byte[] index)
    {
        var reader = new IndexReader(index);
        var mountPoint = reader.ReadFString();
        var count = reader.ReadInt32();
        if (count < 0 || count > MaxEntries)
        {
            throw new PakFormatException("The PAK index entry count is invalid.");
        }
        reader.Skip(8); // path hash seed
        if (reader.ReadInt32() != 0)
        {
            reader.Skip(8 + 8 + 20); // path hash index offset, size, hash
        }
        if (reader.ReadInt32() == 0)
        {
            throw new PakFormatException("The PAK has no full directory index.");
        }
        var directoryOffset = reader.ReadInt64();
        var directorySize = reader.ReadInt64();

        var directoryReader = new IndexReader(ReadBlock(stream, directoryOffset, directorySize));
        var directoryCount = directoryReader.ReadInt32();
        if (directoryCount < 0 || directoryCount > MaxEntries)
        {
            throw new PakFormatException("The PAK directory index is invalid.");
        }

        var files = new HashSet<string>(Math.Min(count, 100_000), StringComparer.Ordinal);
        for (var i = 0; i < directoryCount; i++)
        {
            var directory = directoryReader.ReadFString();
            var fileCount = directoryReader.ReadInt32();
            if (fileCount < 0 || fileCount > MaxEntries)
            {
                throw new PakFormatException("The PAK directory index is invalid.");
            }
            for (var j = 0; j < fileCount; j++)
            {
                var fileName = directoryReader.ReadFString();
                directoryReader.Skip(4); // encoded entry offset
                files.Add(NormalizeKey(mountPoint + "/" + directory + "/" + fileName));
            }
        }
        return files;
    }

    private sealed class IndexReader(byte[] data)
    {
        private int _position;

        public int ReadInt32()
        {
            Ensure(4);
            var value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(_position));
            _position += 4;
            return value;
        }

        public long ReadInt64()
        {
            Ensure(8);
            var value = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(_position));
            _position += 8;
            return value;
        }

        public void Skip(long count)
        {
            Ensure(count);
            _position += (int)count;
        }

        public string ReadFString()
        {
            var length = ReadInt32();
            if (length == 0)
            {
                return "";
            }
            if (length == int.MinValue)
            {
                throw new PakFormatException("The PAK index contains an invalid string.");
            }

            if (length > 0)
            {
                if (length > MaxStringChars)
                {
                    throw new PakFormatException("The PAK index contains an oversized string.");
                }
                Ensure(length);
                var text = Encoding.UTF8.GetString(data, _position, length - 1);
                _position += length;
                return text;
            }

            var chars = -length;
            if (chars > MaxStringChars)
            {
                throw new PakFormatException("The PAK index contains an oversized string.");
            }
            Ensure((long)chars * 2);
            var wide = Encoding.Unicode.GetString(data, _position, (chars - 1) * 2);
            _position += chars * 2;
            return wide;
        }

        private void Ensure(long count)
        {
            if (count < 0 || count > data.Length - _position)
            {
                throw new PakFormatException("The PAK index is truncated or corrupt.");
            }
        }
    }
}
