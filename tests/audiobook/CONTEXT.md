# tests/audiobook — audiobook.cs 的端到端测试

[xUnit](https://xunit.net/) 测试，跑的是 `dotnet build audiobook.cs` 产出的**真实可执行文件**，不是把源码编进测试程序集（`audiobook.cs` 有顶层语句，一个程序集里只能有一份；而且这样测到的才是真正会发布出去的产物）。

## 运行

```powershell
dotnet test tests/audiobook/Audiobook.Tests.csproj
```

默认跑 75 个用例，约 1 分钟。需要 **.NET SDK 10.0.300 或更高版本**（`audiobook.cs` 用了 `#:include`），以及可用的 ffmpeg / ffprobe。

只有一个用例默认跳过：25 小时长书的时间戳回绕测试，因为它要造一个约 85MB 的文件。设置环境变量后运行：

```powershell
$env:AUDIOBOOK_TEST_LONG = "1"
dotnet test tests/audiobook/Audiobook.Tests.csproj --filter "Category=Long"
```

## 依赖解析

- **工具**：`TestEnv` 用 `dotnet build audiobook.cs -o <临时目录>` 构建一次，所有测试共享。构建失败时如果输出里有「无法识别的指令」，会直接提示是 SDK 版本太旧。
- **ffmpeg / ffprobe**：优先用仓库自带的 `utils/ffmpeg/`（按平台选带不带 `.exe`），找不到时回退到 PATH。路径由 `TestEnv.FFmpeg` / `TestEnv.FFprobe` 暴露。
- **仓库根目录**：用 `[CallerFilePath]` 按源码位置解析（和 `utils/file.cs` 的 `FileUtils.GetScriptFolder()` 同一个手法），不依赖当前工作目录，所以从任何地方跑 `dotnet test` 都可以。

## 文件

| 文件 | 说明 |
|:----:|------|
| `TestEnv.cs` | 集合夹具：构建工具、解析 ffmpeg、生成全部音频素材（约 60 个文件，覆盖 merge 的每条分支）。所有测试类共享一份，只跑一次 |
| `Tool.cs` | `Proc`（跨平台跑进程，不用任何 shell）、`Probe`（ffprobe JSON → 便于断言的强类型字段）、`Text.Flat` |
| `MergeTests.cs` | 三条拼接路径、自然序、封面识别、批量子目录、覆盖保护、空目录/无效路径 |
| `EditTests.cs` | export 格式、apply 往返幂等（含音频裸流 MD5）、章节增删改、元数据覆盖、BOM/CRLF、封面替换 |
| `ErrorTests.cs` | apply 的全部报错路径：每条都要指出行号、都不能改动原文件 |
| `InfoTests.cs` | 格式诊断：缺 stik、brand 退回 M4A、章节轨重复、封面缺 attached_pic |
| `QualityTests.cs` | 码率对齐源最高值、采样率/声道取最大值、无损源告警、异构格式 |
| `DriftTests.cs` | 样本级验证章节边界不随文件数累积漂移 |
| `LongTests.cs` | 超过 24 小时的书时间戳不回绕（默认跳过） |

## 几个不明显的约定

**测试不并行。** `TestEnv.cs` 顶上有 `[assembly: CollectionBehavior(DisableTestParallelization = true)]`：这些测试会在共享工作区里读写同名产物，并行跑会互相踩。

**断言消息内容要用 `Text.Flat()` 或 `ProcessResult.FlatOutput`。** Spectre 会按控制台宽度给人看的消息折行，**输出被重定向时同样会折**，于是「不是 m4b 容器」这种短语可能被拆到两行，直接 `Assert.Contains` 就匹配不上。`--json` 那种机器可读的输出例外，工具那边已经改成走原始 stdout 了。

**素材目录名是有含义的**，每个对应 merge 的一条分支，改名字前先看 `TestEnv.GenerateFixtures()` 里的注释：

| 目录 | 针对的情况 |
|:----:|-----------|
| `uniform` | 3 个一致的 mp3，带 title 标签 |
| `mixedrate` | 同 codec 但采样率/声道不同 |
| `mixedcodec` | codec 不同，必须走 `filter_complex`（走 demuxer 会静默丢帧） |
| `covered` | 源 mp3 带封面，有 2 条流，会让 demuxer 的流匹配错位 |
| `order` | `ch1`/`ch2`/`ch10`，测自然序 |
| `aacsrc` | 参数完全一致的 AAC，应走无损 `-c:a copy` |
| `rate` | 低参数的源排在前面，测「不固定 `-ar`/`-ac` 时整本书会被降采样」 |
| `rate2` | 混合 codec 且低参数在前，测 filter 路径的归一化 |
| `aacmix` | 全是 AAC 但采样率不同，走 demuxer 会静默丢掉约一半音频 |
| `brmix` | 128k + 320k，测码率对齐最高值 |
| `flu` / `lossless` | flac / flac+wav，测无损源告警 |
| `fmts` | flac/wav/mp3/m4a/opus/ogg 异构混合 |
| `cbr_xing` `cbr_noxing` `vbr_noxing` `vbr0_noxing` | tone/silence 交替 6 段，测章节边界漂移；只有 `vbr*_noxing` 会触发解码路径 |

**`fmts` 里 ogg 那一项是可选的。** 仓库自带的 osxexperts 精简 ffmpeg 构建没有 vorbis 编码器，`TestEnv` 会探测并跳过，`QualityTests.HeterogeneousFormats` 按 `env.FormatCount` 实际文件数断言，所以两种情况都能过。

**`DriftTests` 的判据是极差而不是绝对误差。** AAC 编码有一帧 priming（约 23ms），那是恒定偏移、无法也不需要消除；真正要抓的是误差随章节序号增长。做法是把合并结果解码成 s16le PCM，逐样本找出每个 tone 段的起点当真值。
