# 打包 epub

支持将漫画打包为 epub，并支持将过大的图片压缩到合适大小（原图会备份为 .7z 文件），压缩时使用[压缩图片和视频](./compress-media.md)时的默认配置。

## 用法

`dotnet script pack-epub.csx -- [path] [OPTIONS]`

参数：

- `path`：要处理的文件目录，默认为当前目录。

选项：

- `-s, --silent`：静默打包，不询问作者和标题。
- `-c, --compress`：压缩漫画，减少 epub 尺寸；原图片会压缩为 7z。
- `-a, --auto-add`：自动添加到之前指定的 Calibre 数据库中。
- `--calibre-library`：将 epub 直接添加到指定 calibre 库，支持本地数据库路径，或内容服务器地址。

常见用法：

1. 打包单个漫画

`dotnet script pack-epub.csx -- myComic`

```
myComic/
├── 01.jpg
├── 02.jpg
└── 03.jpg
```

会将目录内的文件作为漫画页打包，并支持以下特殊用法：

- 支持将 `cover*.jpg`、`0.jpg` 或 `0_*.jpg` 识别为封面，并在目录中添加封面作为标题。
  如 `cover.jpg`，`cover_1B.png`，`0.jpg`, `000.jpg`，`0_1B.jpg` 等均为有效名称。
- 支持将文件名空格后的部分识别为目录标题。
  如 `01 目录.jpg` 会将`目录`作为页面的标题。

2. 打包多个漫画

`dotnet script pack-epub.csx -- myComics`

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
