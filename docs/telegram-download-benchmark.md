# Telegram 分块下载离线吞吐模拟

2026-09-10 的受控延迟模拟中，16 MiB 文件的完成时间中位数从双请求窗口的 **1245.35 ms** 降至四请求窗口的 **626.13 ms**，约 **1.99 倍**模拟吞吐量。两组每次均发送 32 个模拟请求，最终字节内容、长度和 SHA-256 完全一致。

这是同一分块下载器在 **2 个与 4 个在途请求**下的受控模型，**不是旧版 WTelegram 下载器的实测对比，也不代表真实 Telegram 账号速度**。模拟没有访问 Telegram、创建客户端、使用账号凭据或设置服务端限速；它验证固定请求等待能否被并发覆盖，不能证明突破免费账号限速。实际使用范围见 [下载提速说明](telegram-download-performance.md)。

## 方法与环境

- 临时 `net8.0` 控制台项目直接链接本仓库的 `TelegramChunkDownloader.cs`、`TelegramDownloadThrottle.cs`、`DynamicConcurrencyGate.cs`，引用 `WTelegramClient 4.4.6` 提供类型；不引用 EasyGet 主项目，不修改依赖或主项目输出目录。
- 固定 32 块，每块 512 KiB，总计 16,777,216 字节。以 `new Random(20260910).NextBytes(payload)` 生成同一份输入数据。
- 每个模拟请求执行 `Task.Delay(70, ct)` 后返回指定偏移的数据。操作系统调度使实际等待可能超过 70 ms；不模拟带宽上限、加密、代理、网络丢包或服务器限流。
- 双请求基线和四请求方案都调用相同的生产 `TelegramChunkDownloader.DownloadAsync`。基线只在 `fetchPart` 外加 `SemaphoreSlim(2, 2)`；四请求方案不加额外限制，由生产下载器限制并发。
- 两组各预热 1 次，然后各测量 5 次。奇数轮先测双请求，偶数轮先测四请求，减轻先后顺序偏差。线程池最小工作线程数和完成端口线程数均设为 8。
- 输出使用预分配容量的 `MemoryStream`；计时包含分块请求等待、数据复制、进度回调和顺序写入，不包含输入数据生成、最终逐字节比较与 SHA-256 计算。结果不用于评估磁盘性能。
- 计数在额外信号量放行后开始，因此峰值表示真正进入模拟请求的数量，不包含等待信号量的任务。所有运行都断言 32 个偏移各请求一次、零残留请求、进度单调且最终完整。
- SDK `10.0.103`，运行时 `.NET 8.0.24`，系统版本字符串 `Microsoft Windows 10.0.26100`，20 个逻辑处理器；Release 编译，最终构建 0 警告、0 错误。
- 最终记录生成于 `2026-09-10T02:01:48.7895467+08:00`。构建前与模拟结束后检查了三个链接源码文件的 SHA-256，均未变化。

## 结果

| 正式测量轮次 | 双请求窗口耗时（ms） | 四请求窗口耗时（ms） |
|---|---:|---:|
| 1 | 1248.2866 | 622.0794 |
| 2 | 1245.3467 | 624.9109 |
| 3 | 1240.7084 | 627.5015 |
| 4 | 1247.2025 | 629.8322 |
| 5 | 1239.8542 | 626.1302 |
| **中位数** | **1245.3467** | **626.1302** |

| 校验项 | 双请求窗口 | 四请求窗口 |
|---|---:|---:|
| 每次请求数 | 32 | 32 |
| 每次峰值在途请求数 | 2 | 4 |
| 每次输出字节数 | 16,777,216 | 16,777,216 |
| 每次完整字节比较 | 通过 | 通过 |
| 每次最终 SHA-256 | 与输入一致 | 与输入一致 |
| 按中位数计算的模拟吞吐量 | 12.85 MiB/s | 25.55 MiB/s |

中位数之比为 `1245.3467 / 626.1302 = 1.988958`，模型中的耗时降低约 `49.72%`。固定延迟、没有带宽上限的模型有利于展示增加窗口的效果；不能把这个比值用于承诺真实账号加速幅度。限流等待、失败重试与取消行为由另行的回归测试覆盖，本吞吐实验没有触发这些条件。

全部 12 次运行（含预热）的最终 SHA-256：

```text
363981D6ACAB4DE10C90E83A7BBF06C65207D04CFD8C527248EF00ED1EC4F250
```

## 复现与审阅

完整控制台项目、计数断言和原始 JSON 保留在本机临时目录：

```text
C:\Users\admin\AppData\Local\Temp\EasyGet.TelegramBenchmark-18ddc6e1f16d42f6805452a27fe3fec3
```

其 `TelegramBenchmark.csproj` 使用 `<Compile Include="..." Link="..." />` 链接上述三个生产源码，所有 `bin`、`obj` 和结果均位于该临时目录。临时目录可能由系统清理，结果表与源码指纹在本文永久记录。

实际执行的复现命令（PowerShell，项目路径不变时）：

```powershell
$benchmarkDirectory = 'C:\Users\admin\AppData\Local\Temp\EasyGet.TelegramBenchmark-18ddc6e1f16d42f6805452a27fe3fec3'
Set-Location -LiteralPath $benchmarkDirectory
dotnet restore TelegramBenchmark.csproj --ignore-failed-sources --verbosity quiet
dotnet build TelegramBenchmark.csproj --configuration Release --no-restore --verbosity quiet
dotnet run --project TelegramBenchmark.csproj --configuration Release --no-build --no-restore |
    Tee-Object -FilePath benchmark-results.json
```

核心模型如下；完整 `Program.cs` 还记录峰值并发、唯一偏移、请求数、进度，并在每次结束后校验字节与哈希：

```csharp
using var limiter = window == 2 ? new SemaphoreSlim(2, 2) : null;
using var output = new MemoryStream(payload.Length);
await TelegramChunkDownloader.DownloadAsync(
    payload.Length, output, FetchAsync, progress, null, CancellationToken.None);

async Task<byte[]> FetchAsync(long offset, int limit, CancellationToken ct)
{
    if (limiter is not null)
        await limiter.WaitAsync(ct);
    try
    {
        await Task.Delay(70, ct);
        return payload.AsSpan(checked((int)offset), limit).ToArray();
    }
    finally
    {
        limiter?.Release();
    }
}
```

本次测量输入与输出文件的 SHA-256（按文件字节计算）：

| 文件 | SHA-256 |
|---|---|
| `Services/TelegramChunkDownloader.cs` | `02344FB003EFFEDD2E704F0F982047362FCCADA80D4BC50DA973A63C58461AA8` |
| `Services/TelegramDownloadThrottle.cs` | `3FFEBE1A2A82D27380C02F4E04FBBE5A5CEAC58C21BF0EB4B4E16C7066CC9326` |
| `Services/DynamicConcurrencyGate.cs` | `2B1B1C4C4C3430434C942CE64362E96F275D20148CA7503A18070A7E26BCE496` |
| 临时 `Program.cs` | `CB4343832D928C2012395C46899D3B5923ECCEE183D6A999876EE68BA3F1F4C2` |
| 临时 `TelegramBenchmark.csproj` | `6F6B7C6A18D2D8C5C69FE3FEB3876F402B66BF3ABC1567C925943C7E3919ED86` |
| 临时 `benchmark-results.json` | `6CE8A0E35F89491C53E491AD81F81D0E0F8261087BD24C63A391CEB763BB84E3` |
