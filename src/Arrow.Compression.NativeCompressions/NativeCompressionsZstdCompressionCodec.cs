using System;
using System.Buffers;
using System.IO;
using Apache.Arrow.Ipc;
using NativeCompressions;

namespace Arrow.Compression.NativeCompressions;

internal sealed class NativeCompressionsZstdCompressionCodec : ICompressionCodec
{
    private readonly int _compressionLevel;

    public NativeCompressionsZstdCompressionCodec(int? compressionLevel)
    {
        _compressionLevel = compressionLevel ?? 3;
    }

    public void Compress(ReadOnlyMemory<byte> source, Stream destination)
    {
        byte[] compressed = Zstandard.Compress(source.Span, _compressionLevel);
        destination.Write(compressed, 0, compressed.Length);
    }

    public int Decompress(ReadOnlyMemory<byte> source, Memory<byte> destination)
    {
        var decoder = new ZstandardDecoder();
        int totalBytesWritten = 0;
        ReadOnlySpan<byte> input = source.Span;
        Span<byte> output = destination.Span;

        while (true)
        {
            OperationStatus status = decoder.Decompress(
                input,
                output,
                out int bytesConsumed,
                out int bytesWritten);

            input = input.Slice(bytesConsumed);
            output = output.Slice(bytesWritten);
            totalBytesWritten += bytesWritten;

            if (status == OperationStatus.Done || (status == OperationStatus.DestinationTooSmall && output.IsEmpty))
            {
                return totalBytesWritten;
            }

            if (bytesConsumed == 0 && bytesWritten == 0)
            {
                throw new InvalidOperationException($"Failed to decompress Zstandard frame: {status}");
            }
        }
    }

    public void Dispose()
    {
    }
}
