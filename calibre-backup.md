# [calibre] 书库备份

将 [calibre] 便携式书库备份到百度网盘，会使用 7z 加密压缩，并为每本书使用不同的随机密码，避免内容被和谐。

网盘上每本书使用一个以电子书 id 命名的目录，备份文件为 `{id}/{groupName}`：书籍较小时只有一个 `all` 文件，较大时会按照文件类型拆分成 `epub`、`meta`、`data` 等分组；历史记录用 `{groupName} {yyyy-MM-dd} {哈希前8位}` 保存在同一个目录中（日期是这份备份的创建日期，哈希是内容寻址：同一份快照永远得到同一个名字、与修改时间无关，同一天的不同快照也不会撞名；如果同一天已经存过完全相同的内容，就直接丢掉当前这份、不重复保存）。这样每本书的首层只会占用一个目录，便于长期管理。

备份信息（含书籍基本信息以及密码）会存储到书库根目录的 `.backup-config.json`。备份信息本身，以及 [calibre] 书库的 `metadata.db`、`metadata_db_prefs_backup.json` 也会被分到网盘的 `config` 文件中。

会根据电子书的最后修改时间以及 MD5 区分是否发生了改变，只重新上传发生修改的电子书，上传后会覆盖旧版本。

判断云盘上的备份是否还在时，靠的是书籍目录的修改时间：每次运行只列一次备份根目录（迁移完成后约 2~3 页），把每本书目录的修改时间和上次记录的值比对，**时间没变的书直接跳过、不再列目录**；时间变了、或者还没记录过的书才单独列一次目录确认。上传、改名、删除、迁移之后会作废并重新记录该时间。

旧版本直接将 `{id}`、`{id}_{group}` 作为备份文件名，会在目录内保留大量文件。备份前会自动迁移这些旧文件：先批量把与目录同名的文件改名（一次请求最多 100 个），再创建同名目录，最后将旧文件移动到目录中。文件名只会变短，内容未发生改变时直接移动旧数据，不会删除后重新上传。

备份过程：

![](/images/calibre-backup-run.png)

备份结果：

```
/书库备份
├── 1665
│   ├── all
│   ├── all 2026-02-27 3f2a1b8c
│   └── all 2026-03-02 91de77a4
├── 1666
│   ├── epub
│   ├── epub 2026-03-01 5c0d9e21
│   ├── meta
│   └── meta 2026-03-01 77b3aa10
├── 1667
│   └── all
└── config
```

迁移前的命名方式，所有备份文件都堆在同一个目录中：

![](/images/calibre-backup-cloud.png)

## 环境要求

- 7z 命令行工具。
- 百度网盘的 AppKey 和 SecretKey，具体步骤参考[创建应用](https://pan.baidu.com/union/doc/fl0hhnulu)。

## 发布 exe 的注意事项

在带「低完整性」标签的目录（例如本工作区）里 `dotnet publish` 出来的 exe 会继承该标签，Windows 会按低完整性启动这个进程，于是它写不了普通（中等完整性）文件，授权时就会报：

```
Error: Access to the path 'D:\Workspace_Tools\utils\baidu-pan\access-token.config.json' is denied.
```

用工作区根目录的 `publish-exe.cmd`（内部转 `publish-exe.ps1`）发布最省事：它默认输出到 `$env:USERPROFILE\bin`，发布完逐个用 `icacls` 检查 exe 的完整性级别，级别不对就打印需要手动执行的命令。

```powershell
.\publish-exe.cmd                 # 发布所有 .cs 到 %USERPROFILE%\bin
.\publish-exe.cmd -SkipPublish    # 只检查已有 exe
.\publish-exe.cmd -Name calibre-backup -OutDir D:\tools
```

直接 `.\publish-exe.ps1` 可能报「未对文件进行数字签名」——本机对未签名脚本的限制比执行策略更严（`Get-ExecutionPolicy` 显示 RemoteSigned 也会被拒，`-ExecutionPolicy Unrestricted` 同样不行，只有 `Bypass` 能跑）。`.cmd` 内部已经带了 `-ExecutionPolicy Bypass`，等价的手动写法是：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish-exe.ps1
```

也可以手动发布到工作区外（新文件的标签随目标目录）：

```powershell
dotnet publish calibre-backup.cs -o "$env:USERPROFILE\bin"
```

已经在工作区里的 exe，可以用管理员终端把标签改回来：

```powershell
icacls "<exe 路径>" /setintegritylevel Medium
```

自查：`icacls <exe 路径> | findstr /i Mandatory`，显示 `Low Mandatory Level` 就是有问题的，没有输出（或 `Medium`）才正常。

注意新文件的标签是 `min(发布进程的完整性, 输出目录的完整性)`：在受限/沙箱终端里发布，即使输出到普通目录，产物也是 Low。

工具启动时会自检进程完整性，检测到低完整性会直接打印警告和修复命令，不用等到写文件失败才发现。

发布完脚本默认会把 exe **复制回当前目录**（方便直接 `.\calibre-backup.exe`），复制后重新检查完整性：

- 覆盖**已存在**的同名 exe → 保留原有标签，原先 Medium 就还是 Medium；
- 当前目录原本**没有**同名 exe（新建）→ 继承工作区的低标签变 Low，脚本会打印 `icacls "<路径>" /setintegritylevel Medium`，在管理员终端执行一次即可；

```powershell
.\publish-exe.cmd                  # 发布 + 复制回当前目录
.\publish-exe.cmd -NoCopyBack      # 只发布到 %USERPROFILE%\bin，不复制回来
.\publish-exe.cmd -SkipPublish     # 不重新编译，只复制/检查
```

## 用法

`dotnet script calibre-backup.csx -- [path] [OPTIONS]`

参数：

- `path`：要备份的 calibre 书库目录，默认为当前目录。

选项：

- `-l, --list`：列出备份文件信息，会将备份路径（书籍目录）、密码、尺寸等输出到书库根目录的 `.backup-data.csv`。
- `-d, --dir`：网盘的备份目标路径。会保存到配置中，只指定一次即可。
- `-h|--history`：保留的备份历史个数，默认为 3。

[calibre]: https://calibre-ebook.com/zh_CN
