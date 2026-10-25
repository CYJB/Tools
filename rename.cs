#!/usr/bin/env -S dotnet --

/**
 * 重命名当前目录（及子目录）的图片或视频。
 */
// 这里只 include 直接依赖的模块，它们各自会 include 自己的依赖。
// utils/image.cs、utils/video.cs 里还有 MagickFormat、ffmpeg 路径设置，所以这两个包也要声明。
#:include utils/image.cs
#:include utils/video.cs
#:package Cyjb
#:package Magick.NET.Core
#:package Spectre.Console
#:package Spectre.Console.Cli
#:package Xabe.FFmpeg
// 发布成单文件 exe：`dotnet publish rename.cs` 只产出一个 rename.exe。
// 采用依赖框架（framework-dependent）的方式，体积只有几 MB，运行时用本机安装的 .NET 10。
#:property PublishAot=false
#:property PublishSingleFile=true
#:property SelfContained=false
#:property RuntimeIdentifier=win-x64
#:property DebugType=embedded
#nullable enable

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Cyjb;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp<RenameCommand>();
return app.Run(args);

/// <summary>
/// 重命名命令。
/// </summary>
sealed class RenameCommand : Command<RenameCommand.Settings>
{
	/// <summary>
	/// 命令的设置。
	/// </summary>
	public sealed class Settings : CommandSettings
	{
		[Description("要处理的文件目录，默认为当前目录")]
		[CommandArgument(0, "[path]")]
		public string? Path { get; init; }

		[Description("是否递归处理子目录")]
		[CommandOption("-r|--recursion")]
		[DefaultValue(false)]
		public bool Recursion { get; init; }

		[Description("过滤文件名(正则表达式)")]
		[CommandOption("-f|--filter")]
		public string? Filter { get; init; }

		[Description("重命名后的前缀")]
		[CommandOption("-p|--prefix")]
		public string? Prefix { get; init; }

		[Description("是否将文件数添加到文件夹名后")]
		[CommandOption("-c|--count-file")]
		[DefaultValue(false)]
		public bool CountFile { get; init; }

		[Description("是否将文件总大小添加到文件夹名后")]
		[CommandOption("-s|--sum-size")]
		[DefaultValue(false)]
		public bool SumSize { get; init; }
	}

	/// <summary>
	/// 文件数和文件大小的正则表达式。
	/// </summary>
	private static readonly Regex CountSizeRegex = new(@"\s*\[(\d+P)?[-+]?(\d+V)?(\s*-?\s*[0-9.]*\s*[KMGB]+)?\]$", RegexOptions.IgnoreCase);

	/// <summary>
	/// 过滤文件名的正则表达式。
	/// </summary>
	private Regex? filterRegex;
	/// <summary>
	/// 是否递归遍历子文件夹。
	/// </summary>
	private bool recursion = false;
	/// <summary>
	/// 是否将文件数添加到文件夹名后。
	/// </summary>
	private bool countFile = false;
	/// <summary>
	/// 是否将文件总大小添加到文件夹名后。
	/// </summary>
	private bool sumSize = false;
	/// <summary>
	/// 重命名后的前缀。
	/// </summary>
	private string? prefix;
	/// <summary>
	/// 要重命名的文件列表。
	/// </summary>
	private List<RenameItem> list = new();

