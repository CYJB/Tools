#!/usr/bin/env -S dotnet --

/**
 * 备份 calibre 书库。
 */
// 这里只 include 直接依赖的模块，它们各自会 include 自己的依赖。
#:include utils/7z.cs
#:include utils/baidu-pan.cs
#:include utils/string.cs
#:package Spectre.Console.Cli
// 发布成单文件 exe：`dotnet publish calibre-backup.cs` 只产出一个 calibre-backup.exe。
// 采用依赖框架（framework-dependent）的方式，运行时用本机安装的 .NET 10。
#:property PublishAot=false
#:property PublishSingleFile=true
#:property SelfContained=false
#:property RuntimeIdentifier=$(NETCoreSdkRuntimeIdentifier)
#:property DebugType=embedded
#nullable enable

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp();
// `calibre-backup [path]` 仍然是备份，恢复用 `calibre-backup restore [path]`。
app.SetDefaultCommand<BackupCommand>();
app.Configure(config =>
{
	config.SetApplicationName("calibre-backup");
	config.AddCommand<RestoreCommand>("restore").WithDescription("从网盘上的备份恢复书库");
	config.AddCommand<ClearHistoryCommand>("clear-history").WithDescription("删除备份里的历史记录，当前的备份不动");
});
return await app.RunAsync(args);

/// <summary>
/// 备份命令。
/// </summary>
sealed class BackupCommand : AsyncCommand<BackupCommand.Settings>
{
	/// <summary>
	/// 命令的设置。
	/// </summary>
	public sealed class Settings : CommandSettings
	{
		[Description("要备份的 calibre 书库目录，默认为当前目录。")]
		[CommandArgument(0, "[path]")]
		public string? Path { get; init; }

		[Description("列出备份文件信息，会将备份路径、密码、尺寸等输出到书库根目录的 .backup-data.csv。")]
		[CommandOption("-l|--list")]
		[DefaultValue(false)]
		public bool ListData { get; init; }

		[Description("网盘的备份目标路径。会保存到配置中，只指定一次即可。")]
		[CommandOption("-d|--dir")]
		[DefaultValue("")]
		public string? Dir { get; init; }

		[Description("参与派生 config 密码的额外口令。会保存到配置中，只指定一次即可；恢复时必须提供同样的值。")]
		[CommandOption("-k|--key")]
		[DefaultValue("")]
		public string? Key { get; init; }

		[Description("保留的备份历史个数，默认为 3。")]
		[CommandOption("--history")]
		[DefaultValue("")]
		public int? History { get; init; }
	}

	/// <summary>
	/// 执行命令。
	/// </summary>
	protected override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
	{
		// 工作区里发布出来的 exe 会带「低完整性」标签，进程按低完整性启动后写不了书库里的普通文件。
		BackupUtils.WarnLowIntegrity("写入书库");
		var currentDir = settings.Path ?? Directory.GetCurrentDirectory();
		if (!File.Exists(Path.Combine(currentDir, BackupUtils.MetadataDB)))
		{
			AnsiConsole.MarkupLine($"[red]{currentDir.EscapeMarkup()}[/] 不是 calibre 电子书目录");
			return 0;
		}
		ConfigHolder<BackupConfig> configHolder = new(Path.Combine(currentDir, BackupUtils.BackupConfigFile));
		BackupConfig config = configHolder.Config;
		// 统一配置里的路径写法，避免在 Windows / macOS 之间交替备份时把同一批文件当成新文件重新打包。
		bool needSave = BackupUtils.NormalizeConfig(config);
		if (settings.History.HasValue)
		{
			config.History = settings.History.Value;
			needSave = true;
		}
		if (!string.IsNullOrEmpty(settings.Dir))
		{
			config.Dir = BackupUtils.NormalizeDir(settings.Dir);
			needSave = true;
		}
		if (!string.IsNullOrEmpty(settings.Key))
		{
			// 空值改不了已经设过的额外口令，要清除只能直接改配置。
			config.Key = settings.Key;
			needSave = true;
		}
		if (string.IsNullOrEmpty(config.Dir))
		{
			// 不指定备份目录容易误覆盖，这里总是询问。
			config.Dir = BackupUtils.NormalizeDir(AnsiConsole.Ask<string>("请输入网盘的备份目录:"));
			needSave = true;
		}
		if (needSave)
		{
			configHolder.Save();
		}
		// 列出备份文件信息。
		if (settings.ListData)
		{
			ListData(config, Path.Combine(currentDir, BackupUtils.BackupDataFile));
		}
		else
		{
			BackupContext backupContext = new(configHolder, config.Dir!);
			AnsiConsole.MarkupLine($"准备将 [green]{currentDir.EscapeMarkup()}[/] 备份至 [green]{config.Dir.EscapeMarkup()}[/]");
			await backupContext.Backup(currentDir);
		}
		return 0;
	}

	/// <summary>
	/// 列出数据并保存到指定路径。
	/// </summary>
	private static void ListData(BackupConfig config, string filePath)
	{
		var prefix = config.Dir ?? "/";
		var data = new List<string>
		{
			"位置	名称	作者	密码	大小	原始大小"
		};
		foreach (var pair in config.Files)
		{
			// 书籍的备份文件都放在以 id 命名的目录中。
			string path = pair.Key == BackupUtils.ConfigId ? prefix + pair.Key : $"{prefix}{pair.Key}/";
			data.Add(pair.Value.GetDataRow(path));
		}
		if (File.Exists(filePath))
		{
			File.Delete(filePath);
		}
		File.WriteAllLines(filePath, data);
		AnsiConsole.MarkupLine($"已将 [green]{data.Count - 1}[/] 条数据保存至 [green]{filePath.EscapeMarkup()}[/]");
	}

	/// <summary>
	/// 备份的上下文。
	/// </summary>
	private class BackupContext(ConfigHolder<BackupConfig> configHolder, string cloudDir)
	{
		/// <summary>
		/// 备份的临时文件名。
		/// </summary>
		private const string BackupTempFile = ".backup-temp.7z";
		/// <summary>
		/// calibre 的元数据数据库备份。
		/// </summary>
		private const string MetadataBackupDB = "metadata_backup.db";
		/// <summary>
		/// 匹配书籍名的正则表达式。
		/// </summary>
		private static readonly Regex BookNameRegex = new(@"(.+)\s+\((\d+)\)$");
		/// <summary>
		/// 匹配备份历史记录中时间的正则表达式。
		/// </summary>
		private static readonly Regex HistoryDateRegex = new(@"^\d{4}-\d{2}-\d{2}$");
		/// <summary>
		/// 使用多文件上传模式的阈值，10MiB。
		/// </summary>
		private const long MultiFileThreshold = 10 * 1024 * 1024;

		/// <summary>
		/// 备份配置。
		/// </summary>
		private readonly ConfigHolder<BackupConfig> configHolder = configHolder;
		/// <summary>
		/// 网盘的存储路径。
		/// </summary>
		private readonly string cloudDir = cloudDir;
		/// <summary>
		/// 网盘操作。
		/// </summary>
		private readonly BaiduPan cloud = new();
		/// <summary>
		/// 备份的临时文件路径。
		/// </summary>
		private string backupTempPath = "";
		/// <summary>
		/// 网盘目录的文件列表缓存，键为目录名，空字符串表示备份的根目录。
		/// </summary>
		private readonly Dictionary<string, List<BaiduPanFileInfo>> cloudFolders = new();
		/// <summary>
		/// 根目录中旧命名的备份文件，按书籍 id 分组；null 表示还未整理。
		/// </summary>
		private Dictionary<string, List<(BaiduPanFileInfo File, string Name, string NewName)>>? legacyFiles = null;
		/// <summary>
		/// 根目录下按书籍 id 归类的目录信息；null 表示还未整理。
		/// </summary>
		private Dictionary<string, BaiduPanFileInfo>? bookFolders = null;
		/// <summary>
		/// 预扫描得到的作者目录内容，键为作者目录的完整路径。
		/// </summary>
		private readonly ConcurrentDictionary<string, string[]> scannedBookDirs = new(StringComparer.Ordinal);
		/// <summary>
		/// 预扫描得到的书籍文件列表，键为书籍目录的完整路径；取不到时现场枚举。
		/// </summary>
		private readonly ConcurrentDictionary<string, List<FileInfo>> scannedFiles = new(StringComparer.Ordinal);
		/// <summary>
		/// 距离上次写盘后又改动了多少本书。
		/// </summary>
		private int unsavedCount = 0;
		/// <summary>
		/// 上次写盘的时间，用来给写盘限流。
		/// </summary>
		private readonly Stopwatch saveTimer = Stopwatch.StartNew();
		/// <summary>
		/// 每改动这么多本书写一次配置。
		/// </summary>
		private const int SaveInterval = 50;
		/// <summary>
		/// 两次写盘之间最多间隔这么久。
		/// </summary>
		private const long SaveIntervalMilliseconds = 5000;
		/// <summary>
		/// 保护配置的修改与写盘。Ctrl+C 是在另一个线程上处理并直接退出的，可能和主流程同时动配置。
		/// </summary>
		private readonly object configLock = new();

		/// <summary>
		/// 获取备份的临时文件路径。
		/// </summary>
		public string BackupTempPath => backupTempPath;

		/// <summary>
		/// 写盘，调用方必须已经持有 <see cref="configLock"/>。
		/// </summary>
		private void SaveConfigFile()
		{
			unsavedCount = 0;
			saveTimer.Restart();
			configHolder.Save();
		}

		/// <summary>
		/// 记录配置有改动，攒够一批再写盘。
		/// </summary>
		/// <remarks>
		/// 配置里存着每本书每个文件的 MD5，大书库能有几 MB，每本书都序列化一遍会变成 O(书数²)。
		/// 攒批最多丢掉几本书的进度，这些书下次会重新校验，不会影响正确性。
		/// </remarks>
		private void SaveConfig()
		{
			lock (configLock)
			{
				if (++unsavedCount >= SaveInterval || saveTimer.ElapsedMilliseconds >= SaveIntervalMilliseconds)
				{
					SaveConfigFile();
				}
			}
		}

		/// <summary>
		/// 立刻把配置写盘。
		/// </summary>
		private void FlushConfig()
		{
			lock (configLock)
			{
				if (unsavedCount > 0)
				{
					SaveConfigFile();
				}
			}
		}

