using System;
using Apache.Arrow.Ipc;

namespace Arrow.Compression.NativeCompressions;

public sealed class NativeCompressionsCodecFactory : ICompressionCodecFactory
{
    public ICompressionCodec CreateCodec(CompressionCodecType compressionCodecType)
    {
        return CreateCodec(compressionCodecType, null);
    }

    public ICompressionCodec CreateCodec(CompressionCodecType compressionCodecType, int? compressionLevel)
    {
        return compressionCodecType switch
        {
            CompressionCodecType.Lz4Frame => new NativeCompressionsLz4CompressionCodec(),
            CompressionCodecType.Zstd => new NativeCompressionsZstdCompressionCodec(compressionLevel),
            _ => throw new NotSupportedException($"Compression type {compressionCodecType} is not supported by NativeCompressions")
        };
    }
}
