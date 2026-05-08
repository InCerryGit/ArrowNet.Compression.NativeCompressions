# Arrow.Compression.NativeCompressions

Optional NativeCompressions-based compression codec backend for Apache Arrow .NET.

This package is not an official Apache Arrow package. It implements Apache Arrow .NET's
`ICompressionCodecFactory` / `ICompressionCodec` extension points so applications can opt into
NativeCompressions for LZ4 and Zstandard compressed Arrow IPC streams.

## Status

- Experimental / preview.
- Targets `net8.0`, `net9.0`, and `net10.0`.
- Depends on `NativeCompressions`, which is currently preview.
- Not strong-named while NativeCompressions assemblies are not strong-named.

## Usage

```csharp
using Apache.Arrow.Ipc;
using Arrow.Compression.NativeCompressions;

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
This repository keeps NativeCompressions as an opt-in dependency instead of adding native runtime
dependencies to Apache Arrow .NET's default compression package.

## Benchmarks

The benchmark project compares this package's `NativeCompressionsCodecFactory` with Apache Arrow
.NET's default `Apache.Arrow.Compression.CompressionCodecFactory` on Arrow IPC read/write paths.
The workload is a deterministic 65,536-row `int + string` record batch.

Command:

```bash
dotnet run --project benchmarks/Arrow.Compression.NativeCompressions.Benchmarks/Arrow.Compression.NativeCompressions.Benchmarks.csproj -c Release -f net8.0 -- --filter "*ArrowIpcCompressionBenchmarks*"
```

Environment for the run below: BenchmarkDotNet 0.15.8, Ubuntu 24.04.2 LTS,
Intel Core i7-14700K, .NET SDK 10.0.107, runtime .NET 8.0.26.

| Path | Codec | Apache.Arrow.Compression | NativeCompressions | Difference |
| --- | --- | ---: | ---: | ---: |
| Write compressed IPC stream | LZ4 frame | 1,742.6 us | 1,840.1 us | 5.6% slower |
| Read compressed IPC stream | LZ4 frame | 533.3 us | 312.6 us | 41.4% faster |
| Write compressed IPC stream | Zstd | 2,525.1 us | 2,169.8 us | 14.1% faster |
| Read compressed IPC stream | Zstd | 1,036.1 us | 881.2 us | 15.0% faster |

These numbers are end-to-end Arrow IPC benchmarks, not pure codec throughput. The write path includes
Arrow IPC writer work and `MemoryStream.ToArray()` allocation/copy costs. Re-run the benchmark on your
target hardware and workload before making deployment decisions.

## Known limitations

- NativeCompressions platform support follows NativeCompressions' runtime packages.
- Strong-name signing is not enabled because NativeCompressions assemblies are currently not strong-named.
- Arrow IPC buffers may include padding after the compressed frame. The decoder implementation is written
  for Arrow's exact-output-size codec contract and should be validated further against more producer payloads.
