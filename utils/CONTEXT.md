# utils/ — 共享工具库

各命令行工具都是 .NET 10 的[基于文件的应用](https://learn.microsoft.com/zh-cn/dotnet/core/sdk/file-based-apps)，通过 `#:include` 引用此目录下的共享模块。

每个 `.cs` 文件都是独立的编译单元，会 `#:include` 自己依赖的模块，所以入口只需要引用直接依赖的模块。成员都放在静态类里（如 `SevenZ`、`Compress`、`FileUtils`、`ImageUtils`、`TaskRun`、`VideoUtils`、`CodePages`、`BaiduPan`）。

## 模块列表

| 模块 | 说明 |
|:----:|------|
| `config-holder` | JSON 配置文件持久化（`ConfigHolder<T>`），默认读写同名 `.config.json` |
| `compress` | 图片和视频压缩核心逻辑（`Compress.GetTasksAsync`，被 `compress-media` 和 `pack-epub` 共用） |
| `7z` | 7z 命令行封装（`SevenZ.Compress`，加密压缩/分卷） |
| `calibre` | Calibre 数据库和内容服务器交互 |
| `baidu-pan` | 百度网盘接入（`BaiduPan`，具体实现在子目录） |
| `video` / `ffmpeg` | [Xabe.FFmpeg](https://github.com/tomaszzmuda/Xabe.FFmpeg) 封装、视频扩展名、ffmpeg/ffprobe 路径（`VideoUtils.FFmpegPath`） |
| `image` | ImageMagick 封装、图片扩展名与 MagickFormat 映射（`ImageUtils`） |
| `task` | 多线程任务执行与取消辅助（`TaskRun.RunAsync`；没有交互式终端时自动退化成逐行输出） |
| `file` | 文件操作工具（封面判断、安全删除、脚本目录、相对路径，`FileUtils`） |
| `console` | 控制台辅助（`ConsoleApp.RunAsyncWithCancellation`，把 Ctrl+C 转换成取消） |
| `http` | HTTP 请求辅助（`HttpUtils.BuildUriQuery` / `GetJsonAsync` / `PostJsonAsync`） |
| `md5` | MD5 计算（`Md5Utils`） |
| `string` | 字符串辅助（`StringUtils.RandomString`） |
| `code-pages` | 字符编码支持（`CodePages.RegisterGBK`） |

## 子目录

### `epub/` — epub 文件生成

| 文件 | 说明 |
|:----:|------|
| `exporter.cs` | epub 导出器（组装整本书），`EPubExporter` |
| `metadata.cs` | epub 元数据（标题、作者等），`EPubMetadata` |
| `navigation.cs` | epub 导航/目录 |
| `renderer.cs` | epub 页面渲染（HTML 生成） |
| `xml.cs` | XML 工具函数，`EPubXml` |
| `container.xml` | epub 容器模板（静态，运行时按源码路径读取） |
| `main.css` | epub 样式（静态，运行时按源码路径读取） |

### `baidu-pan/` — 百度网盘 API

入口为 `baidu-pan.cs`，子目录存放具体实现。

| 文件 | 说明 |
|:----:|------|
| `access-token.cs` | OAuth 设备码授权与 token 刷新（`BaiduPanAuth`） |
| `defines.cs` | API 常量与数据结构（`BaiduPanFileInfo` 等） |
| `error.cs` | errno 到异常的转换（`BaiduPanError`） |
| `list-query.cs` | 文件列表查询参数（`BaiduPanListQuery`） |
| `upload-context.cs` | 分块上传（`UploadContext`） |

### `ffmpeg/` — ffmpeg 二进制及参考

包含 `ffmpeg.exe`、`ffprobe.exe`（Windows 二进制）和 `ffmpeg.md`（常用命令速查）。

### `json/` — JSON 辅助

| 文件 | 说明 |
|:----:|------|
| `bool-converter.cs` | 把 null、字符串、数字都当作 bool 读取的转换器（`BooleanJsonConverter`） |
