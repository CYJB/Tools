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
#:property RuntimeIdentifier=win-x64
#:property DebugType=embedded
#nullable enable

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp<BackupCommand>();
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

		[Description("保留的备份历史个数，默认为 3。")]
		[CommandOption("--history")]
		[DefaultValue("")]
		public int? History { get; init; }
	}

	/// <summary>
	/// 备份配置的文件名。
	/// </summary>
	private const string BackupConfigFile = ".backup-config.json";
	/// <summary>
	/// 备份数据的文件名。
	/// </summary>
	private const string BackupDataFile = ".backup-data.csv";
	/// <summary>
	/// calibre 的元数据数据库。
	/// </summary>
	private const string MetadataDB = "metadata.db";
	/// <summary>
	/// 备份配置与元数据时使用的 ID。
	/// </summary>
	private const string ConfigId = "config";
	/// <summary>
	/// 书籍未再细分为多个文件时使用的分组名。
	/// </summary>
	private const string AllGroupName = "all";
	/// <summary>
	/// 迁移时为与目录同名的文件追加的后缀。
	/// </summary>
	private const string OldNameSuffix = ".old";

	/// <summary>
	/// 执行命令。
	/// </summary>
	protected override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
	{
		// 工作区里发布出来的 exe 会带「低完整性」标签，进程按低完整性启动后写不了书库里的普通文件。
		if (IsLowIntegrity())
		{
			string exePath = Environment.ProcessPath ?? "calibre-backup.exe";
			AnsiConsole.MarkupLine("[yellow]警告：当前进程是「低完整性」，写入书库会报 Access to the path is denied。[/]");
			AnsiConsole.MarkupLine("[yellow]      工作区目录带低完整性标签，在里面发布出来的 exe 会让进程按低完整性启动。[/]");
			AnsiConsole.MarkupLine("      请用工作区的 [green]publish-exe.cmd[/] 发布到 %USERPROFILE%\\bin 后再运行，或在管理员终端执行：");
			AnsiConsole.MarkupLine($"      [green]icacls \"{exePath.EscapeMarkup()}\" /setintegritylevel Medium[/]");
			AnsiConsole.WriteLine();
		}
		var currentDir = settings.Path ?? Directory.GetCurrentDirectory();
		if (!File.Exists(Path.Combine(currentDir, MetadataDB)))
		{
			AnsiConsole.MarkupLine($"[red]{currentDir.EscapeMarkup()}[/] 不是 calibre 电子书目录");
			return 0;
		}
		ConfigHolder<BackupConfig> configHolder = new(Path.Combine(currentDir, BackupConfigFile));
		BackupConfig config = configHolder.Config;
		bool needSave = false;
		if (settings.History.HasValue)
		{
			config.History = settings.History.Value;
			needSave = true;
		}
		if (!string.IsNullOrEmpty(settings.Dir))
		{
			config.Dir = NormalizeDir(settings.Dir);
			needSave = true;
		}
		if (string.IsNullOrEmpty(config.Dir))
		{
			// 不指定备份目录容易误覆盖，这里总是询问。
			config.Dir = NormalizeDir(AnsiConsole.Ask<string>("请输入网盘的备份目录:"));
			needSave = true;
		}
		if (needSave)
		{
			configHolder.Save();
		}
		// 列出备份文件信息。
		if (settings.ListData)
		{
			ListData(config, Path.Combine(currentDir, BackupDataFile));
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
	/// 标准化备份目录。
	/// </summary>
	private static string NormalizeDir(string dir)
	{
		if (!dir.StartsWith('/'))
		{
			dir = "/" + dir;
		}
		if (!dir.EndsWith('/'))
		{
			dir += "/";
		}
		return dir;
	}

	/// <summary>
	/// 判断当前进程是否运行在「低完整性」级别。
	/// </summary>
	/// <remarks>
	/// 工作区目录带 Windows「低完整性」标签，在里面 dotnet publish 出来的 exe 会继承该标签，
	/// 进程按低完整性启动后就写不了普通（中等完整性）文件，例如书库里的 .backup-config.json。
	/// </remarks>
	private static bool IsLowIntegrity()
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
			string path = pair.Key == ConfigId ? prefix + pair.Key : $"{prefix}{pair.Key}/";
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
	/// 备份配置。
	/// </summary>
	private class BackupConfig
	{
		/// <summary>
		/// 备份路径。
		/// </summary>
		public string? Dir { get; set; } = null;
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
	private class BackupConfigItem
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
		/// 书籍包含的文件信息。
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
	private class BackupFileInfo
	{
		public BackupFileInfo()
		{
			MD5 = "";
		}
		public BackupFileInfo(string? group, string path)
		{
			var fileInfo = new FileInfo(path);
			LastModified = new DateTimeOffset(fileInfo.LastWriteTime).ToUnixTimeMilliseconds();
			MD5 = Md5Utils.CalculateFileMD5(path);
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
		/// calibre 的元数据备份。
		/// </summary>
		private const string MetadataDBPrefsBackup = "metadata_db_prefs_backup.json";
		/// <summary>
		/// 密码的字符范围。
		/// </summary>
		private const string PwdChars = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
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
		/// 获取备份的临时文件路径。
		/// </summary>
		public string BackupTempPath => backupTempPath;

		/// <summary>
		/// 备份到云盘。
		/// </summary>
		public async Task Backup(string dir)
		{
			backupTempPath = Path.Combine(dir, BackupTempFile);

			// 提前把与书籍目录同名的旧文件批量改名，迁移时就能直接创建目录、移动文件。
			await PrepareLegacyRenames();

			bool hasBackup = false;
			var authors = Directory.GetDirectories(dir)
				.Select(path => Path.GetFileName(path))
				.Where(name => !name.StartsWith('.'))
				.ToList();
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
				// 发生了备份操作，备份配置本身。
				await AnsiConsole.Status().StartAsync($"    备份配置", async ctx =>
				{
					// calibre 在运行时会占用 metadata.db，会导致 7z 压缩失败。
					// 总是将 metadata.db 备份为 metadata_backup.db，确保不会压缩失败。
					string metaPath = Path.Combine(dir, MetadataDB);
					string metaBackupPath = Path.Combine(dir, MetadataBackupDB);
					using (FileStream stream = File.Open(metaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
					{
						using FileStream outStream = File.OpenWrite(metaBackupPath);
						await stream.CopyToAsync(outStream);
					}
					// 配置使用固定的 id。
					var item = GetBackupConfigItem(ConfigId, "", "config & metadata");
					BackupTask task = new(ConfigId, item, dir);
					BackupFileGroup group = new();
					group.Files.Add(Path.Combine(dir, BackupConfigFile));
					group.Files.Add(metaBackupPath);
					group.Files.Add(Path.Combine(dir, MetadataDBPrefsBackup));
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
					configHolder.Save();
				});
			}

			// 刷新本次运行中变动过的书籍目录的修改时间（上传/改名/删除/迁移都会改它）。
			await RefreshFolderMtimes();
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
					if (pair.Key == ConfigId || pair.Value.FolderMtime != null)
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
				configHolder.Save();
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
			string candidate;
			if (string.IsNullOrEmpty(hash))
			{
				// 拿不到哈希（极少见）时退回只带日期。
				candidate = name + date;
			}
			else
			{
				string shortHash = hash[..Math.Min(8, hash.Length)].ToLowerInvariant();
				candidate = $"{name}{date} {shortHash}";
			}
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
		/// 将未分组的旧备份文件归入 <see cref="AllGroupName"/> 分组。
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
						info.Group = AllGroupName;
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
			if (name.StartsWith(id + OldNameSuffix, StringComparison.Ordinal))
			{
				// 上次迁移时改过名，但还没来得及移动的文件。
				newName = AllGroupName;
				return true;
			}
			if (name == id)
			{
				newName = AllGroupName;
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
				newName = $"{AllGroupName} {rest}";
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
			string name = id + OldNameSuffix;
			for (int i = 2; files.Exists(file => file.Name == name); i++)
			{
				name = id + OldNameSuffix + i;
			}
			return name;
		}

		/// <summary>
		/// 备份指定作者的目录。
		/// </summary>
		private async Task<bool> BackupAuthor(string dir, string author)
		{
			bool isBackup = false;
			var fileNames = Directory.GetDirectories(dir)
				.Select(path => Path.GetFileName(path))
				.ToList();
			foreach (var fileName in fileNames)
			{
				var match = BookNameRegex.Match(fileName);
				if (match.Success)
				{
					var name = match.Groups[1].Value;
					var id = match.Groups[2].Value;
					isBackup |= await BackupBook(id, author, name, Path.Combine(dir, fileName));
				}
				else
				{
					string name = $"{author}/{fileName}";
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
					// 目录长度注意包含最后的目录分隔符。
					var dirLen = dir.Length + 1;
					var files = Directory.GetFiles(dir, "", SearchOption.AllDirectories);
					BackupTask task = new(id, item, dir);
					List<BackupFileGroup> groups;
					// 计算目录内所有文件的大小。
					long totalSize = 0;
					foreach (string file in files)
					{
						FileInfo fileInfo = new FileInfo(file);
						totalSize += fileInfo.Length;
					}
					if (totalSize >= MultiFileThreshold)
					{
						// 按照文件类型分组上传。
						groups = GroupFiles(files, dirLen);
					}
					else
					{
						// 不分组，直接整体上传。
						BackupFileGroup group = new(AllGroupName);
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
						configHolder.Save();
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
		/// <remarks>
		/// <list type="bullet">
		/// <item>metadata.opf 和 cover.jpg 作为元数据 meta</item>
		/// <item>data 作为 data</item>
		/// <item>其它分组为对应的扩展名，没有扩展名的文件作为 other</item>
		/// </list>
		/// </remarks>
		private static List<BackupFileGroup> GroupFiles(ICollection<string> files, int dirLen)
		{
			// 按照文件类型分组。
			List<BackupFileGroup> groups = new();
			foreach (string filePath in files)
			{
				var fileName = filePath[dirLen..];
				string groupName;
				if (fileName == "metadata.opf" || fileName == "cover.jpg")
				{
					groupName = "meta";
				}
				else if (fileName == "data" || fileName.StartsWith("data" + Path.DirectorySeparatorChar))
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
				group.Files.Add(filePath);
			}
			return groups;
		}

		/// <summary>
		/// 返回指定的备份配置项。
		/// </summary>
		private BackupConfigItem GetBackupConfigItem(string id, string author, string name)
		{
			var files = configHolder.Config.Files;
			if (!files.TryGetValue(id, out var item))
			{
				item = new BackupConfigItem
				{
					Author = author,
					Name = name,
					Pwd = StringUtils.RandomString(7, 10, PwdChars),
					Files = new(),
				};
				files.Add(id, item);
			}
			return item;
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
		/// 分组的文件列表。
		/// </summary>
		public List<string> Files { get; init; } = new();
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
		private readonly Dictionary<string, BackupFileInfo> files = new();
		/// <summary>
		/// 待备份的目录。
		/// </summary>
		private readonly string dir = dir;
		/// <summary>
		/// 目录部分的长度。
		/// </summary>
		private readonly int dirLen = dir.Length + ((dir.EndsWith('/') || dir.EndsWith('\\')) ? 0 : 1);
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
		private bool IsConfig => id == ConfigId;
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
				Files = group.Files,
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
			return IsConfig ? id : (groupName ?? AllGroupName);
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
				var name = file[dirLen..];
				if (!prevFiles.TryGetValue(name, out var info) || info.Group != groupName)
				{
					isChanged = true;
					files.Add(name, new BackupFileInfo(groupName, file));
					continue;
				}
				// 从旧文件列表中移除，便于最后统一移除已失效的备份。
				prevFiles.Remove(name);
				var fileInfo = new FileInfo(file);
				var fileLastModified = new DateTimeOffset(fileInfo.LastWriteTime).ToUnixTimeMilliseconds();
				if (info.LastModified == fileLastModified)
				{
					// 修改时间未发生改变，认为文件是相同的。
					files.Add(name, info);
					continue;
				}
				// 比较 MD5。
				string fileMD5 = Md5Utils.CalculateFileMD5(file);
				if (fileMD5 == info.MD5)
				{
					// MD5 相同，回写修改时间。
					info.LastModified = fileLastModified;
					needSave = true;
				}
				else
				{
					isChanged = true;
					info.MD5 = fileMD5;
				}
				files.Add(name, info);
			}
			return isChanged;
		}
	}
}
