using System.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Memory;
using ArrowNet.Compression.NativeCompressions;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

if (CppFixtureExporter.TryExport(args))
{
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, BenchmarkConfig.Instance);


internal static class BenchmarkConfig
{
    public static readonly IConfig Instance = ManualConfig
        .Create(DefaultConfig.Instance)
        .AddColumn(UncompressedThroughputColumn.Instance);
}

internal sealed class UncompressedThroughputColumn : IColumn
{
    public static readonly UncompressedThroughputColumn Instance = new();
    private static readonly Dictionary<int, double> UncompressedBytesByRowCount = new();

    private UncompressedThroughputColumn()
    {
    }

    public string Id => nameof(UncompressedThroughputColumn);
    public string ColumnName => "Uncompressed MB/s";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 0;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;
    public string Legend => "Estimated throughput in mebibytes per second, based on uncompressed Arrow IPC stream bytes divided by mean execution time.";

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
    {
        return GetValue(summary, benchmarkCase, summary.Style);
    }

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        BenchmarkReport? report = summary[benchmarkCase];
        if (report?.ResultStatistics is null || !TryGetRowCount(benchmarkCase, out int rowCount))
        {
            return "NA";
        }

        double bytes = GetUncompressedBytes(rowCount);
        double seconds = report.ResultStatistics.Mean / 1_000_000_000.0;
        double mebibytesPerSecond = bytes / (1024.0 * 1024.0) / seconds;
        return mebibytesPerSecond.ToString("N1", CultureInfo.InvariantCulture);
    }

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
    public bool IsAvailable(Summary summary) => true;

    private static bool TryGetRowCount(BenchmarkCase benchmarkCase, out int rowCount)
    {
        foreach (var parameter in benchmarkCase.Parameters.Items)
        {
            if (string.Equals(parameter.Name, "RowCount", StringComparison.Ordinal) && parameter.Value is int intValue)
            {
                rowCount = intValue;
                return true;
            }
        }

        rowCount = 0;
        return false;
    }

    private static double GetUncompressedBytes(int rowCount)
    {
        if (!UncompressedBytesByRowCount.TryGetValue(rowCount, out double bytes))
        {
            using RecordBatch recordBatch = ArrowIpcCompressionBenchmarks.CreateRecordBatch(rowCount);
            bytes = ArrowIpcCompressionBenchmarks.WriteUncompressedIpcStream(recordBatch).Length;
            UncompressedBytesByRowCount.Add(rowCount, bytes);
        }

        return bytes;
    }
}

[MemoryDiagnoser]
public class ArrowIpcCompressionBenchmarks
{
    private RecordBatch _recordBatch = null!;
    private byte[] _officialCompressedStream = null!;
    private ICompressionCodecFactory _factory = null!;

    [Params(100_000, 500_000, 1_000_000)]
    public int RowCount { get; set; }

    [Params(CompressionCodecType.Lz4Frame, CompressionCodecType.Zstd)]
    public CompressionCodecType Codec { get; set; }

    [Params(CompressionBackend.ArrowOfficial, CompressionBackend.Native)]
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

    internal static ICompressionCodecFactory CreateFactory(CompressionBackend backend)
    {
        return backend switch
        {
            CompressionBackend.ArrowOfficial => new Apache.Arrow.Compression.CompressionCodecFactory(),
            CompressionBackend.Native => new NativeCompressionsCodecFactory(),
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown compression backend")
        };
    }

    internal static byte[] WriteCompressedIpcStream(
        ICompressionCodecFactory factory,
        CompressionCodecType codec,
        RecordBatch recordBatch)
    {
        using var stream = new MemoryStream();
        WriteCompressedIpcStream(factory, codec, recordBatch, stream);
        return stream.ToArray();
    }

    internal static long WriteCompressedIpcStream(
        ICompressionCodecFactory factory,
        CompressionCodecType codec,
        RecordBatch recordBatch,
        Stream stream)
    {
        var options = new IpcOptions
        {
            CompressionCodec = codec,
            CompressionCodecFactory = factory
        };

        using var writer = new ArrowStreamWriter(stream, recordBatch.Schema, true, options);
        writer.WriteStart();
        writer.WriteRecordBatch(recordBatch);
        writer.WriteEnd();

        return stream.Position;
    }