	/// <summary>
	/// 执行命令。
	/// </summary>
	protected override int Execute([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
	{
		var path = settings.Path ?? Directory.GetCurrentDirectory();
		if (settings.Filter != null)
		{
			filterRegex = new Regex(settings.Filter, RegexOptions.Compiled | RegexOptions.IgnoreCase);
		}
		recursion = settings.Recursion;
		countFile = settings.CountFile;
		sumSize = settings.SumSize;
		prefix = settings.Prefix;
		GetRenameList(path);
		string? dir = null;
		foreach (RenameItem item in list)
		{
			if (dir != item.Directory)
			{
				AnsiConsole.MarkupLine($"[navy]{item.Directory.EscapeMarkup()}[/]");
				dir = item.Directory;
			}
			AnsiConsole.MarkupLine($"[grey]> {item.OriginName.EscapeMarkup()} → [/]{item.TargetName.EscapeMarkup()}");
		}
		if (list.Count == 0)
		{
			Console.WriteLine("没有需要重命名的文件");
			return 0;
		}
		if (AnsiConsole.Confirm("是否重命名文件？"))
		{
			foreach (RenameItem item in list)
			{
				string originPath = Path.Combine(item.Directory, item.OriginName);
				string targetPath = Path.Combine(item.Directory, item.TargetName!);
				if (item.IsDirectory)
				{
					Directory.Move(originPath, targetPath);
				}
				else
				{
					File.Move(originPath, targetPath);
				}
			}
			Console.WriteLine("重命名完毕");
		}
		return 0;
	}

	/// <summary>
	/// 返回要重命名的文件列表。
	/// </summary>
	private RenameSummary GetRenameList(string directory)
	{
		int imageCount = 0;
		int videoCount = 0;
		long fileSumSize = 0;
		if (recursion)
		{
			foreach (string path in Directory.GetDirectories(directory))
			{
				RenameSummary summary = GetRenameList(path);
				imageCount += summary.ImageCount;
				videoCount += summary.VideoCount;
				fileSumSize += summary.Size;
			}
		}
		// 缓存为数组避免重复创建 RenameItem。
		IEnumerable<RenameItem> items = Directory.GetFiles(directory)
			.Select(path => new RenameItem(path, false))
			.OrderBy(item => item)
			.ToArray();
		// 重命名图片
		RenameItem[] images = items.Where(item => ImageUtils.Ext.Contains(item.Ext)).ToArray();
		imageCount += images.Length;
		Rename(images, prefix);
		// 重命名视频。
		RenameItem[] videos = items.Where(item => VideoUtils.Ext.Contains(item.Ext)).ToArray();
		videoCount += videos.Length;
		Rename(videos, prefix == null ? "video" : prefix);
		// 检查是否需要重命名文件夹。
		if ((countFile || sumSize) && images.Length + videos.Length > 0)
		{
			// 移除文件夹名后可能已有的文件数和大小。
			string dirName = CountSizeRegex.Replace(Path.GetFileName(directory), "");
			RenameItem item = new(directory, true);
			string countSizeSuffix = "";
			if (countFile)
			{
				List<string> fileCount = new();
				if (imageCount > 0)
				{
					fileCount.Add($"{imageCount}P");
				}
				if (videoCount > 0)
				{
					fileCount.Add($"{videoCount}V");
				}
				countSizeSuffix = $"{string.Join('+', fileCount)}";
			}
			if (sumSize)
			{
				if (countSizeSuffix.Length > 0)
				{
					countSizeSuffix += "-";
				}
				foreach (RenameItem sizeItem in images)
				{
					fileSumSize += new FileInfo(sizeItem.OriginPath).Length;
				}
				foreach (RenameItem sizeItem in videos)
				{
					fileSumSize += new FileInfo(sizeItem.OriginPath).Length;
				}
				string unit = "B";
				double formatSize = fileSumSize;
				if (formatSize > 1000)
				{
					formatSize /= 1000;
					unit = "KB";
				}
				if (formatSize > 1000)
				{
					formatSize /= 1000;
					unit = "MB";
				}
				if (formatSize > 1000)
				{
					formatSize /= 1000;
					// GB 的舍入误差太大，这里保留两位小数。
					countSizeSuffix += formatSize.ToString("0.##GB");
				}
				else
				{
					countSizeSuffix += Math.Ceiling(formatSize).ToString() + unit;
				}
			}
			item.TargetName = $"{dirName} [{countSizeSuffix}]";
			if (item.TargetName != item.OriginName)
			{
				list.Add(item);
			}
		}
		return new RenameSummary(images.Length, videos.Length, fileSumSize);
	}

	/// <summary>
	/// 相机照片文件名的正则表达式（如 IMG_20240101），这类文件不重命名。
	/// </summary>
	private static readonly Regex CameraImageRegex = new(@"^[0-9A-Z_]{4}\d{4}\.", RegexOptions.IgnoreCase);

	/// <summary>
	/// 重命名指定的文件列表。
	/// </summary>
	private void Rename(IEnumerable<RenameItem> items, string? prefix)
	{
		if (filterRegex != null)
		{
			items = items.Where(item => filterRegex.Match(item.OriginName).Success);
		}
		// 过滤掉一些常见的相机命名方式。
		items = items.Where(items => !CameraImageRegex.Match(items.OriginName).Success);
		RenameItem[] filteredItems = items.ToArray();
		int count = filteredItems.Length;
		if (count == 1)
		{
			if (prefix != null)
			{
				// 只有一个文件的时候，可以直接使用 prefix
				RenameItem item = filteredItems[0];
				item.TargetName = prefix + item.Ext;
				if (item.OriginName != item.TargetName)
				{
					list.Add(item);
				}
			}
			return;
		}
		// 至少保留两位数字
		string format = new string('0', (int)Math.Floor(Math.Log10(count + 9)) + 1);
		// 将名字为 0 或包含 cover 的，总是重命名为 0。
		// 先统计封面个数。
		List<RenameItem> covers = new List<RenameItem>();
		for (int i = 0; i < filteredItems.Length; i++)
		{
			RenameItem item = filteredItems[i];
			if (item.GetIsCover())
			{
				covers.Add(item);
			}
		}
		if (covers.Count > 0)
		{
			if (covers.Count == 1)
			{
				RenameItem item = covers[0];
				item.TargetName = 0.ToString(format) + item.Ext;
				if (item.OriginName != item.TargetName)
				{
					list.Add(item);
				}
			}
			else
			{
				for (int i = 0; i < covers.Count; i++)
				{
					RenameItem item = covers[i];
					// 多个封面从 1 开始编号，如 00_1.jpg、00_2.jpg。
					item.TargetName = 0.ToString(format) + "_" + (i + 1).ToString() + item.Ext;
					if (item.OriginName != item.TargetName)
					{
						list.Add(item);
					}
				}
			}
		}
		int idx = 1;
		foreach (RenameItem item in filteredItems)
		{
			if (item.TargetName != null)
			{
				continue;
			}
			string targetName = idx.ToString(format) + item.Ext;
			if (prefix != null)
			{
				targetName = prefix + " " + targetName;
			}
			if (item.OriginName != targetName)
			{
				item.TargetName = targetName;
				list.Add(item);
			}
			idx++;
		}
	}
}

/// <summary>
/// 重命名的统计。
/// </summary>
class RenameSummary
{
	public RenameSummary(int imageCount, int videoCount, long size)
	{
		this.ImageCount = imageCount;
		this.VideoCount = videoCount;
		this.Size = size;
	}
	/// <summary>
	/// 图片数。
	/// </summary>
	public int ImageCount { get; init; }
	/// <summary>
	/// 视频数。
	/// </summary>
	public int VideoCount { get; init; }
	/// <summary>
	/// 总大小。
	/// </summary>
	public long Size { get; init; }
}

/// <summary>
/// 要重命名的项。
/// </summary>
class RenameItem : IComparable<RenameItem>
{
	/// <summary>
	/// 扩展名。
	/// </summary>
	private string? ext;

	public RenameItem(string path, bool isDirectory)
	{
		this.Directory = Path.GetDirectoryName(path)!;
		this.OriginName = Path.GetFileName(path);
		this.IsDirectory = isDirectory;
	}
	/// <summary>
	/// 文件所在目录。
	/// </summary>
	public string Directory { get; init; }
	/// <summary>
	/// 文件的原始名称。
	/// </summary>
	public string OriginName { get; init; }
	/// <summary>
	/// 文件的原始路径。
	/// </summary>
	public string OriginPath
	{
		get
		{
			return Path.Combine(Directory, OriginName);
		}
	}
	/// <summary>
	/// 是否是文件夹。
	/// </summary>
	public bool IsDirectory { get; init; }
	/// <summary>
	/// 文件的目标名称。
	/// </summary>
	public string? TargetName { get; set; }
	/// <summary>
	/// 获取文件的扩展名。
	/// </summary>
	public string Ext
	{
		get
		{
			if (ext == null)
			{
				ext = Path.GetExtension(OriginName).ToLower();
			}
			return ext;
		}
	}
	/// <summary>
	/// 返回是否是封面。
	/// </summary>
	public bool GetIsCover()
	{
		if (OriginName.Contains("cover", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		string originNameWithoutExt = Path.GetFileNameWithoutExtension(OriginName);
		// 支持 0-1.jpg、00_1.jpg 这类场景：按 - 或 _ 分割，前缀是 0 就认为是封面。
		int idx = originNameWithoutExt.AsSpan().IndexOfAny('-', '_');
		if (idx > 0)
		{
			return int.TryParse(originNameWithoutExt[..idx], out int value) && value == 0;
		}
		else
		{
			return int.TryParse(originNameWithoutExt, out int value) && value == 0;
		}
	}
	public int CompareTo(RenameItem? other)
	{
		if (other == null)
		{
			return 1;
		}
		return StringUtil.NaturalCompare(OriginName, other.OriginName);
	}
}