		/// <summary>
		/// 备份到云盘。
		/// </summary>
		public async Task Backup(string dir)
		{
			backupTempPath = Path.Combine(dir, BackupTempFile);

			// Ctrl+C 默认会直接杀掉进程，来不及把已经改好的进度写盘。
			// 这里先把配置落盘，然后立刻退出，不等正在进行的压缩和上传。
			ConsoleCancelEventHandler onCancelKeyPress = (sender, args) =>
			{
				args.Cancel = true;
				try
				{
					FlushConfig();
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException)
				{
					// 写不出来也只能退出。
				}
				// 退出前把光标恢复显示（进度渲染时可能把它藏了）。
				try
				{
					AnsiConsole.Cursor.Show();
				}
				catch
				{
				}
				Console.Error.WriteLine();
				Console.Error.WriteLine("已中断，配置已保存");
				Console.Out.Flush();
				Console.Error.Flush();
				Environment.Exit(130);
			};
			Console.CancelKeyPress += onCancelKeyPress;
			try
			{
				// 提前把与书籍目录同名的旧文件批量改名，迁移时就能直接创建目录、移动文件。
				await PrepareLegacyRenames();

				bool hasBackup = false;
				var authors = Directory.GetDirectories(dir)
					.Select(path => Path.GetFileName(path))
					.Where(name => !name.StartsWith('.'))
					.ToList();
				// 先把所有书籍目录的文件列表并行读进内存，后面的逐本比对直接复用，省掉机械盘上的随机寻道。
				await ScanBooks(dir, authors);
				for (int i = 0; i < authors.Count; i++)
				{
					var author = authors[i];
					AnsiConsole.WriteLine($"正在扫描 {i + 1}/{authors.Count} {author}");
					if (await BackupAuthor(Path.Combine(dir, author), author))
					{
						hasBackup = true;
					}
				}
				if (hasBackup)
				{
					// 压缩的是磁盘上的 .backup-config.json，先把攒着的改动写掉。
					FlushConfig();
					// 发生了备份操作，备份配置本身。
					await AnsiConsole.Status().StartAsync($"    备份配置", async ctx =>
					{
						// calibre 在运行时会占用 metadata.db，会导致 7z 压缩失败。
						// 总是将 metadata.db 备份为 metadata_backup.db，确保不会压缩失败。
						string metaPath = Path.Combine(dir, BackupUtils.MetadataDB);
						string metaBackupPath = Path.Combine(dir, MetadataBackupDB);
						using (FileStream stream = File.Open(metaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
						{
							using FileStream outStream = File.Create(metaBackupPath);
							await stream.CopyToAsync(outStream);
						}
						// 配置使用固定的 id。
						var item = GetBackupConfigItem(BackupUtils.ConfigId, "", "config & metadata");
						// config 的密码由备份目录（和可选的额外口令）派生，保证在完全没有本地文件的情况下，
						// 只要知道备份目录就能解开 config，从而拿到每本书的密码。
						// 密码写回配置后会改变 .backup-config.json，这次自然会被判定为需要重新上传。
						string configPwd = BackupUtils.GetConfigPassword(cloudDir, configHolder.Config.Key);
						lock (configLock)
						{
							if (item.Pwd != configPwd)
							{
								item.Pwd = configPwd;
								SaveConfigFile();
							}
						}
						BackupTask task = new(BackupUtils.ConfigId, item, dir);
						BackupFileGroup group = new();
						group.Files.Add(new FileInfo(Path.Combine(dir, BackupUtils.BackupConfigFile)));
						group.Files.Add(new FileInfo(metaBackupPath));
						group.Files.Add(new FileInfo(Path.Combine(dir, BackupUtils.MetadataDBPrefsBackup)));
						await task.Backup(this, group, 0, (type, progress) =>
						{
							string message = $"    {type} config";
							if (progress.Length > 0)
							{
								message += " " + progress;
							}
							ctx.Status(message);
						});
						// 上传完毕，保存配置。
						item.Size = task.Size;
						lock (configLock)
						{
							SaveConfigFile();
						}
					});
				}

				// 刷新本次运行中变动过的书籍目录的修改时间（上传/改名/删除/迁移都会改它）。
				await RefreshFolderMtimes();
			}
			finally
			{
				try
				{
					// 中途失败也要把已经改好的部分落盘，免得下次重复上传。
					FlushConfig();
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException)
				{
					AnsiConsole.MarkupLine($"    [red]保存配置失败[/]: {e.Message.EscapeMarkup()}");
				}
				finally
				{
					Console.CancelKeyPress -= onCancelKeyPress;
				}
			}
		}

		/// <summary>
		/// 并行读出所有书籍目录里的文件列表。
		/// </summary>
		/// <param name="dir">书库根目录。</param>
		/// <param name="authors">作者目录名。</param>
		/// <remarks>
		/// <para>
		/// 书库常常放在机械盘上，每本书目录都要一次随机寻道。逐本串行枚举时磁盘队列深度只有 1，
		/// 时间全花在等寻道上；并发读取能让磁盘按物理位置重排请求。同一个约 7000 本、27700 个文件的
		/// 书库实测从 335 ms 降到 59 ms（热缓存），冷缓存下的差距更大。
		/// </para>
		/// <para>
		/// 这里只是把目录项提前读进内存，逐文件比对的逻辑完全不变；读失败的目录不记结果，
		/// 由 <see cref="BackupBook"/> 现场枚举，并按原来的方式报错。
		/// </para>
		/// </remarks>
		private async Task ScanBooks(string dir, List<string> authors)
		{
			// Spectre 的 Status 不是线程安全的，状态文字只在这个（主）线程上刷新。
			int done = 0;
			await AnsiConsole.Status().StartAsync("扫描书籍目录", async ctx =>
			{
				Task work = Task.Run(() => Parallel.ForEach(authors, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) }, author =>
				{
					string authorDir = Path.Combine(dir, author);
					try
					{
						string[] bookDirs = Directory.GetDirectories(authorDir);
						scannedBookDirs[authorDir] = bookDirs;
						foreach (string bookDir in bookDirs)
						{
							if (!BookNameRegex.IsMatch(Path.GetFileName(bookDir)))
							{
								continue;
							}
							try
							{
								scannedFiles[bookDir] = [.. new DirectoryInfo(bookDir).EnumerateFiles("*", SearchOption.AllDirectories)];
							}
							catch (Exception e) when (e is IOException or UnauthorizedAccessException)
							{
								// 不记结果，BackupBook 会现场枚举并按原来的方式报错。
							}
						}
					}
					catch (Exception e) when (e is IOException or UnauthorizedAccessException)
					{
						// 连作者目录都列不出来时也不记结果，BackupAuthor 会再列一次并抛出同样的错误。
					}
					finally
					{
						Interlocked.Increment(ref done);
					}
				}));
				while (!work.IsCompleted)
				{
					ctx.Status($"扫描书籍目录 {Volatile.Read(ref done)}/{authors.Count}");
					await Task.Delay(100);
				}
				await work;
			});
		}

		/// <summary>
		/// 返回作者目录下的书籍目录，优先使用预扫描的结果。
		/// </summary>
		private string[] GetAuthorBookDirs(string authorDir)
		{
			return scannedBookDirs.TryGetValue(authorDir, out string[]? bookDirs) ? bookDirs : Directory.GetDirectories(authorDir);
		}

		/// <summary>
		/// 返回书籍目录里的文件，优先使用预扫描的结果。
		/// </summary>
		private List<FileInfo> GetBookFiles(string dir)
		{
			// 预扫描之后才出现的目录、以及预扫描时读失败的目录，都在这里现场枚举。
			return scannedFiles.TryGetValue(dir, out List<FileInfo>? files)
				? files
				: new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
		}

		/// <summary>
		/// 返回云盘目录中指定名称的文件。
		/// </summary>
		/// <param name="folder">目录名，空字符串表示备份的根目录。</param>
		/// <param name="name">文件名。</param>
		public async Task<BaiduPanFileInfo?> GetCloudFile(string folder, string name)
		{
			var files = await GetFolderFiles(folder);
			return files.Find(file => !file.IsDir && file.Name == name);
		}

		/// <summary>
		/// 判断是否可以相信云端的备份没有被动过。
		/// </summary>
		/// <param name="folder">书籍目录名。</param>
		/// <param name="mtime">上次记录下来的目录修改时间。</param>
		/// <remarks>
		/// 百度网盘的目录 <c>server_mtime</c> 会在目录内容（新增/删除条目）变化时更新，
		/// 所以时间没变就可以认为云端那份备份还在，不必再列目录确认。
		/// 没有记录过时间、目录不存在时都返回 false，走逐目录确认。
		/// </remarks>
		public async Task<bool> CanTrustFolder(string folder, long? mtime)
		{
			if (mtime == null || folder.Length == 0)
			{
				return false;
			}
			var folders = await GetBookFolders();
			return folders.TryGetValue(folder, out var dir) && dir.ServerModifyTime == mtime.Value;
		}

		/// <summary>
		/// 返回根目录缓存中书籍目录的修改时间，目录不存在或时间未知时返回 null。
		/// </summary>
		public async Task<long?> GetFolderMtime(string folder)
		{
			if (folder.Length == 0)
			{
				return null;
			}
			var folders = await GetBookFolders();
			return folders.TryGetValue(folder, out var dir) && dir.ServerModifyTime > 0 ? dir.ServerModifyTime : null;
		}

		/// <summary>
		/// 返回根目录下按书籍 id 归类的目录信息。
		/// </summary>
		private async Task<Dictionary<string, BaiduPanFileInfo>> GetBookFolders()
		{
			if (bookFolders == null)
			{
				bookFolders = new(StringComparer.Ordinal);
				foreach (var file in await GetFolderFiles(""))
				{
					if (file.IsDir)
					{
						bookFolders[file.Name] = file;
					}
				}
			}
			return bookFolders;
		}

		/// <summary>
		/// 刷新本次运行中变动过的书籍目录的修改时间。
		/// </summary>
		/// <remarks>
		/// 上传、改名、删除、迁移都会改变目录的修改时间，代码里会把对应书籍的
		/// <see cref="BackupConfigItem.FolderMtime"/> 置为 null，运行结束时在这里统一补上。
		/// </remarks>
		public async Task RefreshFolderMtimes()
		{
			bool needSave = false;
			try
			{
				bool needReload = false;
				foreach (var pair in configHolder.Config.Files)
				{
					// 配置与元数据没有目录，不需要记录目录修改时间。
					if (pair.Key == BackupUtils.ConfigId || pair.Value.FolderMtime != null)
					{
						continue;
					}
					if (!needReload)
					{
						// 重新列一次根目录，拿到最新的目录修改时间。
						cloudFolders.Remove("");
						bookFolders = null;
						needReload = true;
					}
					var folders = await GetBookFolders();
					if (folders.TryGetValue(pair.Key, out var dir))
					{
						pair.Value.FolderMtime = dir.ServerModifyTime;
						needSave = true;
					}
				}
			}
			catch (Exception e)
			{
				// 刷新失败只会让下次运行多做一次校验，不影响本次备份结果。
				AnsiConsole.MarkupLine($"    [yellow]刷新目录修改时间失败[/]: {e.Message.EscapeMarkup()}");
			}
			if (needSave)
			{
				lock (configLock)
				{
					SaveConfigFile();
				}
			}
		}

