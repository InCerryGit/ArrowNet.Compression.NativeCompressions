# Apache Arrow .NET 压缩优化：使用 NativeCompressions 实现更快的 IPC 读写

## 前言

在很多数据密集型系统里，Apache Arrow 已经是很常见的内存列式数据格式了。它的优势很直接：跨语言、列式、适合做数据交换。

但是当我们在 .NET 里使用 Arrow IPC，并且开启压缩以后，会遇到一个比较现实的问题：压缩和解压本身会变成读写路径上的成本。

Apache Arrow .NET 默认的压缩实现已经能用，但在一些 read-heavy 的 Arrow IPC 场景里，尤其是 LZ4 读取场景，性能并不算理想。

在 Arrow .NET 23 版本里，我其实已经给 arrow-net 提交过不少性能优化相关的 PR。很多路径优化以后，整体性能已经能看到明显提升。

但 LZ4 这条路继续往下走，就绕不开底层库。Arrow .NET 默认用 K4os 做 LZ4 压缩和解压，继续优化意味着要继续啃 K4os，或者换一个实现。

我最后选了一个更保守的办法：不改 Arrow .NET 的默认实现，基于它已有的压缩扩展点单独做一个可选库。

也就是这个：

```bash
dotnet add package ArrowNet.Compression.NativeCompressions
```

项目地址：

```text
https://github.com/InCerryGit/ArrowNet.Compression.NativeCompressions
```

这个库不是 Apache Arrow 官方包，而是一个可选的高性能压缩后端。它通过 Apache Arrow .NET 暴露出来的 `ICompressionCodecFactory` 扩展点，把底层压缩实现换成了 Cysharp 的 NativeCompressions。

NativeCompressions 仓库地址：

```text
https://github.com/Cysharp/NativeCompressions
```

## 性能对比

先直接看结果。

Benchmark 环境：

- BenchmarkDotNet 0.15.8
- Ubuntu 24.04.2 LTS
- Intel Core i7-14700K
- .NET SDK 10.0.107
- Runtime .NET 10.0.7

测试的是 Arrow IPC 读写路径，不是单纯的 codec micro benchmark。也就是说，写入路径里包含 Arrow IPC writer 和 `MemoryStream.ToArray()` 的成本。

测试命令：

```bash
dotnet run --project benchmarks/ArrowNet.Compression.NativeCompressions.Benchmarks/ArrowNet.Compression.NativeCompressions.Benchmarks.csproj -c Release -f net10.0 -- --filter "*ArrowIpcCompressionBenchmarks*"
```

测试数据是 deterministic 的 `int + string` Arrow RecordBatch，分别测试：

- 10w 行
- 50w 行
- 100w 行

Benchmark 输出里的 backend 名字含义是：

- `ArrowOfficial`：Apache Arrow .NET 官方默认的 `Apache.Arrow.Compression.CompressionCodecFactory`
- `Native`：这个库提供的 `NativeCompressionsCodecFactory`

表里的 `Uncompressed MB/s` 是按压缩前的 Arrow IPC stream 字节数除以平均耗时估算的吞吐，不是压缩后的 LZ4/Zstd payload 吞吐。

结果如下：

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

可以看到，最明显的仍然是 LZ4 read 场景。

在 10w、50w、100w 三组数据下，NativeCompressions 后端分别快了大约 66.9%、73.3%、82.5%，按压缩前数据量估算的吞吐也明显更高。

这轮实现还补上了 Arrow .NET 写路径会使用的 `ITryCompressionCodec` 快路径。Zstd 可以直接写入 Arrow 提供的目标 buffer；LZ4 因为当前 NativeCompressions API 需要最大压缩长度空间，所以仍然会租用一个 pooled temporary buffer，再把实际压缩结果复制到 Arrow 的 destination。

所以这个优化不能简单理解成“所有指标都更好”。更准确地说：

- LZ4 read：收益非常明显，时间和 managed allocation 都更好；
- LZ4 write：时间更快，allocation 明显更少；
- Zstd read/write：时间更快，但 managed allocation 可能略高。

性能优化不能只看一个指标。只看耗时，容易忽略 allocation；只看 allocation，又可能错过真实吞吐收益。

这里的 allocated 是 BenchmarkDotNet `MemoryDiagnoser` 统计出来的 managed allocation per operation，不是进程峰值内存，也不是 native memory。

## 关于 NativeCompressions

NativeCompressions 是 Cysharp 做的 native compression binding / high-level API。

