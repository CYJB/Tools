# 从图片识别书籍目录

支持利用 ollama 调用大模型，识别图片中的目录，并重命名相关文件，将目录标题添加到文件名后。

## 用法

`dotnet script recognize-content.csx -- <path> [extra-prompt]`

参数：

- `path`：要处理的包含目录的图片路径。
- `extra-prompt`：【可选】识别目录时使用的额外提示。

使用 ollama 接口调用大模型，一般来说可以本地部署的 qwen3.5:9b 级别即可完成图片识别工作。

常见用法：

假设目录文件名为 `01.jpg`，识别到的目录为
- 02: foo
- 13: bar

会将 `02.jpg` 重命名为 `02 foo.jpg`，`03.jpg` 重命名为 `03 bar.jpg`。