    internal static byte[] WriteUncompressedIpcStream(RecordBatch recordBatch)
    {
        using var stream = new MemoryStream();
        using var writer = new ArrowStreamWriter(stream, recordBatch.Schema, true);
        writer.WriteStart();
        writer.WriteRecordBatch(recordBatch);
        writer.WriteEnd();

        return stream.ToArray();
    }

    internal static RecordBatch CreateRecordBatch(int rowCount)
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

[MemoryDiagnoser]
public class ArrowIpcNoCompressionBenchmarks
{
    private RecordBatch _recordBatch = null!;
    private byte[] _uncompressedStream = null!;

    [Params(100_000, 500_000, 1_000_000)]
    public int RowCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _recordBatch = ArrowIpcCompressionBenchmarks.CreateRecordBatch(RowCount);
        _uncompressedStream = ArrowIpcCompressionBenchmarks.WriteUncompressedIpcStream(_recordBatch);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _recordBatch.Dispose();
    }

    [Benchmark]
    public byte[] WriteUncompressedIpcStream()
    {
        return ArrowIpcCompressionBenchmarks.WriteUncompressedIpcStream(_recordBatch);
    }

    [Benchmark]
    public int ReadUncompressedIpcStream()
    {
        using var reader = new ArrowStreamReader(_uncompressedStream);
        int rows = 0;

        RecordBatch? batch;
        while ((batch = reader.ReadNextRecordBatch()) is not null)
        {
            rows += batch.Length;
            batch.Dispose();
        }

        return rows;
    }
}

[MemoryDiagnoser]
public class ArrowIpcWriteSinkBenchmarks
{
    private RecordBatch _recordBatch = null!;
    private ICompressionCodecFactory _factory = null!;
    private MemoryStream _preallocatedStream = null!;

    [Params(100_000, 500_000, 1_000_000)]
    public int RowCount { get; set; }

    [Params(CompressionCodecType.Lz4Frame, CompressionCodecType.Zstd)]
    public CompressionCodecType Codec { get; set; }

    [Params(CompressionBackend.ArrowOfficial, CompressionBackend.Native)]
    public CompressionBackend Backend { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _recordBatch = ArrowIpcCompressionBenchmarks.CreateRecordBatch(RowCount);
        _factory = ArrowIpcCompressionBenchmarks.CreateFactory(Backend);
        byte[] sample = ArrowIpcCompressionBenchmarks.WriteCompressedIpcStream(_factory, Codec, _recordBatch);
        _preallocatedStream = new MemoryStream(new byte[Math.Max(sample.Length * 2, 1024)], writable: true);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _preallocatedStream.Dispose();
        _recordBatch.Dispose();
    }

    [Benchmark(Baseline = true)]
    public byte[] WriteCompressedIpcStreamToArray()
    {
        return ArrowIpcCompressionBenchmarks.WriteCompressedIpcStream(_factory, Codec, _recordBatch);
    }

    [Benchmark]
    public long WriteCompressedIpcStreamToPreallocatedSink()
    {
        // Keeps Arrow IPC writing and compression work, but removes MemoryStream.ToArray() from the measured path.
        _preallocatedStream.Position = 0;
        _preallocatedStream.SetLength(0);
        return ArrowIpcCompressionBenchmarks.WriteCompressedIpcStream(_factory, Codec, _recordBatch, _preallocatedStream);
    }
}

[MemoryDiagnoser]
public class ArrowCodecAttributionBenchmarks
{
    private ICompressionCodec _codec = null!;
    private byte[] _source = null!;
    private byte[] _compressedPayload = null!;
    private byte[] _compressionBuffer = null!;
    private byte[] _decompressionBuffer = null!;
    private MemoryStream _compressionStream = null!;

    [Params(100_000, 500_000, 1_000_000)]
    public int RowCount { get; set; }

    [Params(CompressionCodecType.Lz4Frame, CompressionCodecType.Zstd)]
    public CompressionCodecType Codec { get; set; }

