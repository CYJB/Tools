/**
 * 备份 calibre 书库。
 */

#load "utils/baidu-pan.csx"
#load "utils/config-holder.csx"
#load "utils/md5.csx"
#load "utils/string.csx"
#r "nuget: Spectre.Console, 0.48.0"
#r "nuget: Spectre.Console.Cli, 0.48.0"
#r "nuget: System.Text.Encoding.CodePages, 10.0.5"

#nullable enable

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Cli;

// 注册 GBK 编码支持。
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

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
	/// 备份的临时文件名。
	/// </summary>
	private const string BackupTempFile = ".backup-temp.7z";
	/// <summary>
	/// calibre 的元数据文件名。
	/// </summary>
	private readonly string[] CalibreMetadata = [
		"metadata.db",
		"metadata_db_prefs_backup.json",
	];
	/// <summary>
	/// 密码的字符范围。
	/// </summary>
	private const string PwdChars = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
	/// <summary>
	/// 匹配书籍名的正则表达式。
	/// </summary>
	private static readonly Regex BookNameRegex = new(@"(.+)\s+\((\d+)\)$");
	/// <summary>
	/// 7z 压缩进度的正则表达式。
	/// </summary>
	private static readonly Regex SevenZProgressRegex = new(@"^(\d+%)");
	/// <summary>
	/// 备份配置。
	/// </summary>
	public required ConfigHolder<BackupConfig> configHolder;
	/// <summary>
	/// 备份的临时文件路径。
	/// </summary>
	private string backupTempPath = "";
	/// <summary>
	/// 网盘操作。
	/// </summary>
	private readonly BaiduPan cloud = new();
	/// <summary>
	/// 网盘的存储路径。
	/// </summary>
	private string cloudDir = "/";
	/// <summary>
	/// 网盘文件列表。
	/// </summary>
	private List<BaiduPanFileInfo> cloudFiles = [];

	/// <summary>
	/// 执行命令。
	/// </summary>
	public override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings)
	{
		var currentDir = settings.Path ?? Directory.GetCurrentDirectory();
		configHolder = new ConfigHolder<BackupConfig>(Path.Combine(currentDir, BackupConfigFile));
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
			// 将数据备份到云盘。
			cloudDir = config.Dir!;
			AnsiConsole.MarkupLine($"准备将 [green]{currentDir}[/] 备份至 [green]{cloudDir}[/]");
			// 提前获取云盘文件列表。
			backupTempPath = Path.Combine(currentDir, BackupTempFile);
			cloudFiles = await cloud.List(cloudDir);
			await BackupCloud(currentDir);
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
	/// 备份到云盘。
	/// </summary>
	private async Task BackupCloud(string dir)
	{
		bool isBackup = false;
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
				isBackup = true;
			}
		}
		if (isBackup)
		{
			// 发生了备份操作，备份配置本身。
			await AnsiConsole.Status().StartAsync($"    备份配置", async ctx =>
			{
				// 配置使用固定的 id。
				string id = "config";
				var item = GetBackupConfigItem(id, "", "config & metadata");
				// 注意包含最后的 \
				var dirLen = dir.Length;
				if (!dir.EndsWith('/') && !dir.EndsWith('\\'))
				{
					dirLen++;
				}
				// config 内容一定发生改变了。
				List<string> files = [Path.Combine(dir, BackupConfigFile)];
				foreach (string name in CalibreMetadata)
				{
					var file = Path.Combine(dir, name);
					if (File.Exists(file))
					{
						files.Add(file);
					}
				}
				await BackupFiles(id, item, files, dirLen, dir, (type, progress) =>
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
				if (await BackupBook(id, author, name, Path.Combine(dir, fileName)))
				{
					isBackup = true;
				}
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
				// 注意包含最后的 \
				var dirLen = dir.Length + 1;
				var files = Directory.GetFiles(dir, "", SearchOption.AllDirectories);
				if (!IsFilesChanged(item.Files, files, dirLen, out var needSave))
				{
					// 虽然内容未发生改变，但文件修改时间发生了改变。
					if (needSave)
					{
						configHolder.Save();
					}
					return false;
				}
				await BackupFiles(id, item, files, dirLen, dir, (type, progress) =>
				{
					string message = $"    {type} {escapedName}";
					if (progress.Length > 0)
					{
						message += " " + progress;
					}
					ctx.Status(message);
				});
				// 上传完毕，保存配置。
				configHolder.Save();
				return true;
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
		}
		return false;
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

	/// <summary>
	/// 备份指定的文件列表。
	/// </summary>
	private async Task BackupFiles(string id, BackupConfigItem item, ICollection<string> files,
		int dirLen, string dir, Action<string, string>? progressCallback = null)
	{
		progressCallback?.Invoke("压缩", "");
		// 移除旧临时文件。
		if (File.Exists(backupTempPath))
		{
			File.Delete(backupTempPath);
		}
		var args = new List<string>
		{
			"a", // 添加文件到压缩包
			"-bsp1", // 输出压缩进度
			"-mhe", // 加密文件名
			$"-p{item.Pwd}", // 设置密码
			backupTempPath,
		};
		args.AddRange(files);
		await Compress(args, dir, (progress) =>
		{
			progressCallback?.Invoke("压缩", progress);
		});
		item.Size = new FileInfo(backupTempPath).Length;
		// 检查云盘是否具有同名文件。
		progressCallback?.Invoke("上传", "");
		int idx = cloudFiles.FindIndex((file) => file.Name == id);
		if (idx >= 0)
		{
			// 先删除同名文件。
			await cloud.Delete(cloudDir + id);
		}
		// 再上传。
		await cloud.Upload(cloudDir + id, backupTempPath, (progress) =>
		{
			progressCallback?.Invoke("上传", progress.ToString("0.##") + "%");
		});
		// 移除临时文件。
		File.Delete(backupTempPath);
		// 最后设置文件列表，避免中途失败时被误标记已上传。
		item.Files = GetFiles(files, dirLen);
	}

	/// <summary>
	/// 检查指定目录是否发生了改变。
	/// </summary>
	private static bool IsFilesChanged(Dictionary<string, BackupFileInfo> curFiles, string[] files, int dirLen, out bool needSave)
	{
		needSave = false;
		if (curFiles.Count != files.Length)
		{
			return true;
		}
		foreach (var filePath in files)
		{
			var name = filePath[dirLen..];
			if (!curFiles.TryGetValue(name, out var info))
			{
				return true;
			}
			var fileInfo = new FileInfo(filePath);
			var fileLastModified = new DateTimeOffset(fileInfo.LastWriteTime).ToUnixTimeMilliseconds();
			if (info.LastModified == fileLastModified)
			{
				// 修改时间未发生改变，认为文件是相同的。
				continue;
			}
			// 比较 MD5。
			string fileMD5 = CalculateFileMD5(filePath);
			if (fileMD5 != info.MD5)
			{
				return true;
			}
			// MD5 相同，回写修改时间。
			info.LastModified = fileLastModified;
			needSave = true;
		}
		return false;
	}

	/// <summary>
	/// 获取指定目录下的文件信息。
	/// </summary>
	private static Dictionary<string, BackupFileInfo> GetFiles(ICollection<string> files, int dirLen)
	{
		Dictionary<string, BackupFileInfo> result = new(files.Count);
		foreach (var filePath in files)
		{
			var name = filePath[dirLen..];
			var fileInfo = new FileInfo(filePath);
			var fileLastModified = new DateTimeOffset(fileInfo.LastWriteTime).ToUnixTimeMilliseconds();
			var fileMD5 = CalculateFileMD5(filePath);
			result.Add(name, new BackupFileInfo()
			{
				LastModified = fileLastModified,
				MD5 = fileMD5,
			});
		}
		return result;
	}

	/// <summary>
	/// 使用指定的参数执行 7z 指令。
	/// </summary>
	private async Task Compress(List<string> args, string workingDirectory, Action<string> progressCallback)
	{
		using var process = new Process();
		process.StartInfo = new ProcessStartInfo("7z", args)
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			// 避免乱码
			StandardOutputEncoding = Encoding.GetEncoding("GBK"),
			StandardErrorEncoding = Encoding.GetEncoding("GBK"),
		};

		var errorBuilder = new StringBuilder();
		process.OutputDataReceived += (sender, e) =>
		{
			if (e.Data != null)
			{
				var match = SevenZProgressRegex.Match(e.Data.Trim());
				if (match.Success)
				{
					progressCallback(match.Value);
				}
			}
		};
		process.ErrorDataReceived += (sender, e) =>
		{
			if (e.Data != null)
			{
				errorBuilder.AppendLine(e.Data);
			}
		};
		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		await process.WaitForExitAsync();
		if (process.ExitCode != 0)
		{
			throw new Exception($"压缩失败: {process.ExitCode}, {errorBuilder}");
		}
	}
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

private class BackupFileInfo
{
	/// <summary>
	/// 文件的修改时间。
	/// </summary>
	public long LastModified { get; set; }
	/// <summary>
	/// 文件的 MD5。
	/// </summary>
	public required string MD5 { get; set; }
}