		/// <summary>
		/// 返回云盘目录中指定名称的历史文件，按时间从早到晚排序。
		/// </summary>
		/// <param name="folder">目录名，空字符串表示备份的根目录。</param>
		/// <param name="name">历史记录对应的文件名。</param>
		public async Task<BaiduPanFileInfo[]> GetCloudFileHistory(string folder, string name)
		{
			string prefix = name + " ";
			var files = await GetFolderFiles(folder);
			var historyFiles = files.Where(file => !file.IsDir && file.Name.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
			// 文件名由 `文件名 + 空格 + yyyy-MM-dd` 组成，按名称排序即按时间排序。
			Array.Sort(historyFiles, (x, y) => string.CompareOrdinal(x.Name, y.Name));
			return historyFiles;
		}

		/// <summary>
		/// 上传临时文件。
		/// </summary>
		/// <param name="folder">目录名，空字符串表示备份的根目录。</param>
		/// <param name="name">文件名。</param>
		/// <param name="history">是否将同名的旧文件保留为历史记录。</param>
		/// <param name="progressCallback">上传进度回调。</param>
		public async Task Upload(string folder, string name, bool history, Action<float>? progressCallback = null)
		{
			await EnsureFolder(folder);
			var files = await GetFolderFiles(folder);
			var oldFile = files.Find(file => !file.IsDir && file.Name == name);
			if (oldFile != null)
			{
				if (history)
				{
					// 旧格式的历史记录（只有日期、没有哈希）先补上哈希，保证目录里的命名格式一致。
					await NormalizeHistoryNames(folder, files, name);
					// 将同名文件改名为历史备份。重名的目标会让网盘改名失败，所以要挑一个没被占用的名字。
					DateTimeOffset dateTimeOffset = DateTimeOffset.FromUnixTimeSeconds(oldFile.ServerCreatetime);
					string? historyName = GetHistoryFileName(files, name, oldFile, dateTimeOffset);
					if (historyName == null)
					{
						// 目录里已经有一份内容相同的历史记录，当前这份就不用再留了。
						await cloud.Delete(GetFilePath(folder, name));
					}
					else
					{
						await cloud.Rename((GetFilePath(folder, name), historyName));
					}
				}
				else
				{
					// 删除同名文件。
					await cloud.Delete(GetFilePath(folder, name));
				}
				// 文件列表已经发生了变化。
				cloudFolders.Remove(folder);
			}
			// 再上传。
			try
			{
				await cloud.Upload(GetFilePath(folder, name), backupTempPath, progressCallback);
			}
			catch (Exception ex)
			{
				throw new Exception(Markup.Escape(ex.Message));
			}
			cloudFolders.Remove(folder);
		}

		/// <summary>
		/// 把旧格式（只有日期、没有哈希）的历史记录改名成新格式。
		/// </summary>
		/// <param name="folder">目录名，空字符串表示备份的根目录。</param>
		/// <param name="files">目录中已有的文件，改完后会同步更新。</param>
		/// <param name="name">备份文件名。</param>
		/// <remarks>
		/// 只在即将写入新的历史记录时顺带整理，所以没改动过的书籍会等到下次变化时才补齐。
		/// </remarks>
		private async Task NormalizeHistoryNames(string folder, List<BaiduPanFileInfo> files, string name)
		{
			foreach (var (oldName, newName) in GetHistoryRenames(files, name))
			{
				var file = files.Find(item => item.Name == oldName);
				if (file == null)
				{
					continue;
				}
				await cloud.Rename((GetFilePath(folder, oldName), newName));
				files.Remove(file);
				files.Add(MoveFileInfo(file, GetFilePath(folder, newName), newName));
			}
		}

		/// <summary>
		/// 返回旧格式历史记录（只有日期、没有哈希）需要改成的新名字。
		/// </summary>
		/// <param name="files">目录中已有的文件。</param>
		/// <param name="name">备份文件名。</param>
		private static List<(string OldName, string NewName)> GetHistoryRenames(List<BaiduPanFileInfo> files, string name)
		{
			string prefix = name + " ";
			List<(string OldName, string NewName)> renames = [];
			foreach (var file in files)
			{
				if (file.IsDir || !file.Name.StartsWith(prefix, StringComparison.Ordinal))
				{
					continue;
				}
				// 只有「文件名 + 空格 + yyyy-MM-dd」这 10 位日期才是旧格式。
				string rest = file.Name[prefix.Length..];
				if (rest.Length != 10 || !HistoryDateRegex.IsMatch(rest))
				{
					continue;
				}
				string? shortHash = GetShortHash(file);
				if (shortHash == null)
				{
					continue;
				}
				string newName = $"{file.Name} {shortHash}";
				if (files.Exists(item => item.Name == newName))
				{
					continue;
				}
				renames.Add((file.Name, newName));
			}
			return renames;
		}

		/// <summary>
		/// 返回内容哈希的前 8 位，用于历史记录的文件名。
		/// </summary>
		private static string? GetShortHash(BaiduPanFileInfo file)
		{
			string? hash = file.ServerHash;
			return string.IsNullOrEmpty(hash) ? null : hash[..Math.Min(8, hash.Length)].ToLowerInvariant();
		}

		/// <summary>
		/// 返回历史记录的文件名。
		/// </summary>
		/// <param name="files">目录中已有的文件。</param>
		/// <param name="name">备份文件名。</param>
		/// <param name="file">要改名为历史记录的文件。</param>
		/// <param name="dateTime">这份备份的创建时间。</param>
		/// <returns>历史记录文件名；目录里已经有内容相同的记录时返回 <c>null</c>。</returns>
		/// <remarks>
		/// <para>格式是「文件名 + 空格 + yyyy-MM-dd + 空格 + 内容哈希前 8 位」，日期便于辨认，哈希用于区分同一天的不同快照。</para>
		/// <para>
		/// 用哈希而不是时分秒，是因为它做内容寻址：同一份快照永远得到同一个名字，与修改时间无关
		/// （迁移移动文件会顺手改掉创建时间），而且同一份内容不会重复保存。
		/// </para>
		/// <para>网盘的改名接口遇到重名的目标会失败（errno -8，带 ondup=overwrite 也不会覆盖），所以要先挑空名字。</para>
		/// </remarks>
		private static string? GetHistoryFileName(List<BaiduPanFileInfo> files, string name, BaiduPanFileInfo file, DateTimeOffset dateTime)
		{
			string date = dateTime.ToString(" yyyy-MM-dd");
			string? hash = file.ServerHash;
			string? shortHash = GetShortHash(file);
			string candidate = shortHash == null ? name + date : $"{name}{date} {shortHash}";
			var existing = files.Find(item => item.Name == candidate);
			if (existing == null)
			{
				return candidate;
			}
			// 同一天、同样内容的历史记录已经存在，不必再存一份。
			if (!string.IsNullOrEmpty(hash) && string.Equals(existing.ServerHash, hash, StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			// 哈希前缀撞车（或没有哈希）这种极少见的情况，才退化成带时分秒和序号的名字。
			candidate = name + dateTime.ToString(" yyyy-MM-dd HH-mm-ss");
			for (int i = 2; files.Exists(item => item.Name == candidate); i++)
			{
				candidate = $"{name} {dateTime:yyyy-MM-dd HH-mm-ss}-{i}";
			}
			return candidate;
		}

		/// <summary>
		/// 移除指定文件。
		/// </summary>
		/// <param name="folder">目录名，空字符串表示备份的根目录。</param>
		/// <param name="name">文件名。</param>
		public async Task Delete(string folder, string name)
		{
			await cloud.Delete(GetFilePath(folder, name));
			cloudFolders.Remove(folder);
		}

		/// <summary>
		/// 批量把与书籍目录同名的旧文件改名，为创建同名目录让路。
		/// </summary>
		/// <remarks>
		/// 改名是迁移里唯一能跨书批量处理的写操作（一次请求最多 100 个），提前一次性发完，
		/// 迁移时每本书就只需要「建目录 + 移动文件」。改名失败的书不更新计划，
		/// 迁移时会退回逐本单独改名。
		/// </remarks>
		private async Task PrepareLegacyRenames()
		{
			var legacyFiles = await GetLegacyFiles();
			if (legacyFiles.Count == 0)
			{
				return;
			}
			var rootFiles = await GetFolderFiles("");
			var folders = await GetBookFolders();
			List<(string Id, int Index, string Path, string NewName)> renames = [];
			foreach (var pair in legacyFiles)
			{
				// 目录已存在，或者没有与目录同名的文件时，都不需要改名。
				if (folders.ContainsKey(pair.Key))
				{
					continue;
				}
				var moves = pair.Value;
				int index = moves.FindIndex(move => move.Name == pair.Key);
				if (index < 0)
				{
					continue;
				}
				// 临时名字按书籍 id 生成，不同书籍之间不会冲突。
				string tempName = GetFreeOldName(rootFiles, pair.Key);
				renames.Add((pair.Key, index, GetFilePath("", pair.Key), tempName));
			}
			if (renames.Count == 0)
			{
				return;
			}
			int renamedCount = 0;
			// 百度网盘单次最多约 50 个文件（超过会整批不生效、外层报 errno 12），按 50 分批。
			foreach (var chunk in renames.Chunk(50))
			{
				try
				{
					await cloud.Rename(chunk.Select(rename => (rename.Path, rename.NewName)).ToArray());
				}
				catch (Exception e)
				{
					// 保持原计划，迁移时会为这些书逐本重试改名。
					AnsiConsole.MarkupLine($"    [yellow]批量重命名失败[/]: {e.Message.EscapeMarkup()}");
					continue;
				}
				foreach (var (id, index, _, newName) in chunk)
				{
					var moves = legacyFiles[id];
					var info = MoveFileInfo(moves[index].File, GetFilePath("", newName), newName);
					moves[index] = (info, newName, moves[index].NewName);
					rootFiles.RemoveAll(file => file.Name == id);
					rootFiles.Add(info);
					renamedCount++;
				}
			}
			AnsiConsole.WriteLine($"    已批量重命名 {renamedCount}/{renames.Count} 个旧备份文件");
		}

		/// <summary>
		/// 将书籍的旧备份文件迁移到新的命名规则。
		/// </summary>
		/// <remarks>
		/// <para>旧命名规则为 <c>{id}</c> 或 <c>{id}_{group}</c>，新命名规则为 <c>{id}/{group}</c>。</para>
		/// <para>
		/// 目录与书籍 id 同名，因此需要先将同名文件改名，再创建目录，最后将旧文件移动到目录中。
		/// 文件名本身只会缩短，旧数据可以直接移动复用，不需要删除后重新上传。
		/// </para>
		/// </remarks>
		/// <param name="id">书籍的 id。</param>
		/// <returns>是否修改了备份配置。</returns>
		private async Task<bool> MigrateOldFiles(string id)
		{
			// 未分组的书籍现在固定使用 all 分组，内容未改变时可以直接复用旧的备份文件。
			bool configChanged = UpdateUngroupedFiles(id);
			var legacyFiles = await GetLegacyFiles();
			if (!legacyFiles.TryGetValue(id, out var moves) || moves.Count == 0)
			{
				return configChanged;
			}
			var rootFiles = await GetFolderFiles("");
			var folders = await GetBookFolders();
			bool folderExists = folders.ContainsKey(id);
			// 最新的文件优先迁移，避免残留的临时文件顶替当前的备份。
			moves.Sort((x, y) => y.File.ServerModifyTime.CompareTo(x.File.ServerModifyTime));
			if (!folderExists)
			{
				for (int i = 0; i < moves.Count; i++)
				{
					if (moves[i].Name != id)
					{
						continue;
					}
					// 与目录同名的文件必须先改名，否则无法创建同名目录。
					string tempName = GetFreeOldName(rootFiles, id);
					await cloud.Rename((GetFilePath("", id), tempName));
					rootFiles.RemoveAll(file => file.Name == id);
					moves[i] = (moves[i].File, tempName, moves[i].NewName);
					break;
				}
				string path = GetFolderPath(id);
				await cloud.CreateDir(path);
				var folderInfo = CreateFolderInfo(id, path);
				rootFiles.Add(folderInfo);
				folders[id] = folderInfo;
			}
			// 刚创建的目录里肯定没有文件，不必再为它列一次目录；已存在的目录要用真实列表做冲突检查。
			List<BaiduPanFileInfo> folderFiles = folderExists ? await GetFolderFiles(id) : [];
			HashSet<string> names = new(folderFiles.Select(file => file.Name), StringComparer.Ordinal);
			List<(BaiduPanFileInfo File, string Name, string NewName)> moveFiles = [];
			foreach (var (file, name, newName) in moves)
			{
				if (!names.Add(newName))
				{
					AnsiConsole.MarkupLine($"    [yellow]跳过迁移 {name.EscapeMarkup()}：{id.EscapeMarkup()}/{newName.EscapeMarkup()} 已存在[/]");
					continue;
				}
				moveFiles.Add((file, name, newName));
			}
			if (moveFiles.Count == 0)
			{
				return configChanged;
			}
			await cloud.Move(GetFolderPath(id), moveFiles.Select(file => (GetFilePath("", file.Name), file.NewName)).ToArray());
			foreach (var (file, name, newName) in moveFiles)
			{
				rootFiles.RemoveAll(rootFile => rootFile.Name == name);
				// 同步更新目录缓存，迁移后就不用再为这本书列一次目录。
				folderFiles.Add(MoveFileInfo(file, GetFilePath(id, newName), newName));
			}
			cloudFolders[id] = folderFiles;
			// 目录内容变了，记录的修改时间作废，运行结束时重新记录。
			if (configHolder.Config.Files.TryGetValue(id, out var item))
			{
				item.FolderMtime = null;
			}
			AnsiConsole.MarkupLine($"    已迁移 {moveFiles.Count} 个备份文件到 {id.EscapeMarkup()}/");
			return configChanged;
		}

		/// <summary>
		/// 返回根目录中旧命名的备份文件，按书籍 id 分组。
		/// </summary>
		/// <remarks>
		/// 根目录内的旧备份文件可能很多（每本书每个分组，加上历史记录），
		/// 这里一次性按 id 归类，避免为每本书重复扫描整个根目录。
		/// </remarks>
		private async Task<Dictionary<string, List<(BaiduPanFileInfo File, string Name, string NewName)>>> GetLegacyFiles()
		{
			if (legacyFiles == null)
			{
				legacyFiles = new(StringComparer.Ordinal);
				foreach (var file in await GetFolderFiles(""))
				{
					if (!file.IsDir
						&& TryGetOldFileId(file.Name, out string id)
						&& TryGetNewFileName(file.Name, id, out string newName))
					{
						// 历史记录统一带上内容哈希，和新建的历史记录格式保持一致。
						newName = AddHistoryHash(newName, file);
						if (!legacyFiles.TryGetValue(id, out var moves))
						{
							moves = [];
							legacyFiles.Add(id, moves);
						}
						moves.Add((file, file.Name, newName));
					}
				}
			}
			return legacyFiles;
		}

		/// <summary>
		/// 给历史记录的文件名追加内容哈希前 8 位。
		/// </summary>
		/// <param name="name">转换后的文件名。</param>
		/// <param name="file">对应的旧文件。</param>
		/// <remarks>
		/// 只有「文件名 + 空格 + yyyy-MM-dd」这种历史记录名才需要追加，普通的分组文件保持不变。
		/// </remarks>
		private static string AddHistoryHash(string name, BaiduPanFileInfo file)
		{
			int index = name.LastIndexOf(' ');
			if (index <= 0 || !HistoryDateRegex.IsMatch(name[(index + 1)..]))
			{
				return name;
			}
			string? shortHash = GetShortHash(file);
			return shortHash == null ? name : $"{name} {shortHash}";
		}

		/// <summary>
		/// 尝试从旧的文件名中取出书籍 id（文件名开头的数字部分）。
		/// </summary>
		private static bool TryGetOldFileId(string name, out string id)
		{
			int index = 0;
			while (index < name.Length && char.IsAsciiDigit(name[index]))
			{
				index++;
			}
			id = name[..index];
			return index > 0;
		}

		/// <summary>
		/// 将未分组的旧备份文件归入 <see cref="BackupUtils.AllGroupName"/> 分组。
		/// </summary>
		/// <returns>是否修改了备份配置。</returns>
		private bool UpdateUngroupedFiles(string id)
		{
			bool changed = false;
			if (configHolder.Config.Files.TryGetValue(id, out var item))
			{
				foreach (var info in item.Files.Values)
				{
					if (info.Group == null)
					{
						info.Group = BackupUtils.AllGroupName;
						changed = true;
					}
				}
			}
			return changed;
		}

		/// <summary>
		/// 返回云盘目录中的文件列表。
		/// </summary>
		/// <param name="folder">目录名，空字符串表示备份的根目录。</param>
		private async Task<List<BaiduPanFileInfo>> GetFolderFiles(string folder)
		{
			if (!cloudFolders.TryGetValue(folder, out var files))
			{
				files = await cloud.List(GetFolderPath(folder));
				cloudFolders.Add(folder, files);
			}
			return files;
		}

		/// <summary>
		/// 确保云盘目录存在。
		/// </summary>
		/// <param name="folder">目录名，空字符串表示备份的根目录。</param>
		private async Task EnsureFolder(string folder)
		{
			if (folder.Length == 0)
			{
				return;
			}
			var folders = await GetBookFolders();
			if (folders.ContainsKey(folder))
			{
				return;
			}
			string path = GetFolderPath(folder);
			try
			{
				await cloud.CreateDir(path);
			}
			catch (Exception)
			{
				// 目录可能已经存在，也可能父目录还不存在；由接下来的上传决定是否真的失败。
			}
			// 两个视图都补上，避免后续判断时又列一次目录。
			var info = CreateFolderInfo(folder, path);
			folders[folder] = info;
			var rootFiles = await GetFolderFiles("");
			if (!rootFiles.Exists(file => file.IsDir && file.Name == folder))
			{
				rootFiles.Add(info);
			}
			cloudFolders.Remove(folder);
		}

		/// <summary>
		/// 返回云盘目录的路径。
		/// </summary>
		/// <param name="folder">目录名，空字符串表示备份的根目录。</param>
		private string GetFolderPath(string folder)
		{
			return folder.Length == 0 ? cloudDir : $"{cloudDir}{folder}/";
		}

		/// <summary>
		/// 返回云盘文件的路径。
		/// </summary>
		/// <param name="folder">目录名，空字符串表示备份的根目录。</param>
		/// <param name="name">文件名。</param>
		private string GetFilePath(string folder, string name)
		{
			return GetFolderPath(folder) + name;
		}

		/// <summary>
		/// 返回目录的云盘文件信息。
		/// </summary>
		private static BaiduPanFileInfo CreateFolderInfo(string name, string path)
		{
			return new BaiduPanFileInfo
			{
				Name = name,
				Path = path,
				Size = 0,
				ServerModifyTime = 0,
				ServerCreatetime = 0,
				LocalModifytime = 0,
				LocalCreateTime = 0,
				IsDir = true,
			};
		}

		/// <summary>
		/// 返回移动后的云盘文件信息。
		/// </summary>
		private static BaiduPanFileInfo MoveFileInfo(BaiduPanFileInfo file, string path, string name)
		{
			return new BaiduPanFileInfo
			{
				Name = name,
				Path = path,
				Size = file.Size,
				ServerModifyTime = file.ServerModifyTime,
				ServerCreatetime = file.ServerCreatetime,
				LocalModifytime = file.LocalModifytime,
				LocalCreateTime = file.LocalCreateTime,
				Category = file.Category,
				ServerHash = file.ServerHash,
			};
		}

		/// <summary>
		/// 尝试将旧的备份文件名转换为新的备份文件名。
		/// </summary>
		/// <remarks>
		/// 旧命名规则为 <c>{id}</c>、<c>{id}_{group}</c>，历史记录在末尾追加 <c> yyyy-MM-dd</c>。
		/// 新命名规则为 <c>{group}</c>、<c>{group} yyyy-MM-dd</c>，都保存在以 id 命名的目录中。
		/// </remarks>
		private static bool TryGetNewFileName(string name, string id, out string newName)
		{
			newName = "";
			if (name.StartsWith(id + BackupUtils.OldNameSuffix, StringComparison.Ordinal))
			{
				// 上次迁移时改过名，但还没来得及移动的文件。
				newName = BackupUtils.AllGroupName;
				return true;
			}
			if (name == id)
			{
				newName = BackupUtils.AllGroupName;
				return true;
			}
			string rest;
			if (name.StartsWith(id + " ", StringComparison.Ordinal))
			{
				// 未分组文件的历史记录。
				rest = name[(id.Length + 1)..];
				if (!HistoryDateRegex.IsMatch(rest))
				{
					return false;
				}
				newName = $"{BackupUtils.AllGroupName} {rest}";
				return true;
			}
			if (!name.StartsWith(id + "_", StringComparison.Ordinal))
			{
				return false;
			}
			rest = name[(id.Length + 1)..];
			string suffix = "";
			int index = rest.LastIndexOf(' ');
			if (index > 0 && HistoryDateRegex.IsMatch(rest[(index + 1)..]))
			{
				suffix = rest[index..];
				rest = rest[..index];
			}
			if (rest.Length == 0)
			{
				return false;
			}
			newName = rest + suffix;
			return true;
		}

		/// <summary>
		/// 返回不与现有文件冲突的迁移临时文件名。
		/// </summary>
		private static string GetFreeOldName(List<BaiduPanFileInfo> files, string id)
		{
			string name = id + BackupUtils.OldNameSuffix;
			for (int i = 2; files.Exists(file => file.Name == name); i++)
			{
				name = id + BackupUtils.OldNameSuffix + i;
			}
			return name;
		}

		/// <summary>
		/// 备份指定作者的目录。
		/// </summary>
		private async Task<bool> BackupAuthor(string dir, string author)
		{
			bool isBackup = false;
			var bookDirs = GetAuthorBookDirs(dir);
			foreach (var bookDir in bookDirs)
			{
				var match = BookNameRegex.Match(Path.GetFileName(bookDir));
				if (match.Success)
				{
					var name = match.Groups[1].Value;
					var id = match.Groups[2].Value;
					isBackup |= await BackupBook(id, author, name, bookDir);
				}
				else
				{
					string name = $"{author}/{Path.GetFileName(bookDir)}";
					AnsiConsole.MarkupLine($"[red]未识别的文件名：{Markup.Escape(name)}[/]");
				}
			}
			return isBackup;
		}

		/// <summary>
		/// 备份指定的书籍。
		/// </summary>
		private async Task<bool> BackupBook(string id, string author, string name, string dir)
		{
			string escapedName = Markup.Escape($"[{id}] {name}");
			var history = configHolder.Config.History.GetValueOrDefault(3);
			try
			{
				var isBackup = await AnsiConsole.Status().StartAsync($"    扫描 {escapedName}", async ctx =>
				{
					// 先迁移旧的备份文件命名，这样内容未改变时可以直接复用已有的备份。
					bool migrated = await MigrateOldFiles(id);
					var item = GetBackupConfigItem(id, author, name);
					// 文件列表来自预扫描：一次枚举同时拿到路径、大小和修改时间，判断是否变化时复用这些信息。
					var files = GetBookFiles(dir);
					BackupTask task = new(id, item, dir);
					List<BackupFileGroup> groups;
					// 计算目录内所有文件的大小。
					long totalSize = 0;
					foreach (FileInfo fileInfo in files)
					{
						totalSize += fileInfo.Length;
					}
					if (totalSize >= MultiFileThreshold)
					{
						// 按照文件类型分组上传。
						groups = GroupFiles(files, dir);
					}
					else
					{
						// 不分组，直接整体上传。
						BackupFileGroup group = new(BackupUtils.AllGroupName);
						group.Files.AddRange(files);
						groups = new() { group };
					}
					bool isBackup = false;
					foreach (var group in groups)
					{
						isBackup |= await task.Backup(this, group, history, (type, progress) =>
						{
							string message = $"    {type} {escapedName}";
							if (progress.Length > 0)
							{
								message += " " + progress;
							}
							ctx.Status(message);
						});
					}
					// 上传完毕，保存配置。
					if (isBackup || task.NeedSave || migrated)
					{
						item.Files = task.Files;
						item.Size = task.Size;
						SaveConfig();
						if (isBackup)
						{
							// 清理过期文件。
							await task.ClearInvalidFiles(this);
						}
					}
					return isBackup;
				});
				if (isBackup)
				{
					AnsiConsole.MarkupLine($"    已备份 {escapedName}");
					return true;
				}
			}
			catch (Exception e)
			{
				// 备份失败。
				AnsiConsole.MarkupLine($"    [red]备份 {escapedName} 失败[/]: {e.Message.EscapeMarkup()}");
				AnsiConsole.WriteException(e, ExceptionFormats.ShortenPaths);
				// 移除临时文件。
				if (File.Exists(backupTempPath))
				{
					File.Delete(backupTempPath);
				}
			}
			return false;
		}

		/// <summary>
		/// 分组指定的文件。
		/// </summary>
		/// <param name="files">书籍目录内的文件。</param>
		/// <param name="dir">书籍目录。</param>
		/// <remarks>
		/// <list type="bullet">
		/// <item>metadata.opf 和 cover.jpg 作为元数据 meta</item>
		/// <item>data 作为 data</item>
		/// <item>其它分组为对应的扩展名，没有扩展名的文件作为 other</item>
		/// </list>
		/// 判断分组用的一律是归一化后的相对路径（`/` 分隔），这样在不同平台上备份得到的文件名不会变。
		/// </remarks>
		private static List<BackupFileGroup> GroupFiles(ICollection<FileInfo> files, string dir)
		{
			// 按照文件类型分组。
			List<BackupFileGroup> groups = new();
			foreach (FileInfo file in files)
			{
				var fileName = BackupUtils.GetRelativeName(dir, file.FullName);
				string groupName;
				if (fileName == "metadata.opf" || fileName == "cover.jpg")
				{
					groupName = "meta";
				}
				else if (fileName == "data" || fileName.StartsWith("data/", StringComparison.Ordinal))
				{
					groupName = "data";
				}
				else
				{
					// 移除后缀的 .，没有后缀的文件统一归入 other，避免出现空的分组名。
					string extension = Path.GetExtension(fileName);
					groupName = extension.Length > 1 ? extension[1..].ToLowerInvariant() : "other";
				}
				var group = groups.Find((group) => group.Name == groupName);
				if (group == null)
				{
					group = new BackupFileGroup(groupName);
					groups.Add(group);
				}
				group.Files.Add(file);
			}
			return groups;
		}

		/// <summary>
		/// 返回指定的备份配置项。
		/// </summary>
		private BackupConfigItem GetBackupConfigItem(string id, string author, string name)
		{
			// 新增配置项会改变字典结构，Ctrl+C 的处理线程可能正在序列化它，所以要和写盘共用同一把锁。
			lock (configLock)
			{
				var files = configHolder.Config.Files;
				if (!files.TryGetValue(id, out var item))
				{
					item = new BackupConfigItem
					{
						Author = author,
						Name = name,
						Pwd = StringUtils.RandomString(7, 10, BackupUtils.PwdChars),
						Files = new(),
					};
					files.Add(id, item);
				}
				return item;
			}
		}
	}

	/// <summary>
	/// 备份文件的分组。
	/// </summary>
	private class BackupFileGroup(string? name = null)
	{
		/// <summary>
		/// 分组的名称。
		/// </summary>
		public string? Name { get; init; } = name;
		/// <summary>
		/// 分组的文件列表。用枚举出来的 FileInfo，大小和修改时间已经带在里面，不用再取一次属性。
		/// </summary>
		public List<FileInfo> Files { get; init; } = new();
	}

	/// <summary>
	/// 备份任务。
	/// </summary>
	private class BackupTask(string id, BackupConfigItem item, string dir)
	{
		/// <summary>
		/// 当前备份 ID。
		/// </summary>
		private readonly string id = id;
		/// <summary>
		/// 备份的加密密码。
		/// </summary>
		private readonly string password = item.Pwd;
		/// <summary>
		/// 之前的文件列表。
		/// </summary>
		/// 需要复制一份，避免在检查文件时误将旧配置移除。
		private readonly Dictionary<string, BackupFileInfo> prevFiles = new(item.Files);
		/// <summary>
		/// 最新的文件列表。
		/// </summary>
		private readonly Dictionary<string, BackupFileInfo> files = [];
		/// <summary>
		/// 待备份的目录。
		/// </summary>
		private readonly string dir = dir;
		/// <summary>
		/// 是否需要保存配置。
		/// </summary>
		private bool needSave = false;
		/// <summary>
		/// 压缩后的文件大小。
		/// </summary>
		private long size = 0;

		/// <summary>
		/// 获取是否需要保存配置。
		/// </summary>
		public bool NeedSave => needSave;
		/// <summary>
		/// 获取压缩后的文件大小。
		/// </summary>
		public long Size => size;
		/// <summary>
		/// 获取最新的文件列表。
		/// </summary>
		public Dictionary<string, BackupFileInfo> Files => files;

		/// <summary>
		/// 是否为配置与元数据的备份。
		/// </summary>
		private bool IsConfig => id == BackupUtils.ConfigId;
		/// <summary>
		/// 云盘中的目录名，配置与元数据直接放在根目录中。
		/// </summary>
		private string CloudFolder => IsConfig ? "" : id;

		/// <summary>
		/// 备份指定的分组。
		/// </summary>
		public async Task<bool> Backup(BackupContext context, BackupFileGroup group, int history, Action<string, string>? progressCallback = null)
		{
			string fileName = GetFileName(group.Name);
			// 先检查 group 中的文件是否发生改变，确保能够保存新的文件列表。
			bool changed = IsGroupFilesChanged(group);
			if (!changed)
			{
				// 本地没变：书籍目录的修改时间也没变，就可以认为云端那份备份还在，不必再列目录。
				if (await context.CanTrustFolder(CloudFolder, item.FolderMtime))
				{
					return false;
				}
				// 否则要确认云盘文件是否还在，找不到就重新上传。
				var existFile = await context.GetCloudFile(CloudFolder, fileName);
				if (existFile != null)
				{
					size += existFile.Size;
					// 只有原本记录过时间的才更新；本次运行写过的目录保持 null，结束时统一刷新。
					if (item.FolderMtime != null)
					{
						item.FolderMtime = await context.GetFolderMtime(CloudFolder);
					}
					return false;
				}
			}
			var groupName = group.Name;
			progressCallback?.Invoke($"压缩 {groupName}", "");
			// 移除旧临时文件。
			var backupTempPath = context.BackupTempPath;
			if (File.Exists(backupTempPath))
			{
				File.Delete(backupTempPath);
			}
			SevenZConfig config = new()
			{
				FileName = backupTempPath,
				WorkingDirectory = dir,
				Password = password,
				Files = group.Files.Select(file => file.FullName).ToArray(),
				EncrypteFileName = true,
			};
			await SevenZ.Compress(config, (progress) =>
			{
				progressCallback?.Invoke($"压缩 {groupName}", progress);
			});
			size += new FileInfo(backupTempPath).Length;
			progressCallback?.Invoke($"上传 {groupName}", "");
			// 检查备份历史。
			if (history > 0)
			{
				var historyFiles = await context.GetCloudFileHistory(CloudFolder, fileName);
				int deleteCount = historyFiles.Length - history + 1;
				if (deleteCount > 0)
				{
					// 移除最早的历史记录。
					for (int i = 0; i < deleteCount; i++)
					{
						await context.Delete(CloudFolder, historyFiles[i].Name);
					}
				}
			}
			// 再上传。
			await context.Upload(CloudFolder, fileName, history > 0, (progress) =>
			{
				progressCallback?.Invoke($"上传 {groupName}", progress.ToString("0.##") + "%");
			});
			// 目录内容变了，记录的修改时间作废，运行结束时重新记录。
			item.FolderMtime = null;
			// 移除临时文件。
			File.Delete(backupTempPath);
			return true;
		}

		/// <summary>
		/// 清理已失效的备份。
		/// </summary>
		public async Task ClearInvalidFiles(BackupContext context)
		{
			if (prevFiles.Count == 0)
			{
				return;
			}
			HashSet<string?> groups = new();
			foreach (var pair in prevFiles)
			{
				groups.Add(pair.Value.Group);
			}
			// 避免移除新的分组。
			foreach (var pair in files)
			{
				groups.Remove(pair.Value.Group);
			}
			if (groups.Count == 0)
			{
				return;
			}
			foreach (var groupName in groups)
			{
				string fileName = GetFileName(groupName);
				if (await context.GetCloudFile(CloudFolder, fileName) == null)
				{
					continue;
				}
				await context.Delete(CloudFolder, fileName);
				// 目录内容变了，记录的修改时间作废，运行结束时重新记录。
				item.FolderMtime = null;
				string displayName = CloudFolder.Length == 0 ? fileName : $"{CloudFolder}/{fileName}";
				AnsiConsole.MarkupLine($"    已清除过期备份 {displayName.EscapeMarkup()}");
			}
		}

		/// <summary>
		/// 返回分组对应的备份文件名。
		/// </summary>
		private string GetFileName(string? groupName)
		{
			// 配置与元数据直接使用 id 作为文件名，书籍使用分组名。
			return IsConfig ? id : (groupName ?? BackupUtils.AllGroupName);
		}

		/// <summary>
		/// 检查指定分组是否发生了改变。
		/// </summary>
		private bool IsGroupFilesChanged(BackupFileGroup group)
		{
			var groupName = group.Name;
			bool isChanged = false;
			foreach (var file in group.Files)
			{
				var name = BackupUtils.GetRelativeName(dir, file.FullName);
				if (!prevFiles.TryGetValue(name, out var info) || info.Group != groupName)
				{
					isChanged = true;
					files.Add(name, new BackupFileInfo(groupName, file));
					continue;
				}
				// 从旧文件列表中移除，便于最后统一移除已失效的备份。
				prevFiles.Remove(name);
				var fileLastModified = new DateTimeOffset(file.LastWriteTime).ToUnixTimeMilliseconds();
				if (info.LastModified == fileLastModified)
				{
					// 修改时间未发生改变，认为文件是相同的。
					files.Add(name, info);
					continue;
				}
				// 比较 MD5。
				string fileMD5 = Md5Utils.CalculateFileMD5(file.FullName);
				if (fileMD5 == info.MD5)
				{
					// MD5 相同，回写修改时间。
					info.LastModified = fileLastModified;
					needSave = true;
				}
				else
				{
					isChanged = true;
					// 不要改共享的 info，复制一份，只有上传成功、item.Files = task.Files 时才生效。
					info = new BackupFileInfo { MD5 = fileMD5, LastModified = info.LastModified, Group = info.Group };
				}
				files.Add(name, info);
			}
			return isChanged;
		}
	}
}

/// <summary>
/// 恢复命令。
/// </summary>
sealed class RestoreCommand : AsyncCommand<RestoreCommand.Settings>
{
	/// <summary>
	/// 命令的设置。
	/// </summary>
	public sealed class Settings : CommandSettings
	{
		[Description("恢复到的本地目录，默认为当前目录。")]
		[CommandArgument(0, "[path]")]
		public string? Path { get; init; }

		[Description("网盘的备份目录，也就是备份时使用的目录；config 的密码由它派生。")]
		[CommandOption("-d|--dir")]
		[DefaultValue("")]
		public string? Dir { get; init; }

		[Description("备份时用 --key 指定的额外口令；没设置过就不用给。")]
		[CommandOption("-k|--key")]
		[DefaultValue("")]
		public string? Key { get; init; }
	}

	/// <summary>
	/// 恢复使用的临时目录名，位于目标目录下，恢复完成后删除。
	/// </summary>
	private const string RestoreTempDir = ".backup-restore";

	/// <summary>
	/// 执行命令。
	/// </summary>
	protected override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
	{
		string destDir = Path.GetFullPath(settings.Path ?? Directory.GetCurrentDirectory());
		BackupUtils.WarnLowIntegrity("写入恢复目录");
		// 目标目录里已经有备份配置时，直接用它记录的备份路径和额外口令。
		BackupConfig? existsConfig = null;
		string existsConfigPath = Path.Combine(destDir, BackupUtils.BackupConfigFile);
		if (File.Exists(existsConfigPath))
		{
			existsConfig = new ConfigHolder<BackupConfig>(existsConfigPath).Config;
		}
		string? cloudDir = settings.Dir;
		if (string.IsNullOrEmpty(cloudDir))
		{
			cloudDir = existsConfig?.Dir;
		}
		if (string.IsNullOrEmpty(cloudDir))
		{
			cloudDir = AnsiConsole.Ask<string>("请输入网盘的备份目录:");
		}
		cloudDir = BackupUtils.NormalizeDir(cloudDir);
		string? key = string.IsNullOrEmpty(settings.Key) ? existsConfig?.Key : settings.Key;
		string configPwd = BackupUtils.GetConfigPassword(cloudDir, key);
		var cloud = new BaiduPan();
		string tempDir = Path.Combine(destDir, RestoreTempDir);
		AnsiConsole.MarkupLine($"准备将 [green]{cloudDir.EscapeMarkup()}[/] 恢复到 [green]{destDir.EscapeMarkup()}[/]");
		Directory.CreateDirectory(destDir);
		TryDeleteDirectory(tempDir);
		Directory.CreateDirectory(tempDir);
		try
		{
			// config 是唯一需要「先知道密码」的文件，解开它就拿到了每本书的密码和文件清单。
			string configDir = Path.Combine(tempDir, "config");
			string configArchive = Path.Combine(tempDir, "config.7z");
			Directory.CreateDirectory(configDir);
			var rootFiles = await cloud.List(cloudDir);
			var configFile = rootFiles.Find(file => !file.IsDir && file.Name == BackupUtils.ConfigId);
			if (configFile == null)
			{
				AnsiConsole.MarkupLine($"[red]{cloudDir.EscapeMarkup()} 里没有 config 备份文件，无法恢复。[/]");
				return 1;
			}
			await AnsiConsole.Status().StartAsync("下载 config", async ctx =>
			{
				await cloud.Download(configFile.FsId, configArchive, progress => ctx.Status($"下载 config {progress:0.##}%"));
			});
			await Extract(configArchive, configDir, configPwd, "config");
			string configPath = Path.Combine(configDir, BackupUtils.BackupConfigFile);
			if (!File.Exists(configPath))
			{
				AnsiConsole.MarkupLine($"[red]config 里没有 {BackupUtils.BackupConfigFile.EscapeMarkup()}，无法恢复。[/]");
				return 1;
			}
			BackupConfig backupConfig = new ConfigHolder<BackupConfig>(configPath).Config;
			BackupUtils.NormalizeConfig(backupConfig);
			// 逐本恢复。
			int restored = 0;
			List<string> failed = [];
			var books = backupConfig.Files.Where(pair => pair.Key != BackupUtils.ConfigId).ToList();
			for (int i = 0; i < books.Count; i++)
			{
				string id = books[i].Key;
				BackupConfigItem item = books[i].Value;
				string escapedName = Markup.Escape($"[{id}] {item.Name}");
				try
				{
					await AnsiConsole.Status().StartAsync($"    恢复 {i + 1}/{books.Count} {escapedName}", async ctx =>
					{
						await RestoreBook(cloud, cloudDir, tempDir, destDir, id, item, ctx);
					});
					restored++;
				}
				catch (Exception e)
				{
					failed.Add($"[{id}] {item.Name}");
					AnsiConsole.MarkupLine($"    [red]恢复 {escapedName} 失败[/]: {e.Message.EscapeMarkup()}");
				}
			}
			// 恢复书库根目录的元数据，并写回备份配置，之后可以直接在这份书库上继续备份。
			CopyFile(Path.Combine(configDir, "metadata_backup.db"), Path.Combine(destDir, BackupUtils.MetadataDB), true);
			CopyFile(Path.Combine(configDir, BackupUtils.MetadataDBPrefsBackup), Path.Combine(destDir, BackupUtils.MetadataDBPrefsBackup), true);
			string localConfig = Path.Combine(destDir, BackupUtils.BackupConfigFile);
			if (File.Exists(localConfig))
			{
				AnsiConsole.MarkupLine($"    [yellow]{BackupUtils.BackupConfigFile.EscapeMarkup()} 已存在，保留原文件[/]");
			}
			else
			{
				File.Copy(configPath, localConfig);
				// 写回的配置按归一化之后的路径重新保存一遍，避免还带着备份那个平台上的写法。
				ConfigHolder<BackupConfig> localHolder = new(localConfig);
				BackupUtils.NormalizeConfig(localHolder.Config);
				localHolder.Save();
			}
			AnsiConsole.WriteLine();
			AnsiConsole.MarkupLine($"已恢复 [green]{restored}[/] 本，失败 [red]{failed.Count}[/] 本");
			foreach (string name in failed)
			{
				AnsiConsole.MarkupLine($"    [red]{Markup.Escape(name)}[/]");
			}
			return failed.Count == 0 ? 0 : 1;
		}
		finally
		{
			TryDeleteDirectory(tempDir);
		}
	}

	/// <summary>
	/// 恢复一本书。
	/// </summary>
	/// <param name="cloud">网盘操作。</param>
	/// <param name="cloudDir">网盘的备份目录。</param>
	/// <param name="tempDir">临时目录。</param>
	/// <param name="destDir">恢复到的本地目录。</param>
	/// <param name="id">书籍的 id。</param>
	/// <param name="item">书籍的备份配置项。</param>
	/// <param name="ctx">状态输出。</param>
	private static async Task RestoreBook(BaiduPan cloud, string cloudDir, string tempDir, string destDir,
		string id, BackupConfigItem item, StatusContext ctx)
	{
		string folder = $"{cloudDir}{id}/";
		var cloudFiles = await cloud.List(folder);
		if (cloudFiles.Count == 0)
		{
			throw new Exception($"云端没有 {folder}");
		}
		// 云端每本书一个目录，文件名就是分组名（未分组时是 all）。
		var groups = item.Files.Values.Select(info => info.Group ?? BackupUtils.AllGroupName).Distinct(StringComparer.Ordinal).ToList();
		if (groups.Count == 0)
		{
			throw new Exception("备份配置里没有这本书的文件");
		}
		// 书目录名与 calibre 的规则一致：{作者}/{书名} ({id})。
		string bookDir = Path.Combine(destDir, ValidSegment(item.Author), $"{ValidSegment(item.Name)} ({id})");
		string bookTemp = Path.Combine(tempDir, id);
		try
		{
			foreach (string group in groups)
			{
				var cloudFile = cloudFiles.Find(file => !file.IsDir && file.Name == group);
				if (cloudFile == null)
				{
					throw new Exception($"云端找不到 {folder}{group}");
				}
				string archive = Path.Combine(bookTemp, group + ".7z");
				string extractDir = Path.Combine(bookTemp, "extract");
				Directory.CreateDirectory(extractDir);
				await cloud.Download(cloudFile.FsId, archive, progress => ctx.Status($"    下载 {group} {progress:0.##}%"));
				await Extract(archive, extractDir, item.Pwd, $"{id}/{group}");
				MoveFiles(extractDir, bookDir);
				TryDeleteDirectory(extractDir);
			}
		}
		finally
		{
			TryDeleteDirectory(bookTemp);
		}
	}

	/// <summary>
	/// 删除目录，失败时忽略：临时目录清不掉不影响恢复结果。
	/// </summary>
	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, true);
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
		}
	}

