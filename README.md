# ArrowNet.Compression.NativeCompressions

High-performance [NativeCompressions](https://github.com/Cysharp/NativeCompressions)-based
compression codec backend for Apache Arrow .NET.

This package exists because Apache Arrow .NET's default compression backend currently uses K4os for
LZ4, and that path was not fast enough for read-heavy Arrow IPC workloads. In this repository's
benchmarks, the NativeCompressions backend reads LZ4-compressed Arrow IPC streams about 44%
faster than Apache Arrow .NET's default compression factory across 100k to 1M-row workloads.

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
dotnet run --project benchmarks/ArrowNet.Compression.NativeCompressions.Benchmarks/ArrowNet.Compression.NativeCompressions.Benchmarks.csproj -c Release -f net8.0 -- --filter "*ArrowIpcCompressionBenchmarks*"
```

Environment for the run below: BenchmarkDotNet 0.15.8, Ubuntu 24.04.2 LTS,
Intel Core i7-14700K, .NET SDK 10.0.107, runtime .NET 8.0.26.

| Rows | Path | Codec | Apache mean | Apache allocated | Native mean | Native allocated | Time difference | Allocated difference |
| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 100k | Write compressed IPC stream | LZ4 frame | 3.229 ms | 6,105.70 KB | 2.716 ms | 5,291.66 KB | 15.9% faster | 13.3% less |
| 100k | Read compressed IPC stream | LZ4 frame | 0.764 ms | 3.79 KB | 0.431 ms | 3.07 KB | 43.5% faster | 19.0% less |
| 100k | Write compressed IPC stream | Zstd | 4.205 ms | 2,762.03 KB | 3.318 ms | 3,064.87 KB | 21.1% faster | 11.0% more |
| 100k | Read compressed IPC stream | Zstd | 1.555 ms | 3.12 KB | 1.313 ms | 3.16 KB | 15.6% faster | 1.3% more |
| 500k | Write compressed IPC stream | LZ4 frame | 15.844 ms | 28,698.06 KB | 14.929 ms | 26,426.71 KB | 5.8% faster | 7.9% less |
| 500k | Read compressed IPC stream | LZ4 frame | 4.039 ms | 4.10 KB | 2.235 ms | 3.42 KB | 44.7% faster | 16.6% less |
| 500k | Write compressed IPC stream | Zstd | 21.681 ms | 13,536.49 KB | 17.133 ms | 15,023.90 KB | 21.0% faster | 11.0% more |
| 500k | Read compressed IPC stream | Zstd | 8.181 ms | 3.45 KB | 6.800 ms | 3.48 KB | 16.9% faster | 0.9% more |
| 1M | Write compressed IPC stream | LZ4 frame | 36.852 ms | 57,450.92 KB | 32.276 ms | 52,845.62 KB | 12.4% faster | 8.0% less |
| 1M | Read compressed IPC stream | LZ4 frame | 8.619 ms | 4.11 KB | 4.761 ms | 3.22 KB | 44.8% faster | 21.7% less |
| 1M | Write compressed IPC stream | Zstd | 41.588 ms | 27,016.95 KB | 36.714 ms | 29,987.13 KB | 11.7% faster | 11.0% more |
| 1M | Read compressed IPC stream | Zstd | 16.717 ms | 3.74 KB | 14.523 ms | 4.14 KB | 13.1% faster | 10.7% more |

The NativeCompressions compression path uses pooled buffers with span-based output APIs to avoid the
temporary compressed `byte[]` allocation used by the one-shot APIs. These numbers are end-to-end Arrow
IPC benchmarks, not pure codec throughput. The write path still includes Arrow IPC writer work and
`MemoryStream.ToArray()` allocation/copy costs. The allocated columns are BenchmarkDotNet
`MemoryDiagnoser` managed allocations per operation, not process peak working set. Difference columns are
computed from the BenchmarkDotNet result values before rounding the displayed mean/allocated columns.
Re-run the benchmark on your target hardware and workload before making deployment decisions.

## Known limitations

- NativeCompressions platform support follows NativeCompressions' runtime packages.
- Strong-name signing is not enabled because NativeCompressions assemblies are currently not strong-named.
- Arrow IPC buffers may include padding after the compressed frame. The decoder implementation is written
  for Arrow's exact-output-size codec contract and should be validated further against more producer payloads.