它支持：

- LZ4
- Zstandard
- OpenZL

对于 Arrow .NET 来说，最相关的就是：

- `CompressionCodecType.Lz4Frame`
- `CompressionCodecType.Zstd`

正好对应 Arrow IPC 当前公开的两个压缩 codec。

不过要注意，NativeCompressions 当前仍然是 preview 状态。它的 README 里也明确写了 API 可能变化，不建议直接无脑用于所有生产环境。

在这个库里，它只负责替换 Arrow IPC 的 LZ4/Zstd codec 实现。Arrow 的数据结构、IPC 格式、reader/writer API 还是 Apache Arrow .NET 的。

## Arrow .NET 是怎么接入压缩的？

Apache Arrow .NET 这里设计得比较好，它没有把压缩实现完全写死。

它提供了一个扩展点：

```csharp
ICompressionCodecFactory
```

也就是说，只要实现这个 factory，就可以让 Arrow reader / writer 使用自己的 codec。

使用方式大概是这样：

```csharp
using Apache.Arrow.Ipc;
using ArrowNet.Compression.NativeCompressions;

var options = new IpcOptions
{
    CompressionCodecFactory = new NativeCompressionsCodecFactory(),
    CompressionCodec = CompressionCodecType.Lz4Frame
};
```

如果使用 Zstd，把 `CompressionCodec` 改成 `CompressionCodecType.Zstd` 即可。

所以这个库可以做得很小。

不需要 fork Apache Arrow，也不需要改 Arrow 的源码，只需要实现它已经暴露出来的 codec factory 即可。

## NativeCompressionsCodecFactory 做了什么？

核心入口就是：

```csharp
NativeCompressionsCodecFactory
```

它负责根据 Arrow 的 `CompressionCodecType` 创建对应 codec。

目前只支持两个：

```csharp
CompressionCodecType.Lz4Frame
CompressionCodecType.Zstd
```

不支持的 codec 会直接抛 `NotSupportedException`。

这样做有一个好处：失败是显式的。

压缩格式这种东西最怕静默 fallback。你以为用了某个高性能 backend，实际却 fallback 到别的实现，这种问题很难排查。所以这里宁可直接失败，也不要偷偷降级。

## LZ4 和 Zstd 的实现思路

实现上分别有两个 internal codec：

- `NativeCompressionsLz4CompressionCodec`
- `NativeCompressionsZstdCompressionCodec`

LZ4 路径使用 NativeCompressions 的 LZ4 API。

Zstd 路径使用 NativeCompressions 的 Zstandard API，默认压缩级别是 3。

更值得注意的是，压缩路径尽量走 Arrow .NET 的 `ITryCompressionCodec` 快路径，避免 writer fallback 到额外的 stream/temporary array 路径。

Zstd 可以直接压缩到 Arrow 提供的目标 buffer。LZ4 这边受 NativeCompressions 当前 one-shot API 约束，需要先租用一个最大压缩长度的临时 buffer，确认压缩结果比原始数据更小以后，再复制到 Arrow 的 destination。

所以现在的实现使用了：

- `ITryCompressionCodec`
- `ArrayPool<byte>.Shared`
- span-based output API
- 最大压缩长度预估
- 压缩完成后只写实际压缩长度

这样做不是严格意义上的“零拷贝”，但已经是比较接近当前接口约束下的 minimal-copy 路径。

对于解压路径，Arrow 会给出目标输出大小。codec 只需要把压缩 payload 解到 Arrow 期望的目标 buffer 里即可。

这里还有一个细节：Arrow IPC buffer 里可能存在 padding，所以 decoder 不能简单假设输入长度就等于压缩帧的精确长度。实现需要遵守 Arrow 的 exact-output-size contract。

## Benchmark 是怎么设计的？

Benchmark 不是只测 codec 本身，而是测端到端 Arrow IPC 读写路径。

主要有两个 benchmark：

```csharp
WriteCompressedIpcStream()
ReadOfficialCompressedIpcStream()
```

参数有三组：

```csharp
[Params(100_000, 500_000, 1_000_000)]
public int RowCount { get; set; }

[Params(CompressionCodecType.Lz4Frame, CompressionCodecType.Zstd)]
public CompressionCodecType Codec { get; set; }

[Params(CompressionBackend.ApacheArrowCompression, CompressionBackend.NativeCompressions)]
public CompressionBackend Backend { get; set; }
```

也就是：

- 3 个数据量
- 2 个 codec
- 2 个 backend
- 读写两个路径