	/// <summary>
	/// 解压指定文件，失败时带上是哪个备份文件解不开。
	/// </summary>
	private static async Task Extract(string archive, string outputDir, string password, string name)
	{
		try
		{
			await SevenZ.Extract(new()
			{
				FileName = archive,
				OutputDirectory = outputDir,
				Password = password,
			});
		}
		catch (Exception e)
		{
			if (e.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
			{
				throw new Exception($"解压 {name} 失败：密码不对。-d 必须与备份时一致（config 的密码由备份目录派生），"
					+ "备份时用 --key 设过额外口令的话，恢复时也要用 -k 提供同样的值。", e);
			}
			throw new Exception($"解压 {name} 失败：{e.Message}", e);
		}
	}

	/// <summary>
	/// 把解压出来的文件按原有结构移动到书籍目录。
	/// </summary>
	/// <remarks>
	/// 归档里存的是打包时相对书籍目录的路径。Windows 上打包的归档可能把分隔符存成了 <c>\</c>，
	/// 在别的平台上解出来会变成文件名的一部分，所以这里统一按 <c>\</c> 和 <c>/</c> 拆开再拼。
	/// </remarks>
	private static void MoveFiles(string sourceDir, string destDir)
	{
		foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
		{
			string relative = Path.GetRelativePath(sourceDir, file);
			string[] segments = relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
			if (segments.Length == 0 || segments.Contains(".") || segments.Contains(".."))
			{
				continue;
			}
			string target = Path.Combine([destDir, .. segments]);
			string? parent = Path.GetDirectoryName(target);
			if (!string.IsNullOrEmpty(parent))
			{
				Directory.CreateDirectory(parent);
			}
			File.Move(file, target, true);
		}
	}

	/// <summary>
	/// 换掉当前平台上不能用作文件名的字符，保证在别的平台生成的目录名也能落地。
	/// </summary>
	private static string ValidSegment(string name)
	{
		char[] invalid = Path.GetInvalidFileNameChars();
		StringBuilder builder = new(name.Length);
		foreach (char ch in name)
		{
			builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
		}
		string result = builder.ToString().Trim();
		return result.Length == 0 ? "_" : result;
	}

	/// <summary>
	/// 复制文件，源文件不存在时直接跳过。
	/// </summary>
	private static void CopyFile(string sourcePath, string destPath, bool overwrite)
	{
		if (File.Exists(sourcePath))
		{
			File.Copy(sourcePath, destPath, overwrite);
		}
	}
}

/// <summary>
/// 清理历史记录命令。
/// </summary>
/// <remarks>
/// 只删除书籍目录里的历史记录（<c>{分组名} {yyyy-MM-dd} ...</c>），当前的备份文件（<c>{分组名}</c>）不动。
/// 根目录下旧命名方式留下的历史文件不在这里处理，备份时会自动迁移。
/// </remarks>
sealed class ClearHistoryCommand : AsyncCommand<ClearHistoryCommand.Settings>
{
	/// <summary>
	/// 命令的设置。
	/// </summary>
	public sealed class Settings : CommandSettings
	{
		[Description("书库目录，默认为当前目录。")]
		[CommandArgument(0, "[path]")]
		public string? Path { get; init; }

