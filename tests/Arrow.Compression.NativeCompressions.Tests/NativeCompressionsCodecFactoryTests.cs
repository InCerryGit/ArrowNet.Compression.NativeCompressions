using Apache.Arrow.Ipc;
using Xunit;

namespace Arrow.Compression.NativeCompressions.Tests;

public sealed class NativeCompressionsCodecFactoryTests
{
    [Theory]
    [InlineData(CompressionCodecType.Lz4Frame)]
    [InlineData(CompressionCodecType.Zstd)]
    public void CreateCodecReturnsSupportedCodec(CompressionCodecType compressionCodecType)
    {
        var factory = new NativeCompressionsCodecFactory();

        using ICompressionCodec codec = factory.CreateCodec(compressionCodecType);

        Assert.NotNull(codec);
    }

    [Theory]
    [InlineData(CompressionCodecType.Lz4Frame)]
    [InlineData(CompressionCodecType.Zstd)]
    public void CodecRoundTripsPayload(CompressionCodecType compressionCodecType)
    {
        byte[] source = CreatePayload();
        var factory = new NativeCompressionsCodecFactory();

        using ICompressionCodec codec = factory.CreateCodec(compressionCodecType);
        using var compressed = new MemoryStream();
        codec.Compress(source, compressed);

        byte[] actual = new byte[source.Length];
        int bytesWritten = codec.Decompress(compressed.ToArray(), actual);

        Assert.Equal(source.Length, bytesWritten);
        Assert.Equal(source, actual);
    }

    private static byte[] CreatePayload()
    {
        byte[] payload = new byte[256 * 1024];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)((i * 31) ^ (i >> 3));
        }

        return payload;
    }
}
