/**
 * 打包 epub，支持文本（小说）或只包含图片的目录（漫画）。
 */
#load "utils/7z.csx"
#load "utils/calibre.csx"
#load "utils/compress.csx"
#load "utils/config-holder.csx"
#load "utils/epub/exporter.csx"
#load "utils/file.csx"
#load "utils/task.csx"
#r "nuget: Spectre.Console, 0.54.0"
#r "nuget: Spectre.Console.Cli, 0.53.1"

#nullable enable

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using System.Threading;
using Spectre.Console;
using Spectre.Console.Cli;
using Cyjb.Markdown;
using Cyjb.Markdown.Syntax;
using Paragraph = Cyjb.Markdown.Syntax.Paragraph;

var app = new CommandApp<PackCommand>();
return await app.RunAsync(Args.ToArray());

/// <summary>
/// 打包命令。
/// </summary>
sealed class PackCommand : AsyncCommand<PackCommand.Settings>
{
	/// <summary>
	/// 命令的设置。
	/// </summary>
	public sealed class Settings : CommandSettings
	{
		[Description("要处理的文件目录，默认为当前目录。")]
		[CommandArgument(0, "[path]")]
		public string? Path { get; init; }

		[Description("静默打包，不询问作者和标题。")]
		[CommandOption("-s|--silent ")]
		[DefaultValue(false)]
		public bool Silent { get; init; }

		[Description("压缩漫画，减少 epub 尺寸；原图片会压缩为 7z。")]
		[CommandOption("-c|--compress ")]
		[DefaultValue(false)]
		public bool Compress { get; init; }

		[Description("将 epub 直接添加到指定 calibre 库，支持本地数据库路径，或内容服务器地址。")]
		[CommandOption("--calibre-library")]
		public string? CalibreLibrary { get; init; }

		[Description("自动添加到之前指定的 Calibre 数据库中。")]
		[CommandOption("-a|--auto-add ")]
		[DefaultValue(false)]
		public bool AutoAdd { get; init; }
	}