		[Description("删除前不再询问。")]
		[CommandOption("-y|--yes")]
		[DefaultValue(false)]
		public bool Yes { get; init; }
	}

	/// <summary>
	/// 匹配历史记录文件名的正则表达式：分组名 + 空格 + 日期，日期后面还可能跟哈希或时间。
	/// </summary>
	private static readonly Regex HistoryNameRegex = new(@" \d{4}-\d{2}-\d{2}( |$)");

	/// <summary>
	/// 每次删除请求携带的文件数。
	/// </summary>
	/// <remarks>
	/// 文件管理接口单次超过 50 个会整批不生效（外层 errno 12、逐项 -9），所以按 50 一批。
	/// </remarks>
	private const int DeleteBatchSize = 50;

	/// <summary>
	/// 一批删除失败后的重试次数。
	/// </summary>
	private const int DeleteMaxRetry = 3;

	/// <summary>
	/// 两批删除之间的等待时间，避免连续请求触发频率限制。
	/// </summary>
	private const int DeleteBatchDelayMs = 300;

	/// <summary>
	/// 执行命令。
	/// </summary>
	protected override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
	{
		var timer = Stopwatch.StartNew();
		string libraryDir = Path.GetFullPath(settings.Path ?? Directory.GetCurrentDirectory());
		// 备份路径记在书库的备份配置里，不用再单独指定。
		string configPath = Path.Combine(libraryDir, BackupUtils.BackupConfigFile);
		if (!File.Exists(configPath))
		{
			AnsiConsole.MarkupLine($"[red]{libraryDir.EscapeMarkup()}[/] 里没有备份配置，先备份一次再来");
			return 1;
		}
		ConfigHolder<BackupConfig> configHolder = new(configPath);
		string? cloudDir = configHolder.Config.Dir;
		if (string.IsNullOrEmpty(cloudDir))
		{
			AnsiConsole.MarkupLine($"[red]{BackupUtils.BackupConfigFile.EscapeMarkup()}[/] 里没有记录备份路径，先备份一次再来");
			return 1;
		}
		cloudDir = BackupUtils.NormalizeDir(cloudDir);
		var cloud = new BaiduPan();
		AnsiConsole.MarkupLine($"准备清理 [green]{cloudDir.EscapeMarkup()}[/] 里的历史记录");
		// 递归列一次整个备份目录就够了。以前是逐本书列目录，6000 多本书就是 6000 多次请求，
		// 而接口大约 0.5 次/秒，光扫描就要几个小时；递归列目录只要几十页（实测 33000 项、34 页、约 3 分钟）。
		List<BaiduPanFileInfo> all;
		try
		{
			List<BaiduPanFileInfo> scanned = [];
			await AnsiConsole.Status().StartAsync("正在扫描历史记录", async ctx =>
			{
				scanned = await cloud.ListAll(cloudDir, count => ctx.Status($"正在扫描历史记录，已读取 {count} 项"));
			});
			all = scanned;
		}
		catch (Exception e)
		{
			AnsiConsole.MarkupLine($"[red]列出备份目录失败[/]：{e.Message.EscapeMarkup()}");
			return 1;
		}
		// 历史记录都在书籍目录里，按目录分组，顺便拿到书籍 id。
		Dictionary<string, List<BaiduPanFileInfo>> history = new(StringComparer.Ordinal);
		foreach (var file in all)
		{
			if (file.IsDir || !HistoryNameRegex.IsMatch(file.Name))
			{
				continue;
			}
			// 只处理书籍目录这一层，根目录下的 config 之类的文件一律不碰。
			string? id = GetBookFolderName(cloudDir, file.Path);
			if (id == null)
			{
				continue;
			}
			if (!history.TryGetValue(id, out var files))
			{
				files = [];
				history.Add(id, files);
			}
			files.Add(file);
		}
		int count = history.Sum(item => item.Value.Count);
		if (count == 0)
		{
			AnsiConsole.MarkupLine("[green]没有需要清理的历史记录[/]");
			return 0;
		}
		long totalSize = history.Sum(item => item.Value.Sum(file => file.Size));
		AnsiConsole.MarkupLine($"{history.Count} 本书共 [yellow]{count}[/] 个历史记录，{totalSize / 1024.0 / 1024.0:0.0} MB");
		if (!settings.Yes && !AnsiConsole.Confirm("确认删除这些历史记录吗？当前的备份不会受影响。", false))
		{
			return 0;
		}
		string[] paths = history.SelectMany(pair => pair.Value).Select(file => file.Path).ToArray();
		int deleted = 0;
		int skipped = 0;
		List<string> failed = [];
		// 分批删除，一批一个请求。上百批下来要好几分钟，这里必须有进度，否则看起来像卡住了。
		await AnsiConsole.Progress().StartAsync(async ctx =>
		{
			var task = ctx.AddTask("删除历史记录", maxValue: paths.Length);
			foreach (var chunk in paths.Chunk(DeleteBatchSize))
			{
				for (int attempt = 0; ; attempt++)
				{
					try
					{
						await cloud.Delete(chunk);
						deleted += chunk.Length;
						break;
					}
					catch (Exception) when (attempt < DeleteMaxRetry)
					{
						// 偶发的频率限制和网络抖动，等一会儿重试同一批。
						await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)));
					}
					catch (Exception)
					{
						// 整批仍然失败：逐个删。这样既能只记下真正删不掉的文件，
						// 也能把「同一批里已经被删掉、再删报不存在」的那些算作成功。
						int consecutiveFailure = 0;
						foreach (string path in chunk)
						{
							// 连续多个文件都失败，多半是频率限制或网络断了，不再一个个硬试，
							// 这些留给下次运行（重复执行是安全的）。
							if (consecutiveFailure >= 5)
							{
								skipped++;
								continue;
							}
							try
							{
								await cloud.Delete(path);
								deleted++;
								consecutiveFailure = 0;
							}
							catch (BaiduPanNotExistException)
							{
								deleted++;
								consecutiveFailure = 0;
							}
							catch (Exception e)
							{
								failed.Add($"{path}：{e.Message}");
								consecutiveFailure++;
							}
						}
						break;
					}
				}
				task.Increment(chunk.Length);
				// 稍微放慢一点，避免连续删除触发频率限制。
				await Task.Delay(DeleteBatchDelayMs);
			}
		});
		AnsiConsole.WriteLine();
		string skippedMessage = skipped > 0 ? $"，跳过 [yellow]{skipped}[/] 个（接口连续失败，下次运行会重试）" : "";
		AnsiConsole.MarkupLine($"已删除 [green]{deleted}[/] 个历史记录，失败 [red]{failed.Count}[/] 个{skippedMessage}");
		if (failed.Count > 0)
		{
			foreach (string message in failed.Take(10))
			{
				AnsiConsole.MarkupLine($"    [red]{message.EscapeMarkup()}[/]");
			}
			if (failed.Count > 10)
			{
				AnsiConsole.MarkupLine($"    [red]……还有 {failed.Count - 10} 个失败[/]");
			}
		}
		// 删除会改变书籍目录的内容，配置里记的云端目录修改时间随之失效。
		// 不刷新的话下次备份会为每本受影响的书单独列一次目录（几千次请求，要一个多小时）。
		if (deleted > 0)
		{
			await RefreshFolderMtimes(cloud, cloudDir, configHolder, history.Keys);
		}
		AnsiConsole.MarkupLine($"总用时 {FormatElapsed(timer.Elapsed)}");
		return failed.Count == 0 && skipped == 0 ? 0 : 1;
	}

	/// <summary>
	/// 返回文件所在书籍目录的名字；不在备份目录下的书籍目录里时返回 null。
	/// </summary>
	/// <remarks>
	/// 递归列目录返回的是完整路径（例如 <c>/Book/1665/all 2026-02-27 3f2a1b8c</c>），
	/// 这里只认「备份目录下恰好一层」的文件，根目录里的 config 之类不会被当成历史记录。
	/// </remarks>
	private static string? GetBookFolderName(string cloudDir, string path)
	{
		int index = path.LastIndexOf('/');
		if (index <= 0)
		{
			return null;
		}
		string parent = path[..index];
		// cloudDir 总是以 / 开头和结尾，去掉末尾的 / 就是父目录的前缀。
		string prefix = cloudDir[..^1];
		if (!parent.StartsWith(prefix + "/", StringComparison.Ordinal))
		{
			return null;
		}
		string name = parent[(prefix.Length + 1)..];
		return name.Length > 0 && !name.Contains('/') ? name : null;
	}

	/// <summary>
	/// 刷新受影响书籍的云端目录修改时间。
	/// </summary>
	/// <remarks>
	/// 删掉历史记录会改变书籍目录的内容，配置里记的目录修改时间随之失效。不刷新的话，
	/// 下次备份判断「云端备份还在不在」时对每本受影响的书都要单独列一次目录，
	/// 几千次请求要一个多小时；这里重新列一次根目录就能一次补齐。
	/// </remarks>
	private static async Task RefreshFolderMtimes(BaiduPan cloud, string cloudDir, ConfigHolder<BackupConfig> configHolder, IEnumerable<string> ids)
	{
		try
		{
			int updated = 0;
			await AnsiConsole.Status().StartAsync("刷新书籍目录的修改时间", async ctx =>
			{
				var folders = (await cloud.List(cloudDir))
					.Where(file => file.IsDir)
					.ToDictionary(file => file.Name, StringComparer.Ordinal);
				foreach (string id in ids)
				{
					if (folders.TryGetValue(id, out var folder)
						&& configHolder.Config.Files.TryGetValue(id, out var item)
						&& item.FolderMtime != folder.ServerModifyTime)
					{
						item.FolderMtime = folder.ServerModifyTime;
						updated++;
					}
				}
			});
			if (updated > 0)
			{
				configHolder.Save();
				AnsiConsole.MarkupLine($"    已刷新 [green]{updated}[/] 本书的目录修改时间，下次备份不用再逐本列目录");
			}
		}
		catch (Exception e)
		{
			// 刷新失败只会让下次备份多做一次校验，不影响这次清理的结果。
			AnsiConsole.MarkupLine($"    [yellow]刷新目录修改时间失败[/]：{e.Message.EscapeMarkup()}");
		}
	}

	/// <summary>
	/// 把耗时格式化成「x 分 y 秒」。
	/// </summary>
	private static string FormatElapsed(TimeSpan elapsed)
	{
		if (elapsed.TotalMinutes < 1)
		{
			return $"{elapsed.TotalSeconds:0.0} 秒";
		}
		return $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒";
	}
}

