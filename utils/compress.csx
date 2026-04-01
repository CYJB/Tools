#load "config-holder.csx"
#load "file.csx"
#load "image.csx"
#load "ffmpeg.csx"
#load "task.csx"
#r "nuget: Spectre.Console, 0.54.0"
#r "nuget: Spectre.Console.Cli, 0.53.1"

#nullable enable

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using ImageMagick;
using Spectre.Console;
using Spectre.Console.Cli;
using Xabe.FFmpeg;

/// <summary>
/// 返回多媒体压缩任务列表。
/// </summary>
static async Task<List<Func<TaskContext, Task>>> GetCompressTasksAsync(string path, Action<string>? checkCallback = null,
	CompressConfig? config = null, bool save = false)
{
	SetupFFmpeg();
	CompressContext context = new(config, config != null && save);
	List<Func<TaskContext, Task>> tasks = [];
	EnumerationOptions options = new()
	{
		RecurseSubdirectories = context.Recursion
	};
	// 确保先处理备份文件。
	var files = Directory.EnumerateFiles(path, "*", options)
		.Select(file => CompressContext.ScanFile(file)).ToArray();
	var dicLen = path.Length;
	if (!path.EndsWith(Path.DirectorySeparatorChar))
	{
		dicLen++;
	}
	foreach (string file in files)
	{
		string desc = file[dicLen..];
		desc = desc.EscapeMarkup();
		checkCallback?.Invoke(desc);
		var task = await context.CheckFile(file, desc);
		if (task != null)
		{
			tasks.Add(task);
		}
	}
	return tasks;
}

/// <summary>
/// 压缩多媒体。
/// </summary>
private class CompressContext
{
	/// <summary>
	/// 备份文件的后缀。
	/// </summary>
	private const string backupExtension = ".backup";

	/// <summary>
	/// 压缩配置。
	/// </summary>
	private static readonly ConfigHolder<CompressConfig> configHolder = new();

	/// <summary>
	/// 是否递归遍历子文件夹。
	/// </summary>
	public readonly bool Recursion;
	/// <summary>
	/// 图片尺寸。
	/// </summary>
	private readonly Size imageSize;
	/// <summary>
	/// 视频尺寸。
	/// </summary>
	private readonly Size videoSize;
	/// <summary>
	/// 超出大小的比例。
	/// </summary>
	private readonly double oversize = 0;
	/// <summary>
	/// 码率限制。
	/// </summary>
	private readonly int videoBitRate;
	/// <summary>
	/// 图片后缀。
	/// </summary>
	private string imageExt;
	/// <summary>
	/// 视频后缀。
	/// </summary>
	private string videoExt;

	public CompressContext(CompressConfig? config, bool save)
	{
		CompressConfig defaultConfig = configHolder.Config;
		imageSize = Size.Parse(defaultConfig.ImageSize ?? "2160");
		videoSize = Size.Parse(defaultConfig.VideoSize ?? "1080r");
		videoBitRate = defaultConfig.VideoBitRate ?? 10000000;
		Recursion = defaultConfig.Recursion ?? true;
		ParseExtension(defaultConfig.Extension ?? ".jpg .mp4");
		if (config != null)
		{
			if (config.ImageSize != null)
			{
				var size = Size.Parse(config.ImageSize);
				if (size.IsValid)
				{
					imageSize = size;
					if (save)
					{
						defaultConfig.ImageSize = config.ImageSize;
					}
				}
			}
			if (config.VideoSize != null)
			{
				var size = Size.Parse(config.VideoSize);
				if (size.IsValid)
				{
					videoSize = size;
					if (save)
					{
						defaultConfig.VideoSize = config.VideoSize;
					}
				}
			}
			if (config.VideoBitRate != null && config.VideoBitRate.Value > 0)
			{
				videoBitRate = config.VideoBitRate.Value;
				if (save)
				{
					defaultConfig.VideoBitRate = config.VideoBitRate;
				}
			}
			if (config.Extension != null)
			{
				ParseExtension(config.Extension);
				if (save)
				{
					defaultConfig.Extension = $"{imageExt} {videoExt}";
				}
			}
			if (config.Recursion != null)
			{
				Recursion = config.Recursion.Value;
				if (save)
				{
					defaultConfig.Recursion = config.Recursion;
				}
			}
			if (config.Oversize > 0)
			{
				oversize = config.Oversize;
			}
			if (save)
			{
				configHolder.Save();
			}
		}
	}

