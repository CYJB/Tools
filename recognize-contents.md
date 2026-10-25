# 从图片识别书籍目录

支持利用 ollama 调用本地大模型，识别图片中的目录，并重命名相关文件，将目录标题添加到文件名后。

使用 ollama 接口调用大模型，一般来说可以本地部署的 qwen3.5:9b 级别即可完成图片识别工作。

## 用法

`dotnet run recognize-contents.cs -- <path> [extra-prompt] [OPTIONS]`

这是 .NET 10 的[基于文件的应用](https://learn.microsoft.com/zh-cn/dotnet/core/sdk/file-based-apps)，不需要 `.csproj`：文件顶部的 `#:include` 引入 `utils/config-holder.cs`，`#:package` 声明 NuGet 包，包版本由 `Directory.Packages.props` 集中管理。首次运行会还原依赖并编译（几秒钟），之后走构建缓存。

参数：

- `path`：要处理的包含目录的图片路径。
- `extra-prompt`：【可选】识别目录时使用的额外提示，会追加在默认提示词后面。

选项：

- `-m, --model <MODEL>`：临时指定模型，并记入配置文件。
- `--think <LEVEL>`：开启思考模式，可指定程度（`low`/`medium`/`high`）。默认关闭。
- `--retries <COUNT>`：识别结果不合格时最多尝试几次，默认 3 次。
- `--timeout <SECONDS>`：单次识别的超时秒数，默认 300 秒。
- `--dry-run`：只显示识别结果，不重命名文件。
- `--no-chapter`：标题里不追加「第N話」的话数信息（默认追加）。
- `-y, --yes`：不再询问确认，直接重命名文件。

常见用法：

假设目录文件名为 `01.jpg`，识别到的目录为
- 02: foo
- 13: bar

会将 `02.jpg` 重命名为 `02 foo.jpg`，`03.jpg` 重命名为 `03 bar.jpg`。

标题默认按「作品名 + 副标题 + 话数」拼接，各部分用一个空格分隔。例如目录里印着「東京黑盒子 / ～虐待狂教授的困難事件報告～ / 第16話」，会重命名为 `003 東京黑盒子 ～虐待狂教授的困難事件報告～ 第16話.jpg`；不需要话数时加 `--no-chapter`。

先看结果、确认无误后再改名：

```powershell
dotnet run recognize-contents.cs -- '.\002.jpg' --dry-run
```

批量处理时跳过确认：

```powershell
dotnet run recognize-contents.cs -- '.\002.jpg' --yes
```

需要可执行文件、并且希望只有一个文件时，用 `publish`（`build` 只能产出编译产物，散落的 dll、`*.deps.json`、`*.runtimeconfig.json` 都是它的正常输出；单文件只有 `publish` 才会打包）：

```powershell
dotnet publish recognize-contents.cs
```

发布配置已经写在文件顶部，所以不用带任何参数：

```csharp
#:property PublishAot=false                     // 反射式 JSON 序列化需要；AOT 会把它关掉
#:property PublishSingleFile=true               // 打成单文件
#:property SelfContained=false                  // 依赖本机安装的 .NET 10 运行时
#:property RuntimeIdentifier=win-x64
#:property DebugType=embedded                   // pdb 内嵌，不产生额外的 .pdb
```

默认输出到 `artifacts/recognize-contents/recognize-contents.exe`，**就一个文件**，约 2.8 MB。想换输出目录加 `-o <目录>`。

其它变体：

- 要免装运行时（自包含）：`dotnet publish recognize-contents.cs -p:SelfContained=true -p:EnableCompressionInSingleFile=true`，约 37 MB。单文件压缩只在自包含时可用，依赖框架时带上它会被 `NETSDK1176` 拒绝。
- 原生 AOT 在这里不可行：需要反射式 JSON 序列化，而 AOT 会禁用它，`Spectre.Console.Cli` 也不支持 AOT。

## 提示词与失败排查

内置提示词已经覆盖了常见的坑：页码取「右侧色块里的数字」而不是序号、不要把话数当页码、标题逐字转录不翻译、副标题与话数合并成一行、按图中顺序输出。

模型输出如果不合格，脚本会把不合格的原因（例如「这些页码找不到对应的图片文件」）追加到提示词里重新识别，最多尝试 `--retries` 次。

仍然识别不准时，可以用 `extra-prompt` 补充约定，例如：

- 目录是双列排版：`目录分为左右两栏，先读左栏再读右栏。`
- 页码带前缀：`页码形如 P.003，请只取数字部分。`
- 话数写法不固定：`话数统一写成「第N話」的形式。`

## 实现要点

- 结构化输出（JSON Schema）是手写的，字段都必填且不允许 `null`。由类型反射生成的 Schema 会允许 `null`，模型偶尔真的只返回 `null`，脚本拿到空结果后就会抛异常。
- 采样参数固定为温度 0、贪心解码、固定随机种子，同一张图每次结果一致，便于排查和重试。
- 默认关闭思考模式：转录类任务开思考只会变慢、变长，并不会更准。
- 识别结果先与目录中已有的数字文件名比对，再生成重命名计划；如果目录页码与文件编号整体有偏移（缺封面页、编号从 0 开始等），会自动对齐。
- 文件名里的数字前缀直接取原文件本身的前缀，不会因为模型给出的页码位数不同而改变。
