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
├── Directory.Packages.props    # .cs 应用的中央包管理
├── utils/                      # 共享工具库 → utils/CONTEXT.md
├── web/                        # Web 工具 → web/CONTEXT.md
├── project/                    # .csproj（仅用于 IDE 智能提示）
├── images/                     # 文档图片
├── Workspace_Tools.sln         # VS 解决方案
└── omnisharp.json              # OmniSharp 配置
```

## 命令行工具

`.cs` 是 .NET 10 的基于文件的应用，用 `dotnet run <脚本名>.cs -- [参数]` 运行：文件顶部用 `#:include` 引用 `utils/` 下的共享模块、用 `#:package` 声明 NuGet 包（版本由 `Directory.Packages.props` 集中管理）。`.csx` 是 dotnet-script 脚本，用 `dotnet script <脚本名>.csx -- [参数]` 运行，通过 `#load` 引用共享模块、`#r` 引用 NuGet 包。两种入口都定义一个继承 `AsyncCommand<T>` 的 Spectre.Console.Cli 命令类。

| 脚本 | 说明 | 依赖的 utils 模块 |
|:----:|------|:------------------:|
| `pack-epub.cs` | 漫画打包 epub，支持压缩和自动添加到 Calibre | 7z, calibre, compress, config-holder, epub, file, task, video |
| `recognize-contents.cs` | 通过 ollama 大模型识别图片中的目录并重命名文件 | config-holder |
| `calibre-backup.csx` | 将 calibre 书库加密压缩并备份到百度网盘 | 7z, baidu-pan, config-holder, md5, string |
| `compress-media.csx` | 根据分辨率压缩图片和视频 | compress, console, task |

## 会话内发布 exe 的坑（重要）

当前工作区目录带 Windows「低完整性」标签（`Mandatory Label\Low Mandatory Level`，`(OI)(CI)` 向下继承），这是会话沙箱留下的。**在工作区目录里 `dotnet publish` 出来的 exe 会继承该标签**，Windows 会据此把进程按低完整性启动：读文件、以及操作工作区内同样带标签的文件都正常，但去改名/删除工作区之外的普通目录（例如下载目录）里的文件会失败，报 `Access to the path is denied`（ProcMon 里是 `CreateFile ... Desired Access: <含 Delete> ...` → `ACCESS DENIED`），即使那些文件的 ACL 完全正常。

所以：

- **助手交付在本会话里生成的 exe 时，必须提醒用户手工去掉标签**（管理员终端）：
  ```powershell
  icacls "<exe 路径>" /setintegritylevel Medium
  ```
- 或者把发布输出放到工作区之外，在普通目录里新建/复制的文件不会带这个标签：
  ```powershell
  dotnet publish recognize-contents.cs -o "$env:USERPROFILE\bin"
  ```
- 用户自己手工发布的 exe 没有这个问题。

自查：`icacls <文件>` 里出现 `Mandatory Label\Low Mandatory Level` 就是中招；换目录后重新运行，或让程序打印进程令牌的完整性级别即可确认。
