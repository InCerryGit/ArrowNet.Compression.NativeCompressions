using System;
using System.Buffers;
using System.IO;
using Apache.Arrow.Ipc;
using NativeCompressions;

namespace ArrowNet.Compression.NativeCompressions;

internal sealed class NativeCompressionsZstdCompressionCodec : ITryCompressionCodec
{
    private readonly int _compressionLevel;

    public NativeCompressionsZstdCompressionCodec(int? compressionLevel)
    {
        _compressionLevel = compressionLevel ?? 3;
    }

    public void Compress(ReadOnlyMemory<byte> source, Stream destination)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Zstandard.GetMaxCompressedLength(source.Length));
        try
        {
            int bytesWritten = Zstandard.Compress(source.Span, buffer, _compressionLevel);
            destination.Write(buffer.AsSpan(0, bytesWritten));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public bool TryCompress(ReadOnlyMemory<byte> source, Memory<byte> destination, out int bytesWritten)
    {
        using var encoder = new ZstandardEncoder(_compressionLevel);
        OperationStatus status = encoder.Compress(
            source.Span,
            destination.Span,
            out int bytesConsumed,
            out bytesWritten,
            isFinalBlock: true);

        if (status == OperationStatus.Done && bytesConsumed == source.Length && bytesWritten < source.Length)
        {
            return true;
        }

        bytesWritten = 0;
        return false;
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