    [Params(CompressionBackend.ArrowOfficial, CompressionBackend.Native)]
    public CompressionBackend Backend { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        using RecordBatch recordBatch = ArrowIpcCompressionBenchmarks.CreateRecordBatch(RowCount);
        _source = ArrowIpcCompressionBenchmarks.WriteUncompressedIpcStream(recordBatch);
        _codec = ArrowIpcCompressionBenchmarks.CreateFactory(Backend).CreateCodec(Codec);
        _compressedPayload = CompressPayloadToArray(_codec, _source);
        _compressionBuffer = new byte[_source.Length];
        _decompressionBuffer = new byte[_source.Length];
        _compressionStream = new MemoryStream();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _compressionStream.Dispose();
        _codec.Dispose();
    }

    [Benchmark]
    public int CompressPayloadToStream()
    {
        _compressionStream.Position = 0;
        _compressionStream.SetLength(0);
        _codec.Compress(_source, _compressionStream);
        return checked((int)_compressionStream.Length);
    }

    [Benchmark]
    public int TryCompressPayloadToPreallocatedBuffer()
    {
        // Mirrors Arrow writer's ITryCompressionCodec decision: false means the buffer would be stored uncompressed.
        return _codec is ITryCompressionCodec tryCompressionCodec &&
            tryCompressionCodec.TryCompress(_source, _compressionBuffer, out int bytesWritten)
                ? bytesWritten
                : 0;
    }

    [Benchmark]
    public int DecompressPayload()
    {
        return _codec.Decompress(_compressedPayload, _decompressionBuffer);
    }

    private static byte[] CompressPayloadToArray(ICompressionCodec codec, byte[] source)
    {
        using var stream = new MemoryStream();
        codec.Compress(source, stream);
        return stream.ToArray();
    }
}

public enum CompressionBackend
{
    ArrowOfficial,
    Native
}

[MemoryDiagnoser]
public class ArrowCppShimIpcBenchmarks
{
    private RecordBatch _recordBatch = null!;
    private byte[] _officialCompressedStream = null!;
    private byte[] _writeBuffer = null!;
    private byte[] _errorBuffer = null!;
    private IntPtr _nativeRecordBatch;

    [Params(100_000, 500_000, 1_000_000)]
    public int RowCount { get; set; }

    [Params(CompressionCodecType.Lz4Frame, CompressionCodecType.Zstd)]
    public CompressionCodecType Codec { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _recordBatch = ArrowIpcCompressionBenchmarks.CreateRecordBatch(RowCount);
        _officialCompressedStream = ArrowIpcCompressionBenchmarks.WriteCompressedIpcStream(
            new Apache.Arrow.Compression.CompressionCodecFactory(),
            Codec,
            _recordBatch);

        _writeBuffer = new byte[Math.Max(_officialCompressedStream.Length * 4, 1024 * 1024)];
        _errorBuffer = new byte[1024];
        ArrowCppShimNativeMethods.ThrowIfUnavailable();
        _nativeRecordBatch = ArrowCppShimNativeMethods.CreateRecordBatch(RowCount, _errorBuffer);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        ArrowCppShimNativeMethods.FreeRecordBatch(_nativeRecordBatch);
        _nativeRecordBatch = IntPtr.Zero;
        _recordBatch.Dispose();
    }

    [Benchmark]
    public long ReadOfficialCompressedIpcStreamWithArrowCpp()
    {
        return ArrowCppShimNativeMethods.ReadIpcStream(_officialCompressedStream, RowCount, _errorBuffer);
    }

    [Benchmark]
    public long WriteCompressedIpcStreamWithArrowCpp()
    {
        return ArrowCppShimNativeMethods.WriteIpcStream(_nativeRecordBatch, Codec, _writeBuffer, _errorBuffer);
    }
}

internal static class ArrowCppShimNativeMethods
{
    private const string LibraryName = "arrow_cpp_ipc_shim";
    private const int Lz4Frame = 1;
    private const int Zstd = 2;

    public static void ThrowIfUnavailable()
    {
        if (!NativeLibrary.TryLoad(LibraryName, out IntPtr handle) &&
            !NativeLibrary.TryLoad("libarrow_cpp_ipc_shim.so", out handle))
        {
            throw new InvalidOperationException(
                $"Unable to load {LibraryName}. Build benchmarks/arrow-cpp and set LD_LIBRARY_PATH to the directory containing libarrow_cpp_ipc_shim.so.");
        }

        NativeLibrary.Free(handle);
    }

