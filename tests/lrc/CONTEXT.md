# tests/lrc — utils/lrc.cs 的单元测试

[xUnit](https://xunit.net/) 测试，**直接把 `utils/lrc.cs` 编进测试程序集**（不像 [tests/audiobook](../audiobook/CONTEXT.md) 那样先构建出 exe 再调用）：歌词解析、时间轴平移、序列化都是纯函数，在进程内断言最省事，也方便穷举边界。

## 运行

```powershell
dotnet test tests/lrc/Lrc.Tests.csproj
```

默认 39 个用例，构建之外几乎不花时间，**不需要 ffmpeg**。

## 为什么要在 csproj 里手工列 Compile

`utils/*.cs` 是 .NET 10 基于文件的应用的模块，顶部有 `#!` / `#:package` / `#:include` 指令，普通工程直接编不了。两个条件缺一不可：

- `<Features>FileBasedProgram</Features>` —— 放开 `#!` / `#:` 指令（`project/*.csproj` 和 IDE 智能提示用的是同一个开关）；
- 手工 `<Compile Include="../../utils/lrc.cs" />` —— 这个模式下 **`#:include` 不会展开**，所以 lrc.cs 依赖的 `code-pages.cs` 也要一起列出来。

`System.Text.Encoding.CodePages` 不用显式引包：GBK 的 `CodePagesEncodingProvider` 在 .NET 10 的共享框架里，`code-pages.cs` 顶部的 `#:package` 在这个模式下被忽略（实测能编能跑）。

## 文件

| 文件 | 说明 |
|:----:|------|
| `ParseTests.cs` | `Lrc.Parse`：小数位补零到毫秒、`:` 分隔百分秒、分钟超 59、一行多时间戳、头信息/只有时间戳的占位行、词级时间戳与 `[tr:]` 标记剥离、排序、CRLF/BOM |
| `CueTests.cs` | `Lrc.BuildCues`：按段平移、首尾相接、超出段长的行丢弃、零长度字幕跳过、`ToLrc`/`ToSrt` 的精确输出、时间戳格式化（含 24 小时以上不回绕）、lrc 往返 |
| `FileTests.cs` | `Lrc.ParseFile` 的编码猜测（UTF-8 / UTF-8+BOM / GBK / UTF-16 大小端）、`FindSidecarFiles` 同名配对、`IsLyricFile` |

## 两个不明显的地方

**命名空间不能叫 `Lrc.Tests`。** 那会声明一个叫 `Lrc` 的命名空间，和被测的 `class Lrc` 撞名，直接 CS0101。所以用 `LrcTests`。

**断言的是精确输出。** `ToLrc` 没有结尾换行、`ToSrt` 用空行分隔且末尾只有一个换行，`FormatLrcTimestamp` 四舍五入到百分秒——这些都是 ffmpeg 和歌词工具实际吃到的字节，改实现时测试会立刻发现。
