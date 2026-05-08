using System.Text;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Memory;
using ArrowNet.Compression.NativeCompressions;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

[MemoryDiagnoser]
public class ArrowIpcCompressionBenchmarks
{
    private RecordBatch _recordBatch = null!;
    private byte[] _officialCompressedStream = null!;
    private ICompressionCodecFactory _factory = null!;

    [Params(500_000, 1_000_000, 2_000_000)]
    public int RowCount { get; set; }

    [Params(CompressionCodecType.Lz4Frame, CompressionCodecType.Zstd)]
    public CompressionCodecType Codec { get; set; }

    [Params(CompressionBackend.ApacheArrowCompression, CompressionBackend.NativeCompressions)]
    public CompressionBackend Backend { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _recordBatch = CreateRecordBatch(RowCount);
        _factory = CreateFactory(Backend);

        // Use one official Apache Arrow IPC payload for read benchmarks so both factories decompress identical bytes.
        _officialCompressedStream = WriteCompressedIpcStream(
            new Apache.Arrow.Compression.CompressionCodecFactory(),
            Codec,
            _recordBatch);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _recordBatch.Dispose();
    }

    [Benchmark]
    public byte[] WriteCompressedIpcStream()
    {
        return WriteCompressedIpcStream(_factory, Codec, _recordBatch);
    }

    [Benchmark]
    public int ReadOfficialCompressedIpcStream()
    {
        using var reader = new ArrowStreamReader(_officialCompressedStream, _factory);
        int rows = 0;

        RecordBatch? batch;
        while ((batch = reader.ReadNextRecordBatch()) is not null)
        {
            rows += batch.Length;
            batch.Dispose();
        }

        return rows;
    }

    private static ICompressionCodecFactory CreateFactory(CompressionBackend backend)
    {
        return backend switch
        {
            CompressionBackend.ApacheArrowCompression => new Apache.Arrow.Compression.CompressionCodecFactory(),
            CompressionBackend.NativeCompressions => new NativeCompressionsCodecFactory(),
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown compression backend")
        };
    }

    private static byte[] WriteCompressedIpcStream(
        ICompressionCodecFactory factory,
        CompressionCodecType codec,
        RecordBatch recordBatch)
    {
        using var stream = new MemoryStream();
        var options = new IpcOptions
        {
            CompressionCodec = codec,
            CompressionCodecFactory = factory
        };

        using var writer = new ArrowStreamWriter(stream, recordBatch.Schema, true, options);
        writer.WriteStart();
        writer.WriteRecordBatch(recordBatch);
        writer.WriteEnd();

        return stream.ToArray();
    }

    private static RecordBatch CreateRecordBatch(int rowCount)
    {
        var ids = new Int32Array.Builder();
        var categories = new StringArray.Builder();

        for (int i = 0; i < rowCount; i++)
        {
            ids.Append((i * 31) ^ (i >> 3));
            categories.Append($"category-{i % 128:D3}-bucket-{(i * 17) % 31:D2}", Encoding.UTF8);
        }

        return new RecordBatch.Builder(MemoryAllocator.Default.Value)
            .Append("id", false, ids)
            .Append("category", false, categories)
            .Build();
    }
}

public enum CompressionBackend
{
    ApacheArrowCompression,
    NativeCompressions
}
