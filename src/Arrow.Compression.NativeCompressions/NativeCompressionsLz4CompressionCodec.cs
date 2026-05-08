using System;
using System.Buffers;
using System.IO;
using Apache.Arrow.Ipc;
using NativeCompressions;

namespace Arrow.Compression.NativeCompressions;

internal sealed class NativeCompressionsLz4CompressionCodec : ICompressionCodec
{
    public void Compress(ReadOnlyMemory<byte> source, Stream destination)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(LZ4.GetMaxCompressedLength(source.Length));
        try
        {
            int bytesWritten = LZ4.Compress(source.Span, buffer);
            destination.Write(buffer.AsSpan(0, bytesWritten));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public int Decompress(ReadOnlyMemory<byte> source, Memory<byte> destination)
    {
        var decoder = new LZ4Decoder();
        return DecompressFrame(source.Span, destination.Span, decoder.Decompress);
    }

    private static int DecompressFrame(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        FrameDecoder decoder)
    {
        int totalBytesWritten = 0;

        while (true)
        {
            OperationStatus status = decoder(
                source,
                destination,
                out int bytesConsumed,
                out int bytesWritten);

            source = source.Slice(bytesConsumed);
            destination = destination.Slice(bytesWritten);
            totalBytesWritten += bytesWritten;

            if (status == OperationStatus.Done || (status == OperationStatus.DestinationTooSmall && destination.IsEmpty))
            {
                return totalBytesWritten;
            }

            if (bytesConsumed == 0 && bytesWritten == 0)
            {
                throw new InvalidOperationException($"Failed to decompress LZ4 frame: {status}");
            }
        }
    }

    private delegate OperationStatus FrameDecoder(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        out int bytesConsumed,
        out int bytesWritten);

    public void Dispose()
    {
    }
}
