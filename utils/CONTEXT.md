# utils/ — 共享工具库

各命令行脚本通过 `#load` 指令引用此目录下的共享模块。所有文件均为 C# 脚本（`.csx`）。

## 模块列表

| 模块 | 说明 |
|:----:|------|
| `config-holder.csx` | JSON 配置文件持久化，各脚本用于保存/读取本地配置 |
| `compress.csx` | 图片和视频压缩核心逻辑（被 `compress-media.csx` 和 `pack-epub.csx` 共用） |
| `7z.csx` | 7z 命令行封装（加密压缩/解压） |
| `calibre.csx` | Calibre 数据库和内容服务器交互 |
| `ffmpeg.csx` | ffmpeg/ffprobe 命令行封装 |
| `image.csx` | ImageMagick 命令行封装 |
| `task.csx` | 异步任务执行与取消辅助（`RunAsyncWithCancellation` 等） |
| `console.csx` | 控制台输出辅助 |
| `file.csx` | 文件操作工具 |
| `http.csx` | HTTP 请求工具 |
| `md5.csx` | MD5 哈希计算 |
| `string.csx` | 字符串处理工具 |
| `code-pages.csx` | 字符编码支持 |

## 子目录

### `epub/` — epub 文件生成

| 文件 | 说明 |
|:----:|------|
| `exporter.csx` | epub 导出器（组装整本书） |
| `metadata.csx` | epub 元数据（标题、作者等） |
| `navigation.csx` | epub 导航/目录 |
| `renderer.csx` | epub 页面渲染（HTML 生成） |
| `xml.csx` | XML 工具函数 |
| `container.xml` | epub 容器模板（静态） |
| `main.css` | epub 样式（静态） |

### `baidu-pan/` — 百度网盘 API

入口为 `baidu-pan.csx`，子目录存放具体实现。

| 文件 | 说明 |
|:----:|------|
| `access-token.csx` | OAuth access token 获取与刷新 |
| `defines.csx` | API 常量与数据结构 |
| `error.csx` | API 错误处理 |
| `file-block.csx` | 分块上传 |
| `list-query.csx` | 文件列表查询 |

### `ffmpeg/` — ffmpeg 二进制及参考

包含 `ffmpeg.exe`、`ffprobe.exe`（Windows 二进制）和 `ffmpeg.md`（常用命令速查）。

### `json/` — JSON 辅助

| 文件 | 说明 |
|:----:|------|
| `bool-converter.csx` | JSON bool 值转换器 |