	/// <summary>
	/// 解析首选压缩扩展名。
	/// </summary>
	[MemberNotNull(nameof(imageExt), nameof(videoExt))]
#pragma warning disable CS8774
	private void ParseExtension(string extension)
	{
		foreach (string ext in extension.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		{
			if (ImageExt.Contains(ext))
			{
				imageExt = ext;
			}
			else if (VideoExt.Contains(ext))
			{
				videoExt = ext;
			}
		}
	}
#pragma warning restore CS8774

	/// <summary>
	/// 扫描指定文件。
	/// </summary>
	public static string ScanFile(string file)
	{
		// 提前处理备份文件，可能是之前压缩中途失败的情况。
		if (Path.GetExtension(file).Equals(backupExtension, StringComparison.CurrentCultureIgnoreCase))
		{
			string originFile = Path.ChangeExtension(file, null);
			if (File.Exists(originFile))
			{
				// 移除原始文件，并使用备份替换。
				File.Delete(originFile);
				File.Move(file, originFile);
			}
			else
			{
				// 不存在原始文件，直接使用备份替换。
				File.Move(file, originFile);
				// 这里返回原始文件名，确保可以被正确处理。
				return originFile;
			}
		}
		return file;
	}

	/// <summary>
	/// 检查指定的文件。
	/// </summary>
	public async Task<Func<TaskContext, Task>?> CheckFile(string file, string desc)
	{
		var ext = Path.GetExtension(file).ToLower();
		try
		{
			if (ImageExt.Contains(ext))
			{
				// 检查图片是否超出大小限制
				var info = new MagickImageInfo(file);
				int width = (int)info.Width;
				int height = (int)info.Height;
				if (imageSize.NeedCompress(ref width, ref height, oversize))
				{
					return CompressImageAsync(file, desc, width, height);
				}
			}
			else if (VideoExt.Contains(ext))
			{
				// 检查视频是否超出大小限制。
				var mediaInfo = await FFmpeg.GetMediaInfo(file);
				if (mediaInfo.VideoStreams.Any())
				{
					var videoStream = mediaInfo.VideoStreams.First();
					int width = videoStream.Width;
					int height = videoStream.Height;
					// 码率高于指定值（默认 10Mb/s）的也总是触发压缩。
					if (videoSize.NeedCompress(ref width, ref height, oversize, videoStream.Bitrate > videoBitRate))
					{
						return CompressVideoAsync(file, desc, width, height);
					}
				}
			}
		}
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"检查 {desc} 失败: [red]{ex.Message.EscapeMarkup()}[/]");
		}
		return null;
	}

	/// <summary>
	/// 压缩指定的图片。
	/// </summary>
	private Func<TaskContext, Task> CompressImageAsync(string file, string desc, int width, int height)
	{
		return async ctx =>
		{
			ctx.Status($"压缩 {desc}...");
			// 将文件重命名，避免在压缩过程中出错
			string backupFile = file + backupExtension;
			File.Move(file, backupFile);
			using (var image = new MagickImage(backupFile))
			{
				image.Resize((uint)width, (uint)height);
				image.Quality = 85;
				image.Format = GetMagickFormat(imageExt);
				byte[] data = image.ToByteArray();
				File.WriteAllBytes(Path.ChangeExtension(file, imageExt), data);
			}
			// 正常结束，删除备份文件
			await SafeDeleteFile(backupFile);
		};
	}

	/// <summary>
	/// 压缩指定的视频。
	/// </summary>
	private Func<TaskContext, Task> CompressVideoAsync(string file, string desc, int width, int height)
	{
		return async ctx =>
		{
			ctx.Status($"压缩 {desc} ...");
			// 将文件重命名，避免在压缩过程中出错
			string backupFile = file + backupExtension;
			File.Move(file, backupFile);
			var conversion = await FFmpeg.Conversions.FromSnippet.ChangeSize(backupFile, Path.ChangeExtension(file, videoExt), width, height);
			conversion.OnProgress += (sender, args) =>
			{
				int percent = (int)(Math.Round(args.Duration.TotalSeconds / args.TotalLength.TotalSeconds, 2) * 100);
				ctx.Status($"压缩 {desc} [olive]{percent}% {args.Duration}/{args.TotalLength}[/]");
			};
			await conversion.Start(ctx.CancellationToken);
			// 正常结束，删除备份文件
			await SafeDeleteFile(backupFile);
		};
	}
}