/// <summary>
/// 备份配置。
/// </summary>
sealed class BackupConfig
{
	/// <summary>
	/// 备份路径。
	/// </summary>
	public string? Dir { get; set; } = null;
	/// <summary>
	/// 参与派生 config 密码的额外口令，为空时只用备份目录派生。
	/// </summary>
	public string? Key { get; set; } = null;
	/// <summary>
	/// 备份历史记录个数。
	/// </summary>
	public int? History { get; set; } = null;
	/// <summary>
	/// 文件列表。
	/// </summary>
	public Dictionary<string, BackupConfigItem> Files { get; set; } = new();
}

/// <summary>
/// 备份配置项。
/// </summary>
sealed class BackupConfigItem
{
	/// <summary>
	/// 书籍作者。
	/// </summary>
	public required string Author { get; set; }
	/// <summary>
	/// 书名。
	/// </summary>
	public required string Name { get; set; }
	/// <summary>
	/// 加密密码。
	/// </summary>
	public required string Pwd { get; set; }
	/// <summary>
	/// 压缩后大小。
	/// </summary>
	public long Size { get; set; }
	/// <summary>
	/// 书籍目录在最近一次备份后的修改时间（秒）；null 表示未知，下次需要校验。
	/// </summary>
	public long? FolderMtime { get; set; } = null;
	/// <summary>
	/// 书籍包含的文件信息，键是相对书籍目录的路径（统一用 / 分隔）。
	/// </summary>
	public required Dictionary<string, BackupFileInfo> Files { get; set; }

