# ArrowNet.Compression.NativeCompressions

High-performance [NativeCompressions](https://github.com/Cysharp/NativeCompressions)-based
compression codec backend for Apache Arrow .NET.

This package exists because Apache Arrow .NET's default compression backend currently uses K4os for
LZ4, and that path was not fast enough for read-heavy Arrow IPC workloads. In this repository's
benchmarks, the NativeCompressions backend is faster than Apache Arrow .NET's default compression
factory across the measured LZ4 and Zstd Arrow IPC read/write workloads.

This package is not an official Apache Arrow package. It implements Apache Arrow .NET's
`ICompressionCodecFactory` / `ICompressionCodec` extension points so applications can opt into
NativeCompressions for LZ4 and Zstandard compressed Arrow IPC streams.

## Status

- Experimental / preview.
- Targets `net8.0`, `net9.0`, and `net10.0`.
- Depends on [`NativeCompressions`](https://github.com/Cysharp/NativeCompressions), which is currently preview.
- Not strong-named while NativeCompressions assemblies are not strong-named.

## Usage

Install the package:

```bash
dotnet add package ArrowNet.Compression.NativeCompressions
```

```csharp
using Apache.Arrow.Ipc;
using ArrowNet.Compression.NativeCompressions;

var codecFactory = new NativeCompressionsCodecFactory();
using var reader = new ArrowStreamReader(stream, codecFactory);

RecordBatch? batch;
while ((batch = await reader.ReadNextRecordBatchAsync()) is not null)
{
    // consume batch
}
```

## Supported codecs

- `CompressionCodecType.Lz4Frame`
- `CompressionCodecType.Zstd`

## Why this exists

Apache Arrow .NET already allows custom compression backends through `ICompressionCodecFactory`.
This repository keeps NativeCompressions as an opt-in dependency for applications that need faster
Arrow IPC compression/decompression without changing Apache Arrow .NET itself.

## Benchmarks

The benchmark project compares this package's `NativeCompressionsCodecFactory` with Apache Arrow
.NET's default `Apache.Arrow.Compression.CompressionCodecFactory` on Arrow IPC read/write paths.
The workloads are deterministic 100k, 500k, and 1M-row `int + string` record batches.

Command:

```bash
dotnet run --project benchmarks/ArrowNet.Compression.NativeCompressions.Benchmarks/ArrowNet.Compression.NativeCompressions.Benchmarks.csproj -c Release -f net10.0 -- --filter "*ArrowIpcCompressionBenchmarks*"
```

Environment for the run below: BenchmarkDotNet 0.15.8, Ubuntu 24.04.2 LTS,
Intel Core i7-14700K, .NET SDK 10.0.107, runtime .NET 10.0.7.

Backend labels in the benchmark output:

- `ArrowOfficial`: Apache Arrow .NET's default `Apache.Arrow.Compression.CompressionCodecFactory`.
- `Native`: this package's `NativeCompressionsCodecFactory`.

`Uncompressed MB/s` is estimated from the uncompressed Arrow IPC stream size divided by mean execution
time. It intentionally does not use the compressed LZ4/Zstd payload size.

| Rows | Path | Codec | ArrowOfficial mean | ArrowOfficial uncompressed MB/s | Native mean | Native uncompressed MB/s | Native time advantage |
| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: |
| 100k | Write compressed IPC stream | LZ4 frame | 3.084 ms | 927.8 | 2.552 ms | 1,121.2 | 20.8% faster |
| 100k | Read compressed IPC stream | LZ4 frame | 0.715 ms | 4,002.2 | 0.428 ms | 6,678.1 | 66.9% faster |
| 100k | Write compressed IPC stream | Zstd | 3.703 ms | 772.8 | 3.468 ms | 825.0 | 6.8% faster |
| 100k | Read compressed IPC stream | Zstd | 1.553 ms | 1,842.6 | 1.316 ms | 2,174.1 | 18.0% faster |
| 500k | Write compressed IPC stream | LZ4 frame | 15.979 ms | 895.2 | 13.026 ms | 1,098.3 | 22.7% faster |
| 500k | Read compressed IPC stream | LZ4 frame | 3.840 ms | 3,725.4 | 2.215 ms | 6,457.8 | 73.3% faster |
| 500k | Write compressed IPC stream | Zstd | 20.031 ms | 714.2 | 17.961 ms | 796.5 | 11.5% faster |
| 500k | Read compressed IPC stream | Zstd | 8.311 ms | 1,721.3 | 6.972 ms | 2,051.7 | 19.2% faster |
| 1M | Write compressed IPC stream | LZ4 frame | 37.443 ms | 764.1 | 31.879 ms | 897.5 | 17.5% faster |
| 1M | Read compressed IPC stream | LZ4 frame | 8.923 ms | 3,206.3 | 4.889 ms | 5,852.1 | 82.5% faster |
| 1M | Write compressed IPC stream | Zstd | 38.698 ms | 739.3 | 35.055 ms | 816.2 | 10.4% faster |
| 1M | Read compressed IPC stream | Zstd | 17.642 ms | 1,621.8 | 15.221 ms | 1,879.6 | 15.9% faster |

The NativeCompressions compression path implements Apache Arrow .NET's `ITryCompressionCodec` fast
path where the current NativeCompressions APIs can safely write to the caller-provided output buffer.
For LZ4, the implementation still needs a pooled max-compressed-size temporary buffer before copying
compressed bytes into Arrow's destination buffer. These numbers are end-to-end Arrow IPC benchmarks,
not pure codec throughput. The write path still includes Arrow IPC writer work and `MemoryStream.ToArray()`
allocation/copy costs. The allocated columns in generated BenchmarkDotNet reports are `MemoryDiagnoser`
managed allocations per operation, not process peak working set. Difference columns are computed from
the BenchmarkDotNet result values before rounding the displayed mean columns. Re-run the benchmark on
your target hardware and workload before making deployment decisions.

An optional Apache Arrow C++ comparison benchmark is available under `benchmarks/arrow-cpp`, with a
small P/Invoke shim used by the managed benchmark project. The latest saved shim write results still
show Arrow C++ ahead of the managed NativeCompressions path for writes. A current rerun of the shim
benchmarks was blocked on this machine because `libarrow_cpp_ipc_shim.so` was present but its
`libarrow.so.2400` runtime dependency was not on the host library path. Treat the C++ comparison as
an optional local benchmark that requires a complete Arrow C++ runtime installation; it is not part of
the NuGet package build.

## Known limitations

- NativeCompressions platform support follows NativeCompressions' runtime packages.
- Strong-name signing is not enabled because NativeCompressions assemblies are currently not strong-named.
- Arrow IPC buffers may include padding after the compressed frame. The decoder implementation is written
  for Arrow's exact-output-size codec contract and should be validated further against more producer payloads.
