日常小工具合集。命令行工具基于 .NET 10 的[基于文件的应用](https://learn.microsoft.com/zh-cn/dotnet/core/sdk/file-based-apps)（`.cs`），Web 工具基于 React + Vite。

## 技术栈

| 层        | 技术 |
|:---------:|------|
| 脚本运行  | [基于文件的应用](https://learn.microsoft.com/zh-cn/dotnet/core/sdk/file-based-apps)（.NET 10, C# `.cs`，`dotnet run <file>`） |
| CLI 框架  | [Spectre.Console.Cli](https://spectreconsole.net/) |
| NuGet 包  | Spectre.Console, Cyjb, OllamaSharp 等（`#:package` 声明，版本由 `Directory.Packages.props` 集中管理） |
| 图片压缩  | ImageMagick（Magick.NET） |
| 视频压缩  | [ffmpeg](https://ffmpeg.org/)（`utils/ffmpeg/` 内自带二进制） |
| Web 前端  | React 19 + TypeScript + Vite + [Shineout](https://sheinsight.github.io/shineout-next/) UI |
| CI/CD     | GitHub Actions → GitHub Pages（`.github/workflows/deploy-web.yml`） |

## 项目结构

```
.
├── audiobook.cs / .md          # 有声书合并成带章节的 m4b，也可编辑元数据和章节
├── calibre-backup.cs / .md     # calibre 书库备份（→百度网盘）
├── compress-media.cs / .md     # 图片/视频压缩
├── convert-media.cs            # 音频转 mp3、图片转 png/jpg
├── convert2utf8.cs             # 文本文件转 UTF-8
├── pack-epub.cs / .md          # 漫画打包 epub
├── pg.cs                       # 按站点生成固定密码
├── recognize-contents.cs / .md # 图片识别书籍目录（ollama）
├── rename.cs                   # 重命名图片/视频
├── Directory.Packages.props    # 中央包管理
├── utils/                      # 共享工具库 → utils/CONTEXT.md
├── web/                        # Web 工具 → web/CONTEXT.md
├── project/                    # .csproj（仅用于 IDE 智能提示）
├── tests/                      # 测试（目前只有 audiobook）→ tests/audiobook/CONTEXT.md
├── images/                     # 文档图片
├── Tools.sln                   # VS 解决方案
└── omnisharp.json              # OmniSharp 配置
```

## 命令行工具

用 `dotnet run <脚本名>.cs -- [参数]` 运行：文件顶部用 `#:include` 引用 `utils/` 下的共享模块、用 `#:package` 声明 NuGet 包（版本由 `Directory.Packages.props` 集中管理）。每个入口都定义一个继承 `AsyncCommand<T>`（或同步的 `Command<T>`）的 Spectre.Console.Cli 命令类。

| 脚本 | 说明 | 依赖的 utils 模块 |
|:----:|------|:------------------:|
| `pack-epub.cs` | 漫画打包 epub，支持压缩和自动添加到 Calibre | 7z, calibre, compress, config-holder, epub, file, task, video |
| `recognize-contents.cs` | 通过 ollama 大模型识别图片中的目录并重命名文件 | config-holder |
| `rename.cs` | 重命名目录里的图片和视频，可选把文件数、总大小加到目录名后 | image, video |
| `convert-media.cs` | 把 flac/wav/wv 转成 320Kbps mp3，jp2/heic/webp 转成 png（`--image-format jpg` 可转 jpg） | file, task, video |
| `convert2utf8.cs` | 把 txt 文本转换成 UTF-8 编码 | file, task |
| `pg.cs` | 按「主密码 + 站点」生成固定密码，站点配置在 `pg.config.json` | config-holder |
| `calibre-backup.cs` | 将 calibre 书库加密压缩并备份到百度网盘 | 7z, baidu-pan, config-holder, md5, string |
| `compress-media.cs` | 根据分辨率压缩图片和视频 | compress, console, task |
| `audiobook.cs` | 把目录下的音频合并成带章节的 m4b（`merge`），也可以查看（`info`）、导出（`export`）和修改（`apply`）元数据与章节 | console, file, process, task, video |

入口只需要 `#:include` 直接依赖的模块，间接依赖由各模块自己 `#:include`（因此同一个文件可能被多次引入，编译时的 CS2002 警告属正常现象）。详见 [utils/CONTEXT.md](utils/CONTEXT.md)。

`#:include` 需要 **.NET SDK 10.0.300 或更高版本**。该指令本身从 10.0.300 起已转正，但被引用的模块自己开头也写着 `#!` / `#:package`，这类「传递指令」仍是实验特性，要靠 `ExperimentalFileBasedProgramEnableTransitiveDirectives` 打开——这里已经配在 `Directory.Packages.props` 里了，SDK 10.0.302 实测不打开会报「这是一项实验性功能」。SDK 太旧时会报 `错误: 无法识别的指令 'include'`。

各入口的 `#:property RuntimeIdentifier=$(NETCoreSdkRuntimeIdentifier)` 取当前 SDK 自带的 RID（win-x64 的 SDK 就是 `win-x64`，osx-arm64 的就是 `osx-arm64`），所以 Windows 上的发布产物与写死 `win-x64` 时完全一致，而在 mac 上可以直接 `dotnet run <脚本>.cs -- [参数]` 跑起来做开发调试，不用再加 `-p:RuntimeIdentifier=`。注意用了 `Magick.NET-Q8-x64` 的工具（`compress-media`、`convert-media`、`pack-epub`）在非 Windows 上只能编译、不能真正处理图片，因为那个包只带 win-x64 的原生库。

`project/` 下的 `.csproj` 只用于 IDE 智能提示，不参与 `dotnet run`：用 `<Features>FileBasedProgram</Features>` 放开 `#!` / `#:` 指令，包版本交给 `Directory.Packages.props` 集中管理。

## 测试

目前只有 `audiobook.cs` 有测试，在 `tests/audiobook/`，用 xUnit：

```powershell
dotnet test tests/audiobook/Audiobook.Tests.csproj
```

它是**端到端**测试：夹具用 `dotnet build audiobook.cs -o <临时目录>` 构建出真实的可执行文件再调用，而不是把源码编进测试程序集（`audiobook.cs` 有顶层语句，一个程序集里只能有一份）。ffmpeg 优先用 `utils/ffmpeg/` 下自带的，找不到时回退到 PATH，所以在 Windows 和 macOS 上都能跑。详见 [tests/audiobook/CONTEXT.md](tests/audiobook/CONTEXT.md)。

给这个仓库写测试时有两个坑：

- **断言消息内容要先折叠空白**。Spectre 会按控制台宽度折行，输出被重定向时同样会折，于是「不是 m4b 容器」这种短语可能被拆到两行，直接 `Assert.Contains` 就匹配不上。用 `Text.Flat()` 或 `ProcessResult.FlatOutput`。反过来，`info --json` 那种机器可读的输出绝不能走 `AnsiConsole.WriteLine`（它还会把 JSON 里的 `[ ]` 当 markup 解析），必须走原始 stdout。
- **测试不能并行**。它们共享一个工作区目录，合并产物就放在素材目录旁边。`TestEnv.cs` 顶部有 `[assembly: CollectionBehavior(DisableTestParallelization = true)]`。

## 在工作区发布 exe 的坑（重要）

当前工作区目录带 Windows「低完整性」标签（`Mandatory Label\Low Mandatory Level`，`(OI)(CI)` 向下继承），这是会话沙箱留下的。**在工作区目录里 `dotnet publish` 出来的 exe 会继承该标签，不管是谁发布的**，Windows 会据此把进程按低完整性启动，于是：

- 去改名/删除工作区之外的普通目录（例如下载目录）里的文件会失败，报 `Access to the path is denied`（ProcMon 里是 `CreateFile ... Desired Access: <含 Delete> ...` → `ACCESS DENIED`），即使那些文件的 ACL 完全正常。
- 发布成单文件、并且把原生库打进包里的 exe（如 `pack-epub.cs` 的 `IncludeNativeLibrariesForSelfExtract=true`）会**直接启动失败**：它启动时要把原生库解压到 `%TEMP%\.net\<exe 名>\<hash>`，而 `%TEMP%\.net` 是普通（中完整性）目录，低完整性的进程在里面建目录会被拒绝，报 `Failed to create directory [...\.net\<exe 名>\<hash>] for extracting bundled files. Error code: 5`（5 = 拒绝访问）。

所以：

- **首选**：用工作区根目录的 `publish-exe.cmd`（内部转 `publish-exe.ps1`）发布，它默认输出到 `$env:USERPROFILE\bin`，发布后逐个用 `icacls` 检查 exe 的完整性级别，级别不对就打印需要手动执行的命令（脚本不自己提权）：
  ```powershell
  .\publish-exe.cmd                 # 发布所有 .cs 到 %USERPROFILE%\bin
  .\publish-exe.cmd -SkipPublish    # 只检查已有 exe
  .\publish-exe.cmd -Name calibre-backup -OutDir D:\tools
  ```
  本机对未签名脚本限制较严：直接 `.\publish-exe.ps1` 会报「未对文件进行数字签名」（`Get-ExecutionPolicy` 显示 RemoteSigned 也会被拒，传 `-ExecutionPolicy Unrestricted` 同样不行，**只有 `Bypass` 能跑**），所以用 `.cmd` 包一层；手动等价写法是 `powershell -NoProfile -ExecutionPolicy Bypass -File .\publish-exe.ps1`。
  想把 exe 放回工作区直接运行：脚本**默认会把 exe 复制回当前目录**（覆盖同名文件），复制完重新检查完整性。规律是：覆盖已存在的同名文件会保留它原有的标签（Medium 还是 Medium，因为 `CopyFile` 是截断写入不是删除重建）；**当前目录原本没有同名 exe 时属于新建，会继承工作区的低标签变成 Low**，这时脚本会打印 `icacls "<路径>" /setintegritylevel Medium`（需管理员终端手动执行）。只发布到 `%USERPROFILE%\bin`、不复制回来时加 `-NoCopyBack`：
  ```powershell
  .\publish-exe.cmd                  # 发布到 %USERPROFILE%\bin 并复制回当前目录
  .\publish-exe.cmd -NoCopyBack      # 只发布，不复制回来
  .\publish-exe.cmd -SkipPublish     # 不重新编译，只复制/检查
  ```
- 或者**发布完手工去掉标签**（把标签从低改回中需要提升的权限，用管理员终端）：
  ```powershell
  icacls "<exe 路径>" /setintegritylevel Medium
  ```
- 或者把发布输出放到工作区之外，在普通目录里新建/复制的文件不会带这个标签：
  ```powershell
  dotnet publish pack-epub.cs -o "$env:USERPROFILE\bin"
  ```
- 注意新文件的标签是 `min(发布进程的完整性, 输出目录的完整性)`：在受限/沙箱终端里发布，即使输出到普通目录，产物也是 Low。
- 没有管理员权限时的临时绕法：用 `$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR` 把单文件 exe 的解压目录指到可写位置，但那不解决写 epub 到普通目录的问题，标签最终还是得改。

自查：`icacls <文件>` 里出现 `Mandatory Label\Low Mandatory Level` 就是中招（正常的 exe 是 `Medium Mandatory Level`）；`whoami /groups | findstr Mandatory` 可以看当前进程自己的完整性级别。
