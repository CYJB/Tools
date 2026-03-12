# [calibre] 书库备份

将 [calibre] 便携式书库备份到百度网盘，会使用 7z 加密压缩，并为每本书使用不同的随机密码，避免内容被和谐。

使用电子书 id 作为网盘备份文件名，备份信息（含书籍基本信息以及密码）会存储到书库根目录的 `.backup-config.json`。备份信息本身，以及 [calibre] 书库的 `metadata.db`、`metadata_db_prefs_backup.json` 也会被分到网盘的 `config` 文件中。

会根据电子书的最后修改时间以及 MD5 区分是否发生了改变，只重新上传发生修改的电子书，上传后会覆盖旧版本。

备份过程：

![](/images/calibre-backup-run.png)

备份结果：

![](/images/calibre-backup-cloud.png)

## 环境要求

- 7z 命令行工具。
- 百度网盘的 AppKey 和 SecretKey，具体步骤参考[创建应用](https://pan.baidu.com/union/doc/fl0hhnulu)。

## 用法

`dotnet script calibre-backup.csx -- [path] [OPTIONS]`

参数：

- `path`：要备份的 calibre 书库目录，默认为当前目录。

选项：

- `-l, --list`：列出备份文件信息，会将备份路径、密码、尺寸等输出到书库根目录的 `.backup-data.csv`。
- `-d, --dir`：网盘的备份目标路径。会保存到配置中，只指定一次即可。

[calibre]: https://calibre-ebook.com/zh_CN
