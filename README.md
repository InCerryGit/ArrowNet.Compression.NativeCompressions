# ArrowNet.Compression.NativeCompressions

High-performance [NativeCompressions](https://github.com/Cysharp/NativeCompressions)-based
compression codec backend for Apache Arrow .NET.

This package exists because Apache Arrow .NET's default compression backend currently uses K4os for
LZ4, and that path was not fast enough for read-heavy Arrow IPC workloads. In this repository's
benchmarks, the NativeCompressions backend reads LZ4-compressed Arrow IPC streams about 28% to 47%
faster than Apache Arrow .NET's default compression factory across 500k to 2M-row workloads.

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
The workloads are deterministic 500k, 1M, and 2M-row `int + string` record batches.

Command:

```bash
dotnet run --project benchmarks/ArrowNet.Compression.NativeCompressions.Benchmarks/ArrowNet.Compression.NativeCompressions.Benchmarks.csproj -c Release -f net8.0 -- --filter "*ArrowIpcCompressionBenchmarks*"
```

Environment for the run below: BenchmarkDotNet 0.15.8, Ubuntu 24.04.2 LTS,
Intel Core i7-14700K, .NET SDK 10.0.107, runtime .NET 8.0.26.

| Rows | Path | Codec | Apache.Arrow.Compression | NativeCompressions | Difference |
| ---: | --- | --- | ---: | ---: | ---: |
| 500k | Write compressed IPC stream | LZ4 frame | 15.563 ms | 14.576 ms | 6.3% faster |
| 500k | Read compressed IPC stream | LZ4 frame | 3.992 ms | 2.210 ms | 44.6% faster |
| 500k | Write compressed IPC stream | Zstd | 21.669 ms | 17.017 ms | 21.5% faster |
| 500k | Read compressed IPC stream | Zstd | 8.003 ms | 6.761 ms | 15.5% faster |
| 1M | Write compressed IPC stream | LZ4 frame | 32.383 ms | 31.384 ms | 3.1% faster |
| 1M | Read compressed IPC stream | LZ4 frame | 9.057 ms | 4.811 ms | 46.9% faster |
| 1M | Write compressed IPC stream | Zstd | 41.174 ms | 36.390 ms | 11.6% faster |
| 1M | Read compressed IPC stream | Zstd | 16.860 ms | 14.659 ms | 13.1% faster |
| 2M | Write compressed IPC stream | LZ4 frame | 78.786 ms | 73.402 ms | 6.8% faster |
| 2M | Read compressed IPC stream | LZ4 frame | 28.346 ms | 20.444 ms | 27.9% faster |
| 2M | Write compressed IPC stream | Zstd | 92.348 ms | 82.462 ms | 10.7% faster |
| 2M | Read compressed IPC stream | Zstd | 43.349 ms | 40.209 ms | 7.2% faster |

The NativeCompressions compression path uses pooled buffers with span-based output APIs to avoid the
temporary compressed `byte[]` allocation used by the one-shot APIs. These numbers are end-to-end Arrow
IPC benchmarks, not pure codec throughput. The write path still includes Arrow IPC writer work and
`MemoryStream.ToArray()` allocation/copy costs. Re-run the benchmark on your target hardware and workload
before making deployment decisions.

## Known limitations

- NativeCompressions platform support follows NativeCompressions' runtime packages.
- Strong-name signing is not enabled because NativeCompressions assemblies are currently not strong-named.
- Arrow IPC buffers may include padding after the compressed frame. The decoder implementation is written
  for Arrow's exact-output-size codec contract and should be validated further against more producer payloads.
