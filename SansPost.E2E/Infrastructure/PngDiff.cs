using System.IO.Compression;

namespace SansPost.E2E.Infrastructure
{
    // Minimalny dekoder PNG (8-bit RGB/RGBA, bez przeplotu — format zrzutów Chromium) i porównanie pikseli z tolerancją.
    // Bez zewnętrznych bibliotek graficznych.
    public static class PngDiff
    {
        public sealed record Image(int Width, int Height, byte[] Rgba);

        public sealed record Result(bool SameSize, int DifferentPixels, double Ratio);

        public static Result Compare(byte[] expectedPng, byte[] actualPng, int channelTolerance = 24)
        {
            var expected = Decode(expectedPng);
            var actual = Decode(actualPng);
            if (expected.Width != actual.Width || expected.Height != actual.Height)
                return new Result(false, int.MaxValue, 1);

            var different = 0;
            for (var i = 0; i < expected.Rgba.Length; i += 4)
            {
                if (Math.Abs(expected.Rgba[i] - actual.Rgba[i]) > channelTolerance
                    || Math.Abs(expected.Rgba[i + 1] - actual.Rgba[i + 1]) > channelTolerance
                    || Math.Abs(expected.Rgba[i + 2] - actual.Rgba[i + 2]) > channelTolerance)
                {
                    different++;
                }
            }

            return new Result(true, different, different / (double)(expected.Width * expected.Height));
        }

        public static Image Decode(byte[] png)
        {
            var span = png.AsSpan();
            if (span.Length < 8 || span[0] != 0x89 || span[1] != (byte)'P')
                throw new InvalidDataException("To nie jest plik PNG.");

            int width = 0, height = 0, colorType = 0;
            using var idat = new MemoryStream();
            var offset = 8;
            while (offset < span.Length)
            {
                var length = ReadInt(span, offset);
                var type = System.Text.Encoding.ASCII.GetString(span.Slice(offset + 4, 4));
                var data = span.Slice(offset + 8, length);
                switch (type)
                {
                    case "IHDR":
                        width = ReadInt(data, 0);
                        height = ReadInt(data, 4);
                        if (data[8] != 8 || data[12] != 0)
                            throw new NotSupportedException("Obsługiwane tylko 8-bit PNG bez przeplotu.");
                        colorType = data[9];
                        break;
                    case "IDAT":
                        idat.Write(data);
                        break;
                    case "IEND":
                        offset = span.Length;
                        continue;
                }

                offset += 12 + length;
            }

            var channels = colorType switch { 2 => 3, 6 => 4, _ => throw new NotSupportedException($"Typ koloru {colorType}.") };
            idat.Position = 0;
            using var inflater = new ZLibStream(idat, CompressionMode.Decompress);
            using var raw = new MemoryStream();
            inflater.CopyTo(raw);
            var bytes = raw.ToArray();

            var stride = width * channels;
            var previous = new byte[stride];
            var current = new byte[stride];
            var rgba = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                var filter = bytes[y * (stride + 1)];
                Array.Copy(bytes, y * (stride + 1) + 1, current, 0, stride);
                for (var x = 0; x < stride; x++)
                {
                    int left = x >= channels ? current[x - channels] : 0;
                    int up = previous[x];
                    int upLeft = x >= channels ? previous[x - channels] : 0;
                    current[x] = filter switch
                    {
                        0 => current[x],
                        1 => (byte)(current[x] + left),
                        2 => (byte)(current[x] + up),
                        3 => (byte)(current[x] + (left + up) / 2),
                        4 => (byte)(current[x] + Paeth(left, up, upLeft)),
                        _ => throw new InvalidDataException($"Filtr PNG {filter}.")
                    };
                }

                for (var x = 0; x < width; x++)
                {
                    var target = (y * width + x) * 4;
                    rgba[target] = current[x * channels];
                    rgba[target + 1] = current[x * channels + 1];
                    rgba[target + 2] = current[x * channels + 2];
                    rgba[target + 3] = channels == 4 ? current[x * channels + 3] : (byte)255;
                }

                (previous, current) = (current, previous);
            }

            return new Image(width, height, rgba);
        }

        private static int ReadInt(ReadOnlySpan<byte> data, int offset) =>
            (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

        private static int Paeth(int a, int b, int c)
        {
            var p = a + b - c;
            var pa = Math.Abs(p - a);
            var pb = Math.Abs(p - b);
            var pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }
}
