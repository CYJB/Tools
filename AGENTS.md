日常小工具合集。命令行工具基于 C# 脚本（`.csx`），Web 工具基于 React + Vite。

## 技术栈

| 层        | 技术 |
|:---------:|------|
| 脚本运行  | [dotnet-script](https://github.com/dotnet-script/dotnet-script)（.NET 9, C# `.csx`） |
| CLI 框架  | [Spectre.Console.Cli](https://spectreconsole.net/) |
| NuGet 包  | Spectre.Console, Cyjb, OllamaSharp, System.Text.Json 等（脚本内 `#r` 引用） |
| 图片压缩  | [ImageMagick](https://imagemagick.org/)（外部命令行，自动安装） |
| 视频压缩  | [ffmpeg](https://ffmpeg.org/)（`utils/ffmpeg/` 内自带二进制） |
| Web 前端  | React 19 + TypeScript + Vite + [Shineout](https://sheinsight.github.io/shineout-next/) UI |
| CI/CD     | GitHub Actions → GitHub Pages（`.github/workflows/deploy-web.yml`） |

## 项目结构

```
.
├── calibre-backup.csx / .md    # calibre 书库备份（→百度网盘）
├── compress-media.csx / .md    # 图片/视频压缩
├── pack-epub.csx / .md         # 漫画打包 epub
├── recognize-content.csx / .md # 图片识别书籍目录（ollama）
├── utils/                      # 共享工具库 → utils/CONTEXT.md
├── web/                        # Web 工具 → web/CONTEXT.md
├── project/                    # .csproj（仅用于 IDE 智能提示）
├── images/                     # 文档图片
├── Workspace_Tools.sln         # VS 解决方案
└── omnisharp.json              # OmniSharp 配置
```

## 命令行工具

通过 `dotnet script <脚本名>.csx -- [参数]` 运行。每个脚本入口都定义一个继承 `AsyncCommand<T>` 的 Spectre.Console.Cli 命令类，通过 `#load` 引用 `utils/` 下的共享模块。

| 脚本 | 说明 | 依赖的 utils 模块 |
|:----:|------|:------------------:|
| `calibre-backup.csx` | 将 calibre 书库加密压缩并备份到百度网盘 | 7z, baidu-pan, config-holder, md5, string |
| `compress-media.csx` | 根据分辨率压缩图片和视频 | compress, console, task |
| `pack-epub.csx` | 漫画打包 epub，支持压缩和自动添加到 Calibre | 7z, calibre, compress, config-holder, epub, ffmpeg, file, task |
| `recognize-content.csx` | 通过 ollama 大模型识别图片中的目录并重命名文件 | config-holder |