public class CompressConfig
{
	/// <summary>
	/// 图片压缩选项。
	/// </summary>
	public string? ImageSize { get; set; }

	/// <summary>
	/// 视频压缩选项。
	/// </summary>
	public string? VideoSize { get; set; }

	/// <summary>
	/// 视频码率限制。
	/// </summary>
	public int? VideoBitRate { get; set; }

	/// <summary>
	/// 首选压缩扩展名。
	/// </summary>
	public string? Extension { get; set; }

	/// <summary>
	/// 是否递归处理子目录。
	/// </summary>
	public bool? Recursion { get; set; }

	/// <summary>
	/// 需要超过目标尺寸比例，高于此比例的才会触发压缩。
	/// </summary>
	public double Oversize { get; set; } = 0;
}

/// <summary>
/// 尺寸信息。
/// </summary>
readonly struct Size
{
	/// <summary>
	/// 最大宽度。
	/// </summary>
	private readonly int maxWidth;
	/// <summary>
	/// 最大高度。
	/// </summary>
	private readonly int maxHeight;
	/// <summary>
	/// 是否允许旋转。
	/// </summary>
	private readonly bool rotate;
	private Size(int maxWidth, int maxHeight, bool rotate)
	{
		this.maxWidth = maxWidth;
		this.maxHeight = maxHeight;
		this.rotate = rotate;
	}

	/// <summary>
	/// 获取尺寸是否是有效的。
	/// </summary>
	public bool IsValid => maxWidth > 0;

	/// <summary>
	/// 解析指定的选项。
	/// </summary>
	public static Size Parse(string option)
	{
		return option switch
		{
			"720" => new Size(1280, 720, false),
			"720r" => new Size(1280, 720, true),
			"1080" => new Size(1920, 1080, false),
			"1080r" => new Size(1920, 1080, true),
			"1440" => new Size(2560, 1440, false),
			"1440r" => new Size(2560, 1440, true),
			"2160" => new Size(3840, 2160, false),
			"2160r" => new Size(3840, 2160, true),
			_ => throw new Exception($"无效的尺寸 {option}"),
		};
	}
	/// <summary>
	/// 检查指定文件的压缩配置。
	/// </summary>
	public bool NeedCompress(ref int width, ref int height, double oversize = 0, bool forceCompress = false)
	{
		int targetWidth = maxWidth;
		int targetHeight = maxHeight;
		if (oversize > 0)
		{
			targetWidth += (int)(maxWidth * oversize);
			targetHeight += (int)(maxHeight * oversize);
		}
		if (width <= targetWidth && height <= targetHeight)
		{
			return forceCompress;
		}
		double scale;
		if (width < height && rotate)
		{
			if (height <= targetWidth && width <= targetHeight)
			{
				return forceCompress;
			}
			else
			{
				scale = Math.Min((double)maxWidth / height, (double)maxHeight / width);
			}
		}
		else
		{
			scale = Math.Min((double)maxWidth / width, (double)maxHeight / height);
		}
		width = RoundToEven(width * scale);
		height = RoundToEven(height * scale);
		return true;
	}

	/// <summary>
	/// 将指定值舍入到偶数。
	/// </summary>
	private static int RoundToEven(double value)
	{
		int result = (int)value;
		if (result == value)
		{
			// 无精度损失，直接返回
			return result;
		}
		else if ((result & 1) == 0)
		{
			// 是偶数，直接返回。
			return result;
		}
		else
		{
			// 是奇数，改为使用接近的偶数。
			return result + 1;
		}
	}
	public override string ToString()
	{
		return $"{maxWidth}×{maxHeight}{(rotate ? "r" : "")}";
	}
}
