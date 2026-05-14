# Optional Arrow C++ IPC Benchmark

This benchmark compares Apache Arrow C++ read and write performance against the
managed Arrow IPC benchmarks in this repository. It is intentionally optional:
the main package and the existing BenchmarkDotNet project do not depend on Arrow C++.

## Install Arrow C++

Install an Arrow C++ package that includes IPC, LZ4 frame, and Zstandard support.
The C++ executable and the managed P/Invoke shim both need the Arrow C++ runtime libraries on the
runtime library path. For example, the shim build produced `libarrow_cpp_ipc_shim.so`, but a later
host rerun could not load it until `libarrow.so.2400` was also available. On Debian/Ubuntu this is
typically `libarrow-dev` from the Apache Arrow package repository. If you build Arrow C++ yourself,
enable:

```bash
-DARROW_IPC=ON -DARROW_WITH_LZ4=ON -DARROW_WITH_ZSTD=ON
```

## Export Shared Fixtures

Generate compressed Arrow IPC stream fixtures from the managed benchmark project:

```bash
dotnet run --project benchmarks/ArrowNet.Compression.NativeCompressions.Benchmarks/ArrowNet.Compression.NativeCompressions.Benchmarks.csproj -c Release -f net8.0 -- --export-cpp-fixtures BenchmarkDotNet.Artifacts/arrow-cpp-fixtures
```

The exporter writes one stream per row-count/codec pair:

- `arrow-ipc-100000-lz4frame.arrow`
- `arrow-ipc-100000-zstd.arrow`
- `arrow-ipc-500000-lz4frame.arrow`
- `arrow-ipc-500000-zstd.arrow`
- `arrow-ipc-1000000-lz4frame.arrow`
- `arrow-ipc-1000000-zstd.arrow`

## Build The C++ Benchmark

```bash
cmake -S benchmarks/arrow-cpp -B BenchmarkDotNet.Artifacts/arrow-cpp-build -DCMAKE_BUILD_TYPE=Release
cmake --build BenchmarkDotNet.Artifacts/arrow-cpp-build --config Release
```

If Arrow C++ is installed in a custom prefix, pass it to CMake:

```bash
cmake -S benchmarks/arrow-cpp -B BenchmarkDotNet.Artifacts/arrow-cpp-build -DCMAKE_BUILD_TYPE=Release -DCMAKE_PREFIX_PATH=/path/to/arrow
```

## Validate Fixtures

Always validate before timing. This catches missing files, codec support issues,
and row-count mismatches.

```bash
BenchmarkDotNet.Artifacts/arrow-cpp-build/arrow_cpp_ipc_benchmark --validate --fixtures BenchmarkDotNet.Artifacts/arrow-cpp-fixtures
```

## Run A Short Native Benchmark

```bash
BenchmarkDotNet.Artifacts/arrow-cpp-build/arrow_cpp_ipc_benchmark --benchmark --fixtures BenchmarkDotNet.Artifacts/arrow-cpp-fixtures --iterations 10
```

The output is CSV:

```text
rows,codec,path,backend,iterations,mean_ms
100000,lz4frame,read compressed IPC stream,arrow-cpp,10,0.123
100000,lz4frame,write compressed IPC stream,arrow-cpp,10,0.456
```

Compare read rows with the managed BenchmarkDotNet read rows from the same
fixtures. Compare write rows with the managed BenchmarkDotNet write rows from
the same deterministic workload shape. Run both on the same machine, OS, CPU
settings, and Arrow versions. The C++ benchmark does not report managed
allocation data.

## Latest Saved Shim Write Comparison

The managed BenchmarkDotNet project also contains `ArrowCppShimIpcBenchmarks`, which calls Arrow C++
through `libarrow_cpp_ipc_shim.so`. The latest saved write-only CSV result shows the shim ahead of
the managed NativeCompressions path for writes:

| Rows | Codec | Native write (.NET 10) | Native uncompressed MB/s | Arrow C++ shim write | Shim advantage |
| ---: | --- | ---: | ---: | ---: | ---: |
| 100k | LZ4 frame | 2.552 ms | 1,121.2 | 1.819 ms | 40.3% faster |
| 100k | Zstd | 3.468 ms | 825.0 | 1.823 ms | 90.3% faster |
| 500k | LZ4 frame | 13.026 ms | 1,098.3 | 9.145 ms | 42.4% faster |
| 500k | Zstd | 17.961 ms | 796.5 | 9.000 ms | 99.6% faster |
| 1M | LZ4 frame | 31.879 ms | 897.5 | 18.830 ms | 69.3% faster |
| 1M | Zstd | 35.055 ms | 816.2 | 18.161 ms | 93.0% faster |

The Native write values above are from the latest .NET 10 `ArrowIpcCompressionBenchmarks` run. The
shim values are the latest saved write-only shim CSV values, so treat this as a directional write-path
comparison rather than a fresh same-run C++ rerun. A current full shim rerun on the host did not produce
read/write timing results because the dynamic loader could not resolve `libarrow.so.2400`. Do not treat
that failed rerun as performance data; fix the Arrow C++ runtime library path first, then rerun the shim
benchmark if you need fresh read numbers.