    public static long ReadIpcStream(byte[] input, long expectedRows, byte[] errorBuffer)
    {
        int result = arrow_cpp_read_ipc_stream(input, input.Length, expectedRows, out long rowsRead, errorBuffer, errorBuffer.Length);
        ThrowOnError(result, errorBuffer);
        return rowsRead;
    }

    public static IntPtr CreateRecordBatch(int rowCount, byte[] errorBuffer)
    {
        int result = arrow_cpp_create_record_batch(rowCount, out IntPtr handle, errorBuffer, errorBuffer.Length);
        ThrowOnError(result, errorBuffer);
        return handle;
    }

    public static void FreeRecordBatch(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            arrow_cpp_free_record_batch(handle);
        }
    }

    public static long WriteIpcStream(IntPtr recordBatchHandle, CompressionCodecType codec, byte[] outputBuffer, byte[] errorBuffer)
    {
        int result = arrow_cpp_write_ipc_stream_from_batch(recordBatchHandle, ToNativeCodec(codec), outputBuffer, outputBuffer.Length, out long bytesWritten, errorBuffer, errorBuffer.Length);
        ThrowOnError(result, errorBuffer);
        return bytesWritten;
    }

    private static int ToNativeCodec(CompressionCodecType codec)
    {
        return codec switch
        {
            CompressionCodecType.Lz4Frame => Lz4Frame,
            CompressionCodecType.Zstd => Zstd,
            _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unsupported native Arrow C++ codec")
        };
    }

    private static void ThrowOnError(int result, byte[] errorBuffer)
    {
        if (result == 0)
        {
            return;
        }

        int length = errorBuffer.IndexOf((byte)0);
        string message = Encoding.UTF8.GetString(errorBuffer, 0, length >= 0 ? length : errorBuffer.Length);
        throw new InvalidOperationException($"Arrow C++ shim failed with code {result}: {message}");
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int arrow_cpp_create_record_batch(
        long rowCount,
        out IntPtr handle,
        byte[] error,
        int errorLength);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void arrow_cpp_free_record_batch(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int arrow_cpp_read_ipc_stream(
        byte[] input,
        long inputLength,
        long expectedRows,
        out long rowsRead,
        byte[] error,
        int errorLength);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int arrow_cpp_write_ipc_stream_from_batch(
        IntPtr recordBatchHandle,
        int codec,
        byte[] output,
        long outputCapacity,
        out long bytesWritten,
        byte[] error,
        int errorLength);
}

internal static class CppFixtureExporter
{
    private static readonly int[] RowCounts = [100_000, 500_000, 1_000_000];
    private static readonly CompressionCodecType[] Codecs = [CompressionCodecType.Lz4Frame, CompressionCodecType.Zstd];

    /// <summary>
    /// Exports compressed Arrow IPC streams for the optional Arrow C++ benchmark.
    /// The regular BenchmarkDotNet path remains the default when this switch is absent.
    /// </summary>
    public static bool TryExport(string[] args)
    {
        if (args.Length != 2 || !string.Equals(args[0], "--export-cpp-fixtures", StringComparison.Ordinal))
        {
            return false;
        }

        string outputDirectory = args[1];
        Directory.CreateDirectory(outputDirectory);

        foreach (int rowCount in RowCounts)
        {
            using RecordBatch recordBatch = ArrowIpcCompressionBenchmarks.CreateRecordBatch(rowCount);

            foreach (CompressionCodecType codec in Codecs)
            {
                byte[] ipcStream = ArrowIpcCompressionBenchmarks.WriteCompressedIpcStream(
                    new Apache.Arrow.Compression.CompressionCodecFactory(),
                    codec,
                    recordBatch);

                string codecName = codec switch
                {
                    CompressionCodecType.Lz4Frame => "lz4frame",
                    CompressionCodecType.Zstd => "zstd",
                    _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unsupported fixture codec")
                };

                string filePath = Path.Combine(outputDirectory, $"arrow-ipc-{rowCount}-{codecName}.arrow");
                File.WriteAllBytes(filePath, ipcStream);
                Console.WriteLine($"Wrote {filePath}");
            }
        }

        return true;
    }
}
