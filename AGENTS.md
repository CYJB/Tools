日常小工具合集。命令行工具基于 .NET 10 的[基于文件的应用](https://learn.microsoft.com/zh-cn/dotnet/core/sdk/file-based-apps)（`.cs`）和 [dotnet-script](https://github.com/dotnet-script/dotnet-script) 脚本（`.csx`），Web 工具基于 React + Vite。

## 技术栈

| 层        | 技术 |
|:---------:|------|
| 脚本运行  | [基于文件的应用](https://learn.microsoft.com/zh-cn/dotnet/core/sdk/file-based-apps)（.NET 10, C# `.cs`，`dotnet run <file>`）、[dotnet-script](https://github.com/dotnet-script/dotnet-script)（`.csx`） |
| CLI 框架  | [Spectre.Console.Cli](https://spectreconsole.net/) |
| NuGet 包  | Spectre.Console, Cyjb, OllamaSharp 等（`.cs` 用 `#:package` 并由 `Directory.Packages.props` 管理版本，`.csx` 用 `#r`） |
| 图片压缩  | [ImageMagick](https://imagemagick.org/)（外部命令行，自动安装） |
| 视频压缩  | [ffmpeg](https://ffmpeg.org/)（`utils/ffmpeg/` 内自带二进制） |
| Web 前端  | React 19 + TypeScript + Vite + [Shineout](https://sheinsight.github.io/shineout-next/) UI |
| CI/CD     | GitHub Actions → GitHub Pages（`.github/workflows/deploy-web.yml`） |

## 项目结构

```
.
├── calibre-backup.csx / .md    # calibre 书库备份（→百度网盘）
├── compress-media.csx / .md    # 图片/视频压缩
├── pack-epub.cs / .md          # 漫画打包 epub
├── recognize-contents.cs / .md # 图片识别书籍目录（ollama）
├── rename.cs                   # 重命名图片/视频
├── Directory.Packages.props    # .cs 应用的中央包管理
├── utils/                      # 共享工具库 → utils/CONTEXT.md
├── web/                        # Web 工具 → web/CONTEXT.md
├── project/                    # .csproj（仅用于 IDE 智能提示）
├── images/                     # 文档图片
├── Workspace_Tools.sln         # VS 解决方案
└── omnisharp.json              # OmniSharp 配置
```

## 命令行工具

`.cs` 是 .NET 10 的基于文件的应用，用 `dotnet run <脚本名>.cs -- [参数]` 运行：文件顶部用 `#:include` 引用 `utils/` 下的共享模块、用 `#:package` 声明 NuGet 包（版本由 `Directory.Packages.props` 集中管理）。`.csx` 是 dotnet-script 脚本，用 `dotnet script <脚本名>.csx -- [参数]` 运行，通过 `#load` 引用共享模块、`#r` 引用 NuGet 包。两种入口都定义一个继承 `AsyncCommand<T>`（或同步的 `Command<T>`）的 Spectre.Console.Cli 命令类。

| 脚本 | 说明 | 依赖的 utils 模块 |
|:----:|------|:------------------:|
| `pack-epub.cs` | 漫画打包 epub，支持压缩和自动添加到 Calibre | 7z, calibre, compress, config-holder, epub, file, task, video |
| `recognize-contents.cs` | 通过 ollama 大模型识别图片中的目录并重命名文件 | config-holder |
| `rename.cs` | 重命名目录里的图片和视频，可选把文件数、总大小加到目录名后 | image, video |
| `calibre-backup.csx` | 将 calibre 书库加密压缩并备份到百度网盘 | 7z, baidu-pan, config-holder, md5, string |
| `compress-media.csx` | 根据分辨率压缩图片和视频 | compress, console, task |

`.cs` 入口只需要 `#:include` 直接依赖的模块，间接依赖由各模块自己 `#:include`（因此同一个文件可能被多次引入，编译时的 CS2002 警告属正常现象）。`utils/` 下同一套工具同时维护 `.cs` 和 `.csx` 两份，改逻辑时两份都要改，详见 [utils/CONTEXT.md](utils/CONTEXT.md)。

`project/` 下的 `.csproj` 只用于 IDE 智能提示，不参与 `dotnet run`：`.cs` 的项目用 `<Features>FileBasedProgram</Features>` 放开 `#!` / `#:` 指令、包版本交给 `Directory.Packages.props` 集中管理；`.csx` 的项目（`calibre-backup`）关闭中央包管理，版本与脚本里的 `#r` 保持一致。

## 在工作区发布 exe 的坑（重要）

当前工作区目录带 Windows「低完整性」标签（`Mandatory Label\Low Mandatory Level`，`(OI)(CI)` 向下继承），这是会话沙箱留下的。**在工作区目录里 `dotnet publish` 出来的 exe 会继承该标签，不管是谁发布的**，Windows 会据此把进程按低完整性启动，于是：

- 去改名/删除工作区之外的普通目录（例如下载目录）里的文件会失败，报 `Access to the path is denied`（ProcMon 里是 `CreateFile ... Desired Access: <含 Delete> ...` → `ACCESS DENIED`），即使那些文件的 ACL 完全正常。
- 发布成单文件、并且把原生库打进包里的 exe（如 `pack-epub.cs` 的 `IncludeNativeLibrariesForSelfExtract=true`）会**直接启动失败**：它启动时要把原生库解压到 `%TEMP%\.net\<exe 名>\<hash>`，而 `%TEMP%\.net` 是普通（中完整性）目录，低完整性的进程在里面建目录会被拒绝，报 `Failed to create directory [...\.net\<exe 名>\<hash>] for extracting bundled files. Error code: 5`（5 = 拒绝访问）。

所以：

- **发布完必须手工去掉标签**（把标签从低改回中需要提升的权限，用管理员终端）：
  ```powershell
  icacls "<exe 路径>" /setintegritylevel Medium
  ```
- 或者把发布输出放到工作区之外，在普通目录里新建/复制的文件不会带这个标签：
  ```powershell
  dotnet publish pack-epub.cs -o "$env:USERPROFILE\bin"
  ```
- 没有管理员权限时的临时绕法：用 `$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR` 把单文件 exe 的解压目录指到可写位置，但那不解决写 epub 到普通目录的问题，标签最终还是得改。

自查：`icacls <文件>` 里出现 `Mandatory Label\Low Mandatory Level` 就是中招（正常的 exe 是 `Medium Mandatory Level`）；`whoami /groups | findstr Mandatory` 可以看当前进程自己的完整性级别。
