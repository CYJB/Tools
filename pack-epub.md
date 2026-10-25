# 打包 epub

支持将漫画打包为 epub，并支持将过大的图片压缩到合适大小（原图会备份为 .7z 文件），压缩时使用[压缩图片和视频](./compress-media.md)时的默认配置。

打包漫画时，目录中的非图片文件（例如视频、说明文本）会保持原样，不会被压缩或添加到 epub 中。如果设置自动添加到 Calibre，它们会作为数据文件添加到相应书籍的数据目录内。

## 用法

`dotnet run pack-epub.cs -- [path] [OPTIONS]`

这是 .NET 10 的[基于文件的应用](https://learn.microsoft.com/zh-cn/dotnet/core/sdk/file-based-apps)，不需要 `.csproj`：文件顶部的 `#:include` 引入 `utils/` 下的共享模块，`#:package` 声明 NuGet 包，包版本由 `Directory.Packages.props` 集中管理。首次运行会还原依赖并编译（几秒钟），之后走构建缓存。

参数：

- `path`：要处理的文件目录，默认为当前目录。

选项：

- `-s, --silent`：静默打包，不询问作者和标题。
- `-c, --compress`：压缩漫画，减少 epub 尺寸；原图片会压缩为 7z。
- `-a, --auto-add`：自动添加到之前指定的 Calibre 数据库中。
- `--calibre-library`：将 epub 直接添加到指定 calibre 库，支持本地数据库路径，或内容服务器地址。

常见用法：

1. 打包单个漫画

```powershell
dotnet run pack-epub.cs -- myComic
```

```
myComic/
├── 01.jpg
├── 02.jpg
└── 03.jpg
```

会将目录内的图片作为漫画页打包，并支持以下特殊用法：

- 支持将 `cover*.jpg`、`0.jpg` 或 `0_*.jpg` 识别为封面，并在目录中添加封面作为标题。
  如 `cover.jpg`，`cover_1B.png`，`0.jpg`, `000.jpg`，`0_1B.jpg` 等均为有效名称。
- 支持将文件名空格后的部分识别为目录标题。
  如 `01 目录.jpg` 会将`目录`作为页面的标题。
- 目录名形如 `[作者] 标题` 时会自动作为 epub 的作者和标题，不用再手工输入。

2. 打包多个漫画

```powershell
dotnet run pack-epub.cs -- myComics
```

```
myComics/
├── myComic1/
|   ├── 01.jpg
|   ├── 02.jpg
|   └── 03.jpg
└── myComic2/
    ├── 01.jpg
    ├── 02.jpg
    └── 03.jpg
```

会将目录内的 `myComic1` 和 `myComic2` 分别打包为 epub。

3. 静默打包（不询问作者和标题，配合 `[作者] 标题` 的目录名使用）

```powershell
dotnet run pack-epub.cs -- "[作者] 标题" --silent
```

## 发布为 exe

需要可执行文件时用 `publish`：

```powershell
dotnet publish pack-epub.cs
```

发布配置已经写在文件顶部，所以不用带任何参数：

```csharp
#:property PublishAot=false                            // 反射式 JSON 序列化需要；AOT 会把它关掉
#:property PublishSingleFile=true                      // 打成单文件
#:property SelfContained=false                         // 依赖本机安装的 .NET 10 运行时
#:property RuntimeIdentifier=win-x64
#:property IncludeNativeLibrariesForSelfExtract=true   // Magick.NET 的原生库也打进单文件，运行时自解压
#:property DebugType=embedded                          // pdb 内嵌，不产生额外的 .pdb
```

默认输出到 `artifacts/pack-epub/pack-epub.exe`，**就一个文件**，约 28 MB。想换输出目录加 `-o <目录>`。

> ⚠️ 发布到当前工作区目录里（不管是谁发布的），exe 会继承该目录的 Windows「低完整性」标签，会有两种表现：
>
> - 启动阶段直接失败：单文件 exe 启动时要把 Magick.NET 的原生库解压到 `%TEMP%\.net\pack-epub\<hash>`，低完整性进程在里面建不了目录，报 `Failed to create directory [...\.net\pack-epub\<hash>] for extracting bundled files. Error code: 5`。
> - 跑起来之后去改名/删除普通目录里的文件会报 `Access to the path is denied`。
>
> 用管理员终端去掉标签即可（`icacls` 里应该显示 `Medium Mandatory Level`）：
>
> ```powershell
> icacls "<exe 路径>" /setintegritylevel Medium
> ```
>
> 或者把输出目录放到仓库外（如 `-o "$env:USERPROFILE\bin"`）。详见 [AGENTS.md](AGENTS.md)。

> ⚠️ epub 的模板（`utils/epub/container.xml`）和样式（`utils/epub/main.css`）是按源码路径读取的，所以发布出来的 exe 仍需要能访问仓库中的 `utils/epub/` 目录，不能只把 exe 拷到别的机器上。