总共 24 组结果。

另外 benchmark 加了：

```csharp
[MemoryDiagnoser]
```

这个属性也是 README 表格里 allocated 数据的来源。

读路径还有一个特意设计：两边 backend 解压的是同一份由 Apache Arrow 官方 compression factory 写出来的 payload。

这样可以避免“不同 writer 生成不同 payload”影响读路径对比。

## 为什么要写成一个独立包？

一开始我并不是奔着“新建一个库”去的。

前面在 Arrow .NET 23 上做性能优化时，很多问题都还能在 arrow-net 自己的代码里解决。但 LZ4 不太一样。越往下看，越像是底层库本身的事情。

Arrow .NET 默认使用 K4os 做 LZ4 后端。如果继续沿着这条路优化，就需要深入 K4os 的实现细节；如果直接替换 Arrow .NET 的默认压缩库，又会带来更大的兼容性和维护成本。

所以最终的选择是：不动默认实现，基于 Arrow .NET 已有的 `ICompressionCodecFactory` 扩展点，做一个可选后端。

这样既不用 fork Apache Arrow，也不用改变默认行为。需要这部分性能收益的用户，可以主动安装并切换到 NativeCompressions 后端；不需要的人，则完全不受影响。

这个边界对我来说很重要。

所以这个库刻意保持得很小：

- 不做自动检测
- 不做 DI 封装
- 不做 fallback chain
- 不 patch Apache Arrow
- 不支持 Arrow 当前没有公开的 codec

只做一件事：提供一个 NativeCompressions-backed codec factory。

## 使用方式

安装：

```bash
dotnet add package ArrowNet.Compression.NativeCompressions
```

然后像前面一样，在 `IpcOptions` 里把 `CompressionCodecFactory` 设置成 `NativeCompressionsCodecFactory`，再选择 `Lz4Frame` 或 `Zstd`。

如果主要是读 Arrow IPC stream，也是在构造 reader 时传入相应 options / factory。

具体接入点取决于使用的是 `ArrowStreamReader`、`ArrowFileReader`，还是 IPC writer。

## 目前的限制

这个库目前有几个明确限制。

第一，只支持：

- LZ4 frame
- Zstd

其他 codec 会直接失败。

第二，NativeCompressions 当前还是 preview。它的 API 和 runtime package 后续可能会变化。

第三，当前没有 strong-name signing。原因是 NativeCompressions 相关依赖目前不是 strong-named。

第四，benchmark 结果只代表当前仓库里的测试环境和 workload。真实 workload 如果字段类型、字符串分布、压缩比例、IO 方式不同，结果也可能不同。

第五，表格里的 allocation 口径要看清楚。它不是 native memory，也不是进程峰值工作集。

## 总结

这次优化的核心其实不是“换个库”这么简单，而是利用 Arrow .NET 已经设计好的扩展点，把压缩后端替换成 NativeCompressions，并且用真实 Arrow IPC 路径做 benchmark 验证。

当前结果看下来：

- LZ4 read 是最值得关注的场景，当前 .NET 10 测试里快了大约 66.9% 到 82.5%；
- LZ4 write 也有稳定收益，按压缩前数据量估算的吞吐更高；
- Zstd 时间上也更快，收益比 LZ4 read 小一些；
- 这个库可以当作 Arrow .NET 的可选高性能压缩后端；
- 由于 NativeCompressions 仍是 preview，生产使用前建议结合自己的 workload 重新 benchmark。

最后，性能优化一定要回到真实路径里验证。

只测 codec throughput 当然有意义，但如果真实业务走的是 Arrow IPC reader/writer，那么 end-to-end IPC benchmark 才更接近真正会感受到的性能。

## 参考资料

- ArrowNet.Compression.NativeCompressions  
  https://github.com/InCerryGit/ArrowNet.Compression.NativeCompressions

- Cysharp NativeCompressions  
  https://github.com/Cysharp/NativeCompressions

- Apache Arrow .NET `ICompressionCodecFactory`  
  https://arrow.apache.org/dotnet/current/api/Apache.Arrow.Ipc.ICompressionCodecFactory.html

- Apache Arrow .NET `IpcOptions`  
  https://arrow.apache.org/dotnet/current/api/Apache.Arrow.Ipc.IpcOptions.html

- Apache Arrow .NET `CompressionCodecType`  
  https://arrow.apache.org/dotnet/current/api/Apache.Arrow.Ipc.CompressionCodecType.html
