# utils/ — 共享工具库

同一套工具维护了两种形态：

- `.cs`：给 .NET 10 的[基于文件的应用](https://learn.microsoft.com/zh-cn/dotnet/core/sdk/file-based-apps)（`dotnet run xxx.cs`）用，入口通过 `#:include` 引用。每个 `.cs` 文件都会 `#:include` 自己的依赖，所以入口只需要引用直接依赖的模块。
- `.csx`：给 [dotnet-script](https://github.com/dotnet-script/dotnet-script) 脚本（`dotnet script xxx.csx`）用，入口通过 `#load` 引用。

两种形态的成员组织方式不同：`.cs` 的每个文件是独立的编译单元，成员都放在静态类里（如 `SevenZ`、`Compress`、`FileUtils`、`ImageUtils`、`TaskRun`、`VideoUtils`、`CodePages`）；`.csx` 里是顶层函数和顶层静态字段（如 `GetCompressTasksAsync`、`ImageExt`、`RunTaskAsync`）。**改逻辑时两份都要改。**

## 模块列表

| 模块 | 说明 | `.cs` | `.csx` |
|:----:|------|:-----:|:------:|
| `config-holder` | JSON 配置文件持久化，各脚本用于保存/读取本地配置（`ConfigHolder<T>`） | ✔ | ✔ |
| `compress` | 图片和视频压缩核心逻辑（被 `compress-media` 和 `pack-epub` 共用，`.cs` 中为 `Compress.GetTasksAsync`） | ✔ | ✔ |
| `7z` | 7z 命令行封装（`SevenZ.Compress`，加密压缩/分卷） | ✔ | ✔ |
| `calibre` | Calibre 数据库和内容服务器交互 | ✔ | ✔ |
| `video` / `ffmpeg` | [Xabe.FFmpeg](https://github.com/tomaszzmuda/Xabe.FFmpeg) 封装、视频扩展名与 ffmpeg 路径设置（`.cs` 中为 `VideoUtils`） | ✔ | ✔ |
| `image` | ImageMagick 封装（`.cs` 中为 `ImageUtils`） | ✔ | ✔ |
| `task` | 多线程任务执行与取消辅助（`.cs` 中为 `TaskRun.RunAsync`） | ✔ | ✔ |
| `file` | 文件操作工具（封面判断、安全删除、脚本目录，`.cs` 中为 `FileUtils`） | ✔ | ✔ |
| `code-pages` | 字符编码支持（`.cs` 中为 `CodePages.RegisterGBK`） | ✔ | ✔ |
| `console` | 控制台输出辅助 | — | ✔ |
| `http` | HTTP 请求工具 | — | ✔ |
| `md5` | MD5 哈希计算 | — | ✔ |
| `string` | 字符串处理工具 | — | ✔ |

## 子目录

### `epub/` — epub 文件生成

| 文件 | 说明 |
|:----:|------|
| `exporter` | epub 导出器（组装整本书），`.cs` 中为 `EPubExporter` |
| `metadata` | epub 元数据（标题、作者等），`.cs` 中为 `EPubMetadata` |
| `navigation` | epub 导航/目录 |
| `renderer` | epub 页面渲染（HTML 生成） |
| `xml` | XML 工具函数，`.cs` 中为 `EPubXml` |
| `container.xml` | epub 容器模板（静态，运行时按源码路径读取） |
| `main.css` | epub 样式（静态，运行时按源码路径读取） |

上面的代码文件都有 `.cs` 和 `.csx` 两份，`container.xml` 和 `main.css` 共用。

### `baidu-pan/` — 百度网盘 API

入口为 `baidu-pan.csx`（只有 `.csx`），子目录存放具体实现。

| 文件 | 说明 |
|:----:|------|
| `access-token.csx` | OAuth access token 获取与刷新 |
| `defines.csx` | API 常量与数据结构 |
| `error.csx` | API 错误处理 |
| `list-query.csx` | 文件列表查询 |
| `upload-context.csx` | 分块上传 |

### `ffmpeg/` — ffmpeg 二进制及参考

包含 `ffmpeg.exe`、`ffprobe.exe`（Windows 二进制）和 `ffmpeg.md`（常用命令速查）。

### `json/` — JSON 辅助

| 文件 | 说明 |
|:----:|------|
| `bool-converter.csx` | JSON bool 值转换器 |
