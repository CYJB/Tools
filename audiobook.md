# 有声书 m4b 合并与编辑

把目录下的多个音频文件合并成单个带章节标记的 `.m4b`，也可以查看和修改已有 m4b 的元数据与章节。

Calibre 的数据模型是「一条记录 → 每个格式恰好一个文件」，所以「一本书 = 一堆章节音频」这种有声书结构它原生管不了，直接把多文件拖进去会被拆成多本书。合并成带章节的单个 m4b 之后，Calibre、Apple Books、Audiobookshelf 都能正常识别，手机上还能断点续听和按章跳转。

依赖 `utils/ffmpeg/` 下自带的 ffmpeg / ffprobe。

## 用法

`dotnet run audiobook.cs -- <命令> [参数] [OPTIONS]`

这是 .NET 10 的[基于文件的应用](https://learn.microsoft.com/zh-cn/dotnet/core/sdk/file-based-apps)，不需要 `.csproj`：文件顶部的 `#:include` 引入 `utils/` 下的共享模块，`#:package` 声明 NuGet 包，包版本由 `Directory.Packages.props` 集中管理。首次运行会还原依赖并编译（几秒钟），之后走构建缓存。

> `#:include` 需要 **.NET SDK 10.0.300 或更高版本**（或 .NET 11 Preview 3+）。

四个子命令：

| 命令 | 说明 |
|:----:|------|
| `merge <dir>` | 把目录下的音频合并成带章节的单个 m4b |
| `info <file>` | 显示元数据、章节和格式诊断 |
| `export <file>` | 导出元数据和章节，供手工编辑 |
| `apply <file>` | 把编辑后的内容写回 m4b |

### merge

`dotnet run audiobook.cs -- merge [path] [OPTIONS]`

- `path`：要合并的音频目录，默认为当前目录。**目录下直接有音频时合并成一个 m4b；没有音频时，会逐个合并它的子目录**（每个子目录一本书）。
- `-o, --output`：输出路径，默认为「`<父目录>/<目录名>.m4b`」。批量合并时忽略此选项。
- `-t, --title`：书名，默认取目录名。
- `-a, --artist`：作者。
- `--album`：系列名。
- `--genre`：流派，默认「有声书」。
- `--date`：出版年份。
- `--comment`：简介。
- `-b, --bitrate`：AAC 码率，仅在需要重新编码时生效，默认 `auto`。
- `--sample-rate`：输出采样率，默认取所有源文件里的最大值。
- `-c, --cover`：封面图片，默认在目录里自动识别（`cover.*` / `folder.*` / `0*.jpg` 等）。
- `--chapter-title`：章节标题来源，`filename`（默认，用文件名）/ `tag`（用音频里的 title 标签，缺失时回退到文件名）/ `number`（用「第 N 章」）。
- `-y, --overwrite`：覆盖已存在的输出文件。

支持的音频格式：`mp3` `mp2` `m4a` `m4b` `aac` `flac` `wav` `ogg` `opus` `wma` `aif` `aiff`。

文件按**自然序**排列，所以 `ch2` 会排在 `ch10` 之前（普通的字符串排序会把 `ch10` 排在 `ch2` 前面，整本书顺序错乱而且不会有任何提示）。

### 音质：默认不引入额外损失

三条路径，工具自动选择并在输出里说明：

| 源文件情况 | 路径 | 音质 |
|-----------|------|------|
| 全部是 AAC，且 codec、采样率、声道都一致 | `-c:a copy` | **完全无损**，音频流逐位搬运，几秒合完一本书 |
| codec 一致，只是采样率/声道/码率不同 | concat demuxer + AAC 重编码 | 有损，但码率对齐源 |
| codec 不一致、参数不一致，或任一源带封面流 | `-filter_complex` + AAC 重编码 | 同上 |

`-b auto`（默认）的取值规则：

- **有损源（mp3 等）**：对齐所有源里最高的码率。AAC-LC 的编码效率高于 mp3，同码率下不会更差，所以「不高于原 mp3 的损失」这条是成立的。
- **无损源（flac / wav / aiff 等）**：用封顶的 320k。无损源没有「对齐码率」的意义（1411k 的 wav 和 130k 的 flac 都是无损），而且这一步必然是有损的，工具会明确提示并**提醒你备份原始无损文件**。
- 上下限是 32k～320k。再往上没有收益：ffmpeg 的原生 aac 编码器会自己饱和，实测单声道请求 320k 只出约 130k、立体声约 207k。

采样率和声道数一律取所有源里的**最大值**并显式固定。降采样和 downmix 是不可逆的损失，升采样只是补零。

### info

`dotnet run audiobook.cs -- info <file> [--json]`

除了元数据和章节表，还会诊断几个「文件能播但体验不对」的问题：`major_brand` 不是 `M4B ` 时播放器会把它当普通音频、`media_type`(stik) 缺失时 Apple Books 不提供断点续听、章节轨多于一条时播放器可能显示过期的章节、封面流没标记 `attached_pic` 时可能不显示封面。

### export / apply

```powershell
dotnet run audiobook.cs -- export book.m4b     # 生成 book.txt
# ...用任意文本编辑器改 book.txt...
dotnet run audiobook.cs -- apply book.m4b      # 写回，原文件留 book.m4b.backup
```

`apply` 是**无损**的：音频流用 `-c copy` 直接搬运，逐位不变（可以提取 AAC 裸流比对 MD5 验证）。只有元数据和章节被重写。

- `-i, --input`：章节文件，默认为同名的 `.txt`。
- `-c, --cover`：替换封面图片。
- `-o, --output`：输出到新文件，默认原地替换。
- `--no-backup`：原地替换成功后删除 `.backup`。

`apply` 只接受 `.m4b` / `.m4a` / `.mp4` / `.m4v` 容器。ffmpeg 其实能把 mp3 音频塞进 m4b 并打上 `mp4a` 的标签，它自己能解码，但 Apple Books 之类的播放器不认，而且不会报错——所以这里直接拒绝。手上的 mp3 请用 `merge`（单个文件也可以放进一个目录后 merge，会重新编码为 AAC）。

## 章节文件格式

`export` 生成、`apply` 读取的是同一个纯文本文件：

```
# 有声书信息与章节列表
# 编辑后用 audiobook apply 写回 m4b。以 # 开头的是注释，空行会被忽略。

title = 三体
artist = 刘慈欣
album = 地球往事三部曲
genre = 有声书
date = 2023

# 共 3 章，总时长 01:23:45.678
[00:00:00] 第一章 疯狂年代
[00:18:42.500] 第二章 沉默的两年
[01:03:10] 第三章 红岸
```

**元数据**写成「`键 = 值`」，可用的键：`title` `artist` `album` `album_artist` `genre` `date` `comment` `description` `composer` `copyright` `publisher` `synopsis`。写别的键会报错（避免 `titel` 这种拼写错误被静默丢掉）。

**章节**每行一条，行首的 `[时:分:秒]` 是这一章的开始时间，可以带 `.毫秒`，也可以只写 `[分:秒]`。

关键点：**只需要写开始时间**。每章的结束时间自动等于下一章的开始时间，最后一章到文件结尾，所以章节总是首尾相接——空隙和重叠在格式层面就不可能写出来。于是：

| 想做什么 | 怎么改 |
|---------|-------|
| 移动章节边界 | 改那一行的时间戳，上一章的结束时间自动跟随 |
| 合并两章 | 删掉一行 |
| 拆分一章 | 插入一行 |
| 改章节标题 | 改时间戳后面的文字 |
| 删除某个元数据 | 把值留空，例如 `artist =` |

`apply` 以原文件的元数据为基础、用文件里的键覆盖，所以只写了章节的文件不会把原有的书名、作者一起清掉。

**所有错误都会指出行号**，而且在调用 ffmpeg 之前就拦下来，不会留下半成品：时间戳无法解析、时间戳不是严格递增（会同时指出冲突的两行）、时间戳超出总时长、第一章不是 `[00:00:00]`、未知的元数据键、无法归类的行。

ffmpeg 自己的 ffmetadata 格式（`[CHAPTER]` / `TIMEBASE` / `START` / `END`）由工具在临时目录里生成，不需要也不应该手写：它的 `TIMEBASE` 是 1/10000000（100 纳秒单位），`END` 要自己算，值里的 `\ ; # =` 要转义，而且用记事本另存加上 BOM 之后 ffmpeg 会**静默丢掉第一章**并返回 0。

## 常见用法

1. 合并一本书

```powershell
dotnet run audiobook.cs -- merge 三体
```

```
三体/
├── cover.jpg
├── 01.mp3
├── 02.mp3
└── 03.mp3
```

生成 `三体.m4b`（和 `三体/` 同级），3 个章节，`cover.jpg` 作为封面，书名取目录名。

2. 批量合并一个书架

```powershell
dotnet run audiobook.cs -- merge D:\有声书 -a 刘慈欣 --genre 科幻
```

```
有声书/
├── 三体/
│   ├── 01.mp3
│   └── 02.mp3
└── 球状闪电/
    ├── 01.mp3
    └── 02.mp3
```

生成 `有声书/三体.m4b` 和 `有声书/球状闪电.m4b`，并行处理。输出文件放在子目录**外面**，所以重复运行不会把上次的结果再合并进去。

3. 章节名用 mp3 里的标签而不是文件名

```powershell
dotnet run audiobook.cs -- merge 三体 --chapter-title tag
```

4. 已经是 m4a/AAC 的源文件走无损合并

源文件全部是同采样率、同声道的 AAC 时，`merge` 会用 `-c:a copy` 直接搬运音频流，不重新编码，几秒钟就能合并完一本书，音质无损。工具会自动判断并在输出里说明走的是哪条路径。

5. 修正章节

```powershell
dotnet run audiobook.cs -- info 三体.m4b      # 先看看现在是什么样
dotnet run audiobook.cs -- export 三体.m4b    # 生成 三体.txt
notepad 三体.txt                              # 改
dotnet run audiobook.cs -- apply 三体.m4b     # 写回，原文件留 三体.m4b.backup
```

## ffmpeg 在这个场景下的静默失败

这个工具的主要价值不是「拼命令」，而是把下面这些**退出码为 0、不报警告、但结果是错的**的坑固化成显式处理。手搓 ffmpeg 时每一条都可能踩到：

| 坑 | 后果 | 本工具的处理 |
|----|------|-------------|
| 省略 `-map_chapters` | ffmpeg 取「第一个有章节的输入」，改完章节后旧章节被静默保留 | 总是显式传 `-map_chapters`，无章节时传 `-1` |
| `apply` 用 `-map 0` | 原有的章节轨（一条 `codec_type=data` 的流）被一起带上，和新章节表叠加成**两条**章节轨，播放器显示过期的那份 | 用 `-map 0:a -map 0:v?` 显式列举 |
| `-c copy` 重封装 | `major_brand` 是 muxer 选项、不走 metadata 映射，每次都会退回 `M4A `，文件不再被当成有声书 | 每次都重新传 `-brand "M4B "` 和 `-metadata media_type=2` |
| `-brand M4B`（少一个空格） | 被**静默忽略**并退回 `isom`，没有任何警告 | 常量固定为 4 字符 `"M4B "`，并走 `ArgumentList` 传参以保住末尾空格 |
| 不传 `-metadata media_type=2` | 文件里根本没有 `stik` atom，Apple Books 不提供断点续听 | 每次都传 |
| 混合 codec 走 concat demuxer | 静默丢帧，日志刷 `PTS ... invalid dropping`，退出码 0 | 探测到 codec 不一致时改走 `-filter_complex concat` |
| **缺 Xing/VBRI 头的 mp3 走 concat demuxer** | demuxer 是拿「按码率估算的容器时长」决定每一段有多长的，所以**每个非末尾文件都被截断**（实测一本 30 分钟的书少了 1.2 秒），退出码 0、日志里没有任何异常，只有回读总时长才看得出来 | 看到 ffprobe 的「Estimating duration from bitrate」就改走 `-filter_complex`，章节边界也改用解码时长 |
| 混合采样率走 `-c:a copy` | 静默损坏：48kHz 的采样按 44.1kHz 解释，约 9% 变调，时长显示还正常 | 只有 codec、采样率、声道全一致时才 copy |
| **AAC(m4a) 源混合采样率走 concat demuxer** | 静默丢掉约一半音频（实测 6 秒素材只出 3 秒）。mp3 源看似能靠自动重采样蒙对，AAC 不行 | demuxer 路径要求采样率和声道也一致，否则走 `-filter_complex` |
| **不固定 `-ar` / `-ac`** | ffmpeg 按**第一个输入**协商参数：22050/mono 的文件排在 44100/stereo 前面时，整本书输出成 22050/mono。也就是说章节文件的排列顺序会决定音质 | 显式传 `-ar <最大采样率> -ac <最大声道数>` |
| **`concat` 滤镜的参数协商** | 同样按第一个输入协商。实测日志：`44100Hz stereo -> 22050Hz mono -> 44100Hz stereo`，即先降采样/downmix 再升回来，11kHz 以上永久丢失，而输出仍宣称 44100/stereo | 每个输入先显式 `aresample=<max>,aformat=sample_fmts=fltp:channel_layouts=<layout>` 归一化，再拼接 |
| 源 mp3 带封面 | 有 2 条流，concat demuxer 的流匹配会错位 | 探测到视频流就走 `-filter_complex` |
| 按容器时长累加算章节边界 | 缺 Xing/VBRI 头的 mp3 每个文件差几十毫秒到一秒多（内容越不均匀差得越多），**逐章累积** | ffprobe 提示「Estimating duration from bitrate」时，自动改用完整解码得到的时长 |
| **flac 的 `stream.bit_rate` 是 N/A** | 「对齐源码率」拿不到值。反过来 mp3/m4a 的 `format.bit_rate` 又因容器和标签开销虚高 2~4%（实测 mp3 `stream=128000` 而 `format=129838`） | stream 优先、format 兜底 |
| 文件名按字符串排序 | `ch10` 排在 `ch2` 前面，整本书顺序错乱且不报错 | 自然序 |
| 章节文件带 UTF-8 BOM | ffmpeg 静默丢掉第一章，退出码 0 | 自己解析文件、剥掉 BOM，再在临时目录里生成干净的 ffmetadata |
| `ffmpeg -f ffmetadata` 导出 | 吐 `TIMEBASE=1/10000000`（100 纳秒单位）、把末章 `END` 扩到总时长、并带出 `major_brand` / `minor_version` / `compatible_brands` / `encoder` 这些 muxer 内部键；原样喂回去会污染文件 | `export` 从 `ffprobe -show_chapters` 的 JSON 自己生成，只输出白名单里的键 |
| `format.duration` | 它是所有流的最大值，会被章节轨撑大，拿它验证输出等于用输入验证输入 | 一律用音频流自己的 `duration` |

另外，`merge` 和 `apply` **每次都会回读校验输出**（章节数、章节标题、章节起点、`major_brand`、`media_type`、章节轨条数、封面流条数、音频时长），校验通过才落位；失败时原文件不动、临时文件清掉。因为 ffmpeg 在上面至少四种情况下都会返回 0，不回读就等于赌运气。

## 测试

端到端测试在 [tests/audiobook/](tests/audiobook/CONTEXT.md)，用 xUnit：

```powershell
dotnet test tests/audiobook/Audiobook.Tests.csproj
```

默认 75 个用例、约 1 分钟，覆盖三条拼接路径、自然序、章节增删改、往返幂等（含音频裸流 MD5 比对）、BOM/CRLF、全部报错路径、格式诊断、码率/采样率/声道对齐，以及用样本级 PCM 分析验证章节边界不会累积漂移。

跑的是 `dotnet build audiobook.cs` 产出的真实可执行文件，Windows 和 macOS 都能跑。唯一一个默认跳过的用例是 25 小时长书的时间戳回绕测试（要造一个约 85MB 的文件），用 `$env:AUDIOBOOK_TEST_LONG = "1"` 打开。

## 发布为 exe

```powershell
dotnet publish audiobook.cs
```

发布配置已经写在文件顶部：

```csharp
#:property PublishAot=false            // 反射式 JSON 序列化需要；AOT 会把它关掉
#:property PublishSingleFile=true      // 打成单文件
#:property SelfContained=false         // 依赖本机安装的 .NET 10 运行时
#:property RuntimeIdentifier=$(NETCoreSdkRuntimeIdentifier)  // 取当前 SDK 自带的 RID，Windows 上就是 win-x64
#:property DebugType=embedded          // pdb 内嵌，不产生额外的 .pdb
```

不涉及 Magick.NET 的原生库，所以不需要 `IncludeNativeLibrariesForSelfExtract`。

也可以用仓库根目录的 `publish-exe.cmd`（会自动发现所有顶层 `.cs`，并处理 Windows 低完整性标签的问题，详见 [AGENTS.md](AGENTS.md)）：

```powershell
.\publish-exe.cmd -Name audiobook
```

> ⚠️ 发布出来的 exe 仍然需要能访问 `utils/ffmpeg/` 下的 `ffmpeg.exe` 和 `ffprobe.exe`：路径是按源码位置解析的（`FileUtils.GetScriptFolder()`），不能只把 exe 拷到别的机器上。