	/// <summary>
	/// 返回数据行。
	/// </summary>
	public string GetDataRow(string path)
	{
		return $"{path}\t{Name}\t{Author}\t{Pwd}\t\t{Size}";
	}
}

/// <summary>
/// 备份文件的信息。
/// </summary>
sealed class BackupFileInfo
{
	public BackupFileInfo()
	{
		MD5 = "";
	}
	public BackupFileInfo(string? group, FileInfo file)
	{
		LastModified = new DateTimeOffset(file.LastWriteTime).ToUnixTimeMilliseconds();
		MD5 = Md5Utils.CalculateFileMD5(file.FullName);
		Group = group;
	}
	/// <summary>
	/// 文件的修改时间。
	/// </summary>
	public long LastModified { get; set; }
	/// <summary>
	/// 文件的 MD5。
	/// </summary>
	public string MD5 { get; set; }
	/// <summary>
	/// 文件的分组。
	/// </summary>
	public string? Group { get; set; }
}

/// <summary>
/// 备份与恢复共用的常量与路径工具。
/// </summary>
static class BackupUtils
{
	/// <summary>
	/// 备份配置与元数据时使用的 ID。
	/// </summary>
	public const string ConfigId = "config";
	/// <summary>
	/// 书籍未再细分为多个文件时使用的分组名。
	/// </summary>
	public const string AllGroupName = "all";
	/// <summary>
	/// calibre 的元数据数据库。
	/// </summary>
	public const string MetadataDB = "metadata.db";
	/// <summary>
	/// calibre 的元数据备份，备份时用它代替被 calibre 占用的 metadata.db。
	/// </summary>
	public const string MetadataDBPrefsBackup = "metadata_db_prefs_backup.json";
	/// <summary>
	/// 备份配置的文件名。
	/// </summary>
	public const string BackupConfigFile = ".backup-config.json";
	/// <summary>
	/// 备份数据的文件名。
	/// </summary>
	public const string BackupDataFile = ".backup-data.csv";
	/// <summary>
	/// 迁移时为与目录同名的文件追加的后缀。
	/// </summary>
	public const string OldNameSuffix = ".old";
	/// <summary>
	/// 密码的字符范围。
	/// </summary>
	public const string PwdChars = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
	/// <summary>
	/// 派生 config 密码时的种子前缀，换算法时要改成 v2。
	/// </summary>
	private const string ConfigPwdSeed = "calibre-backup|config|v1|";
	/// <summary>
	/// 派生出的 config 密码长度。
	/// </summary>
	private const int ConfigPwdLength = 24;

