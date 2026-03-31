/**
 * 备份 calibre 书库。
 */

#load "utils/7z.csx"
#load "utils/baidu-pan.csx"
#load "utils/config-holder.csx"
#load "utils/md5.csx"
#load "utils/string.csx"
#r "nuget: Spectre.Console, 0.54.0"
#r "nuget: Spectre.Console.Cli, 0.53.1"

#nullable enable

using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp<BackupCommand>();
return await app.RunAsync(Args.ToArray());

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
	/// 执行命令。
	/// </summary>
	public override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
	{
		var currentDir = settings.Path ?? Directory.GetCurrentDirectory();
		if (!File.Exists(Path.Combine(currentDir, MetadataDB)))
		{
			AnsiConsole.MarkupLine($"[red]{currentDir}[/] 不是 calibre 电子书目录");
			return 0;
		}
		ConfigHolder<BackupConfig> configHolder = new(Path.Combine(currentDir, BackupConfigFile));
		BackupConfig config = configHolder.Config;
		if (!string.IsNullOrEmpty(settings.Dir))
		{
			config.Dir = NormalizeDir(settings.Dir);
			configHolder.Save();
		}
		if (string.IsNullOrEmpty(config.Dir))
		{
			// 不指定备份目录容易误覆盖，这里总是询问。
			config.Dir = NormalizeDir(AnsiConsole.Ask<string>("请输入网盘的备份目录:"));
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
			AnsiConsole.MarkupLine($"准备将 [green]{currentDir}[/] 备份至 [green]{config.Dir}[/]");
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
			data.Add(pair.Value.GetDataRow(prefix + pair.Key));
		}
		if (File.Exists(filePath))
		{
			File.Delete(filePath);
		}
		File.WriteAllLines(filePath, data);
		AnsiConsole.MarkupLine($"已将 [green]{data.Count - 1}[/] 条数据保存至 [green]{filePath}[/]");
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
			MD5 = CalculateFileMD5(path);
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
		/// 网盘文件列表。
		/// </summary>
		private List<BaiduPanFileInfo> cloudFiles = [];

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
			// 提前获取云盘文件列表。
			cloudFiles = await cloud.List(cloudDir);

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
					// 总是将 metadata.db 备份为 metadata_backup.db，确保不会压缩失败
					string metaPath = Path.Combine(dir, MetadataDB);
					string metaBackupPath = Path.Combine(dir, MetadataBackupDB);
					using (FileStream stream = File.Open(metaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
					{
						using FileStream outStream = File.OpenWrite(metaBackupPath);
						await stream.CopyToAsync(outStream);
					}
					// 配置使用固定的 id。
					string id = "config";
					var item = GetBackupConfigItem(id, "", "config & metadata");
					BackupTask task = new(id, item, dir);
					BackupFileGroup group = new();
					group.Files.Add(Path.Combine(dir, BackupConfigFile));
					group.Files.Add(metaBackupPath);
					group.Files.Add(Path.Combine(dir, MetadataDBPrefsBackup));
					await task.Backup(this, group, (type, progress) =>
					{
						string message = $"    {type} config";
						if (progress.Length > 0)
						{
							message += " " + progress;
						}
						ctx.Status(message);
					});
					// 上传完毕，保存配置。
					configHolder.Save();
				});
			}
		}

		/// <summary>
		/// 返回指定名称的云盘文件。
		/// </summary>
		public BaiduPanFileInfo? GetCloudFile(string name)
		{
			return cloudFiles.Find((file) => file.Name == name);
		}

		/// <summary>
		/// 上传临时文件。
		/// </summary>
		public async Task Upload(string name, Action<float>? progressCallback = null)
		{
			if (cloudFiles.Find((file) => file.Name == name) != null)
			{
				// 先删除同名文件。
				await cloud.Delete(cloudDir + name);
			}
			// 再上传。
			await cloud.Upload(cloudDir + name, backupTempPath, progressCallback);
		}

		/// <summary>
		/// 移除指定文件。
		/// </summary>
		public Task Delete(string name)
		{
			return cloud.Delete(cloudDir + name);
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
			try
			{
				var isBackup = await AnsiConsole.Status().StartAsync($"    扫描 {escapedName}", async ctx =>
				{
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
						BackupFileGroup group = new();
						group.Files.AddRange(files);
						groups = new() { group };
					}
					bool isBackup = false;
					foreach (var group in groups)
					{
						isBackup |= await task.Backup(this, group, (type, progress) =>
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
					if (isBackup || task.NeedSave)
					{
						item.Files = task.Files;
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
				AnsiConsole.MarkupLine($"    [red]备份 {escapedName} 失败[/]: {e.Message}");
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
		/// <list>
		/// <item>metadata.opf 和 cover.jpg 作为元数据 {id}_meta</item>
		/// <item>data 作为 {id}_data</item>
		/// <item>其它分组为 {id}_{ext}</item>
		/// </list>
		/// </remarks>
		private static List<BackupFileGroup> GroupFiles(ICollection<string> files, int dirLen)
		{
			// 按照文件类型分组
			List<BackupFileGroup> groups = new();
			foreach (string filePath in files)
			{
				var fileName = filePath[dirLen..];
				string groupName;
				if (fileName == "metadata.opf" || fileName == "cover.jpg")
				{
					groupName = "meta";
				}
				else if (fileName.StartsWith("data") && fileName[4] == Path.DirectorySeparatorChar)
				{
					groupName = "data";
				}
				else
				{
					// 移除后缀的 .
					groupName = Path.GetExtension(fileName)[1..].ToLowerInvariant();
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
					Pwd = RandomString(7, 10, PwdChars),
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
		/// 备份指定的分组。
		/// </summary>
		public async Task<bool> Backup(BackupContext context, BackupFileGroup group, Action<string, string>? progressCallback = null)
		{
			string cloudFileName = group.Name == null ? id : $"{id}_{group.Name}";
			// 要先检查 group 中文件是否发生改变，确保能够保存新的文件列表。
			// 如果找不到云盘文件也要重新上传。
			var cloudFile = context.GetCloudFile(cloudFileName);
			if (!IsGroupFilesChanged(group) && cloudFile != null)
			{
				size += cloudFile.Size;
				return false;
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
			await Compress(config, (progress) =>
			{
				progressCallback?.Invoke($"压缩 {groupName}", progress);
			});
			size += new FileInfo(backupTempPath).Length;
			// 检查云盘是否具有同名文件。
			progressCallback?.Invoke($"上传 {groupName}", "");
			// 再上传。
			await context.Upload(cloudFileName, (progress) =>
			{
				progressCallback?.Invoke($"上传 {groupName}", progress.ToString("0.##") + "%");
			});
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
				string cloudFileName = groupName == null ? id : $"{id}_{groupName}";
				if (context.GetCloudFile(cloudFileName) != null)
				{
					await context.Delete(cloudFileName);
					AnsiConsole.MarkupLine($"    已清除过期备份 {cloudFileName}");
				}
			}
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
				string fileMD5 = CalculateFileMD5(file);
				if (fileMD5 == info.MD5)
				{
					// MD5 相同，回写修改时间。
					info.LastModified = fileLastModified;
					needSave = true;
				}
				else
				{
					isChanged = true;
				}
				files.Add(name, info);
			}
			return isChanged;
		}
	}
}