	/// <summary>
	/// 触发图片压缩的阈值，10MiB。
	/// </summary>
	private const long CompressThreshold = 10 * 1024 * 1024;
	/// <summary>
	/// 书籍名的正则表达式 [{author}] {title}
	/// </summary>
	private static readonly Regex FileNameRegex = new(@"\[(
		(?>
			[^\]\[]+
			| \[ (?<Depth>)
			| \] (?<-Depth>)
		)+
		(?(Depth)(?!))
	)\]([ \[].+)", RegexOptions.IgnorePatternWhitespace);
	/// <summary>
	/// 作者国籍的正则表达式。
	/// </summary>
	private static readonly Regex AuthorCountryRegex = new(@"^\[.\]");
	/// <summary>
	/// 配置。
	/// </summary>
	private static ConfigHolder<Config> configHolder = new();
	/// <summary>
	/// 作者映射表。
	/// </summary>
	private static Dictionary<string, string> authorMap = [];

	/// <summary>
	/// 执行命令。
	/// </summary>
	public override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
	{
		var targetPath = settings.Path ?? Directory.GetCurrentDirectory();
		Calibre? calibre = null;
		if (settings.CalibreLibrary != null)
		{
			calibre = new Calibre(settings.CalibreLibrary);
			configHolder.Config.CalibreLibrary = settings.CalibreLibrary;
			configHolder.Save();
		}
		else if (settings.AutoAdd && configHolder.Config.CalibreLibrary != null)
		{
			calibre = new Calibre(configHolder.Config.CalibreLibrary);
		}
		var silent = settings.Silent;
		var compress = settings.Compress;
		List<PackTask> tasks = [];
		DetectTasks(targetPath, tasks);
		if (tasks.Count == 0)
		{
			AnsiConsole.MarkupLine($"[red]无效的路径 {targetPath}[/]");
		}
		else
		{
			foreach (var task in tasks)
			{
				var path = task.Path;
				AnsiConsole.MarkupLine($"正在打包 [green]{path.EscapeMarkup()}[/]:");
				var (author, title) = DetectBookInfo(Path.GetFileName(path), silent);
				string epubPath;
				if (title == "" && author == "")
				{
					epubPath = path + ".epub";
				}
				else
				{
					epubPath = Path.Combine(Path.GetDirectoryName(path) ?? "", $"[{author}] {title}.epub");
				}
				EPubExporter exporter = new(epubPath, title, author);
				if (task.Type == PackType.Comic)
				{
					await PackComic(task.Path, exporter, compress, calibre);
				}
				else
				{
					// TODO
				}
			}
		}
		return 0;
	}

	/// <summary>
	/// 检测指定目录内的打包任务。
	/// </summary>
	private static void DetectTasks(string path, List<PackTask> tasks)
	{
		if (Directory.Exists(path))
		{
			// 是目录，检查内容
			var entries = Directory.GetFileSystemEntries(path);
			List<string> dirs = [];
			List<string> novels = [];
			int imageCount = 0;
			foreach (var entry in entries)
			{
				if (Directory.Exists(entry))
				{
					dirs.Add(entry);
				}
				else if (File.Exists(entry))
				{
					var ext = Path.GetExtension(entry).ToLower();
					if (ImageExt.Contains(ext))
					{
						imageCount++;
					}
					else if (ext == ".txt")
					{
						novels.Add(entry);
					}
				}
			}
			if (novels.Count > 0)
			{
				// 包含小说内容，只打包小说。
				foreach (var subPath in novels)
				{
					tasks.Add(new PackTask(subPath, PackType.Novel));
				}
			}
			else if (imageCount > 0 && dirs.Count == 0)
			{
				// 只包含图片，认为是打包漫画。
				tasks.Add(new PackTask(path, PackType.Comic));
			}
			else if (dirs.Count > 0)
			{
				// 只包含子目录，依次检查。
				foreach (var subPath in dirs)
				{
					DetectTasks(subPath, tasks);
				}
			}
		}
		else if (File.Exists(path))
		{
			// 是文件，检查格式。
			tasks.Add(new PackTask(path, PackType.Novel));
		}
	}

	/// <summary>
	/// 检测书籍信息。
	/// </summary>
	private static (string author, string title) DetectBookInfo(string fileName, bool silent)
	{
		string title = "", author = "";
		var match = FileNameRegex.Match(fileName);
		if (match.Success)
		{
			author = match.Groups[1].Value;
			title = match.Groups[2].Value.Trim();
		}
		if (!silent)
		{
			// 临时绕过
			// https://github.com/spectreconsole/spectre.console/issues/1181
			var originValue = title;
			var escapedValue = title.EscapeMarkup();
			var titlePrompt = new TextPrompt<string>("  请输入标题:").DefaultValue(escapedValue);
			title = AnsiConsole.Prompt(titlePrompt);
			if (title == escapedValue)
			{
				title = originValue;
			}
			originValue = author;
			escapedValue = author.EscapeMarkup();
			var authorPrompt = new TextPrompt<string>("  请输入作者:").DefaultValue(escapedValue);
			author = AnsiConsole.Prompt(authorPrompt);
			if (author == escapedValue)
			{
				author = originValue;
			}
		}
		return (author, title);
	}

	/// <summary>
	/// 打包漫画。
	/// </summary>
	private async Task PackComic(string path, EPubExporter exporter, bool compress, Calibre? calibre)
	{
		List<string> generatedFiles = [exporter.FilePath];
		string[] files = Directory.GetFiles(path);
		if (compress)
		{
			long fileSize = 0;
			foreach (var file in files)
			{
				fileSize += new FileInfo(file).Length;
			}
			List<Func<TaskContext, Task>> tasks;
			if (fileSize > CompressThreshold)
			{
				tasks = await GetCompressTasksAsync(path, null, new CompressConfig()
				{
					// 尺寸要超出 20% 才触发压缩。
					Oversize = 0.2,
				});
			}
			else
			{
				tasks = [];
			}
			if (tasks.Count > 0)
			{
				// 将图片备份为 7z。
				var sevenZPath = Path.ChangeExtension(exporter.FilePath, ".7z");
				generatedFiles.Add(sevenZPath);
				SevenZConfig config = new()
				{
					FileName = sevenZPath,
					WorkingDirectory = Path.GetDirectoryName(path)!,
					Files = files,
				};
				await AnsiConsole.Status().StartAsync($"备份原图 ...", ctx => Compress(config, (progress) =>
				{
					ctx.Status($"备份原图 {progress}");
				}));
				await RunTaskAsync("压缩图片", tasks);
				// 压缩完重新提取文件。
				files = Directory.GetFiles(path);
			}
		}
		await AnsiConsole.Status().StartAsync($"生成 epub...", async ctx =>
		{
			// 对文件排序，00 或 cover 在最前面。
			files.Sort((left, right) =>
			{
				bool leftCover = IsCoverFile(left);
				bool rightCover = IsCoverFile(right);
				if (leftCover == rightCover)
				{
					return left.CompareTo(right);
				}
				else if (leftCover)
				{
					return -1;
				}
				else
				{
					return 1;
				}
			});
			bool hasCover = false;
			foreach (var file in files)
			{
				string chapterId = Path.GetFileNameWithoutExtension(file);
				string? chapterTitle;
				bool isCover = IsCoverFile(file);
				// 使用空格分割 id 和 title。
				int idx = chapterId.IndexOf(' ');
				if (idx > 0)
				{
					// 总是保留 title 中的序号。
					chapterTitle = chapterId;
					chapterId = chapterId[0..idx];
				}
				else if (isCover)
				{
					// 不重复设置封面的标题。
					if (hasCover)
					{
						chapterTitle = null;
					}
					else
					{
						chapterTitle = "封面";
					}
				}
				else
				{
					chapterTitle = chapterId;
				}
				Document doc = new();
				Paragraph paragraph = new();
				doc.Children.Add(paragraph);
				Link img = new(true, file, chapterTitle);
				img.Attributes.Add("id", chapterId);
				if (isCover && !hasCover)
				{
					// 只使用首个封面。
					img.Attributes.Add("cover", "");
				}
				paragraph.Children.Add(img);
				exporter.AddPage(chapterId, chapterTitle, doc);
				if (isCover)
				{
					hasCover = true;
				}
			}
			exporter.Finish();
		});
		if (calibre != null)
		{
			var id = await calibre.Add(generatedFiles, new CalibreBookOptions()
			{
				Title = exporter.Title,
				Authors = exporter.Author,
				Automerge = "new_record",
			});
			AnsiConsole.MarkupLine($"已添加 [green]{id}[/]");
		}
	}

	/// <summary>
	/// 配置。
	/// </summary>
	private class Config
	{
		/// <summary>
		/// Calibre 数据库。
		/// </summary>
		public string? CalibreLibrary { get; set; }
	}


	/// <summary>
	/// 打包类型。
	/// </summary>
	private enum PackType
	{
		Comic,
		Novel,
	}

	/// <summary>
	/// 打包任务。
	/// </summary>
	private class PackTask(string path, PackType type)
	{
		/// <summary>
		/// 打包路径。
		/// </summary>
		public string Path = path;
		/// <summary>
		/// 打包类型。
		/// </summary>
		public PackType Type = type;
	}
}