	/// <summary>
	/// 标准化网盘的备份目录：统一用 / 分隔、以 / 开头和结尾，并做 Unicode 归一化。
	/// </summary>
	/// <remarks>
	/// 这个结果同时是派生 config 密码的种子，所以必须与平台无关：用户写成 <c>书库备份</c>、<c>/书库备份/</c>
	/// 或者用 <c>\</c> 分隔，都要得到同一个字符串。
	/// </remarks>
	public static string NormalizeDir(string dir)
	{
		StringBuilder builder = new("/");
		foreach (string segment in dir.Replace('\\', '/').Normalize(NormalizationForm.FormC).Split('/', StringSplitOptions.RemoveEmptyEntries))
		{
			builder.Append(segment);
			builder.Append('/');
		}
		return builder.ToString();
	}

	/// <summary>
	/// 标准化书籍内文件的相对路径：统一用 / 分隔，并做 Unicode 归一化。
	/// </summary>
	/// <remarks>
	/// Windows 上得到的是 \ 分隔的路径，macOS 上是 /；macOS 还可能给出分解形式的 Unicode
	/// （例如带音标的字符）。备份配置里的键必须统一，否则换平台备份时会把同一批文件当成新文件重新打包上传。
	/// </remarks>
	public static string NormalizeRelativePath(string path)
	{
		return path.Replace('\\', '/').Normalize(NormalizationForm.FormC);
	}

	/// <summary>
	/// 返回文件相对书籍目录的路径，统一用 / 分隔。
	/// </summary>
	public static string GetRelativeName(string dir, string file)
	{
		return NormalizeRelativePath(Path.GetRelativePath(dir, file));
	}

	/// <summary>
	/// 返回由备份目录派生的 config 密码。
	/// </summary>
	/// <param name="dir">备份目录（已归一化）。</param>
	/// <param name="key">额外口令，为空时只用备份目录派生。</param>
	/// <remarks>
	/// config 里存着每本书的随机密码，但它自己也在 config 里，所以密码不能来自 config 本身。
	/// 用备份目录派生之后，只要知道备份目录（网盘上就看得见）就能解开 config，实现「从 0 恢复」；
	/// 代价是知道备份目录的人也能解开，加密只用来避免内容被扫描、被和谐。
	/// 需要更强的强度时用 <c>--key</c> 掺一个额外口令进去，但恢复时也要提供它。
	/// </remarks>
	public static string GetConfigPassword(string dir, string? key)
	{
		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(ConfigPwdSeed + dir + "\n" + key));
		StringBuilder builder = new(ConfigPwdLength);
		for (int i = 0; i < ConfigPwdLength; i++)
		{
			builder.Append(PwdChars[hash[i] % PwdChars.Length]);
		}
		return builder.ToString();
	}

	/// <summary>
	/// 判断当前进程是否运行在「低完整性」级别。
	/// </summary>
	/// <remarks>
	/// 工作区目录带 Windows「低完整性」标签，在里面 dotnet publish 出来的 exe 会继承该标签，
	/// 进程按低完整性启动后就写不了普通（中等完整性）文件，例如书库里的 .backup-config.json。
	/// </remarks>
	public static bool IsLowIntegrity()
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}
		IntPtr token = IntPtr.Zero;
		IntPtr buffer = IntPtr.Zero;
		try
		{
			if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
			{
				return false;
			}
			// 先问需要多大的缓冲区。
			GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int length);
			if (length <= 0)
			{
				return false;
			}
			buffer = Marshal.AllocHGlobal(length);
			if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _))
			{
				return false;
			}
			// TOKEN_MANDATORY_LABEL 的第一个字段是 SID 指针。
			var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
			// S-1-16-4096 表示低完整性级别。
			return sid.Value == "S-1-16-4096";
		}
		catch
		{
			return false;
		}
		finally
		{
			if (buffer != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(buffer);
			}
			if (token != IntPtr.Zero)
			{
				CloseHandle(token);
			}
		}
	}

	/// <summary>
	/// 进程处于「低完整性」时打印修复提示。
	/// </summary>
	/// <param name="action">接下来要做的写操作，例如「写入书库」。</param>
	public static void WarnLowIntegrity(string action)
	{
		if (!IsLowIntegrity())
		{
			return;
		}
		string exePath = Environment.ProcessPath ?? "calibre-backup.exe";
		AnsiConsole.MarkupLine($"[yellow]警告：当前进程是「低完整性」，{action.EscapeMarkup()}会报 Access to the path is denied。[/]");
		AnsiConsole.MarkupLine("[yellow]      工作区目录带低完整性标签，在里面发布出来的 exe 会让进程按低完整性启动。[/]");
		AnsiConsole.MarkupLine("      请用工作区的 [green]publish-exe.cmd[/] 发布到 %USERPROFILE%\\bin 后再运行，或在管理员终端执行：");
		AnsiConsole.MarkupLine($"      [green]icacls \"{exePath.EscapeMarkup()}\" /setintegritylevel Medium[/]");
		AnsiConsole.WriteLine();
	}

	/// <summary>
	/// TOKEN_QUERY 权限。
	/// </summary>
	private const int TokenQuery = 0x0008;
	/// <summary>
	/// TOKEN_INFORMATION_CLASS.TokenIntegrityLevel。
	/// </summary>
	private const int TokenIntegrityLevel = 25;

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetCurrentProcess();

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr handle);

	/// <summary>
	/// 标准化配置里的所有路径，返回是否有改动。
	/// </summary>
	public static bool NormalizeConfig(BackupConfig config)
	{
		bool changed = false;
		string? dir = config.Dir;
		if (!string.IsNullOrEmpty(dir))
		{
			string newDir = NormalizeDir(dir);
			if (newDir != dir)
			{
				config.Dir = newDir;
				changed = true;
			}
		}
		foreach (BackupConfigItem item in config.Files.Values)
		{
			Dictionary<string, BackupFileInfo>? files = null;
			foreach (var pair in item.Files)
			{
				string name = NormalizeRelativePath(pair.Key);
				if (name == pair.Key)
				{
					continue;
				}
				files ??= new(item.Files, StringComparer.Ordinal);
				files.Remove(pair.Key);
				files[name] = pair.Value;
				changed = true;
			}
			if (files != null)
			{
				item.Files = files;
			}
		}
		return changed;
	}
}
