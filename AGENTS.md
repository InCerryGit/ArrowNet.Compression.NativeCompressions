# ArrowNet.Compression.NativeCompressions — Agent Notes

Compact repo facts for future OpenCode sessions. Keep this file limited to things an agent is likely to guess wrong.

## Project shape

- Independent, optional NativeCompressions backend for Apache Arrow .NET; never describe it as an official Apache Arrow package.
- Solution file is `ArrowNet.Compression.NativeCompressions.slnx` with three projects: `src/ArrowNet.Compression.NativeCompressions`, `tests/ArrowNet.Compression.NativeCompressions.Tests`, and `benchmarks/ArrowNet.Compression.NativeCompressions.Benchmarks`.
- Public entrypoint is `NativeCompressionsCodecFactory`; codec implementations are internal. Consumers pass the factory to Arrow readers or `IpcOptions.CompressionCodecFactory`.
- Supported codecs are only `CompressionCodecType.Lz4Frame` and `CompressionCodecType.Zstd`. Unsupported codecs should fail explicitly.

## Package and build constraints

- Package id is `ArrowNet.Compression.NativeCompressions`; target frameworks are `net8.0`, `net9.0`, and `net10.0`.
- Runtime dependencies are `Apache.Arrow 23.0.0` and preview `NativeCompressions 0.6.0`; keep README/package metadata clear that this package is experimental/preview.
- `Directory.Build.props` sets `LangVersion=latest`, nullable and implicit usings on, `TreatWarningsAsErrors=true`, `GenerateDocumentationFile=true`, and `SignAssembly=false`.
- Do not enable strong-name signing until NativeCompressions dependencies are strong-named.
- Keep the core package small: no auto-detection, DI abstractions, config system, fallback chain, or Apache Arrow fork/patch unless explicitly requested.

## Verification commands

- Build: `dotnet build -c Release`
- Tests: `dotnet test -c Release`
- Focused tests: `dotnet test -c Release --filter FullyQualifiedName~NativeCompressionsCodecFactoryTests`
- Benchmark build: `dotnet build benchmarks/ArrowNet.Compression.NativeCompressions.Benchmarks/ArrowNet.Compression.NativeCompressions.Benchmarks.csproj -c Release`
- Benchmark dry run: `dotnet run --project benchmarks/ArrowNet.Compression.NativeCompressions.Benchmarks/ArrowNet.Compression.NativeCompressions.Benchmarks.csproj -c Release -f net8.0 -- --filter "*ArrowIpcCompressionBenchmarks*" --job Dry`
- Full benchmarks: `dotnet run --project benchmarks/ArrowNet.Compression.NativeCompressions.Benchmarks/ArrowNet.Compression.NativeCompressions.Benchmarks.csproj -c Release -f net8.0 -- --filter "*ArrowIpcCompressionBenchmarks*"`
- Pack: `dotnet pack -c Release`
- CI workflow: `.github/workflows/ci.yml` restores, builds, and tests on `main` pushes and PRs.
- Release workflow: `.github/workflows/release.yml` publishes to NuGet from `v*.*.*` tags or manual `workflow_dispatch`; it strips a leading `v` and requires the `NUGET_API_KEY` secret.
- There is no repo-local `global.json`, `NuGet.config`, `Directory.Packages.props`, or `.editorconfig`; do not assume pinned SDKs, custom NuGet sources, central package management, or formatter config.

## Tests and known edge cases

- Existing tests are self-contained xUnit round trips using a deterministic 256 KiB payload for LZ4 and Zstd.
- README benchmark numbers must come from this repo's full BenchmarkDotNet project; update them only with the exact command, environment, and result artifact from that run.
- Benchmark code should compare `NativeCompressionsCodecFactory` against `Apache.Arrow.Compression.CompressionCodecFactory` on Arrow IPC read/write paths for both LZ4 frame and Zstd when feasible.
- Current compression path uses pooled buffers with span-based output APIs; avoid reverting to one-shot `Compress(...)` APIs that allocate compressed `byte[]` values.
- Current benchmark workloads are deterministic 100k, 500k, and 1M-row `int + string` Arrow IPC data; write-path results include Arrow IPC writer and `MemoryStream.ToArray()` costs, not pure codec throughput. README benchmark tables include BenchmarkDotNet `Allocated` managed allocation data.
- Arrow IPC buffers may include padding after the compressed frame; preserve the exact-output-size decompression contract and validate any decoder changes against padded producer payloads.

## Files to avoid editing

- Do not edit generated build output under `bin/`, `obj/`, `artifacts/`, or `TestResults/`.
