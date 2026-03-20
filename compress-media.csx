/**
 * 压缩图片或视频。
 * 压缩功能依赖 https://imagemagick.org/
 */
#load "utils/config-holder.csx"
#load "utils/console.csx"
#load "utils/file.csx"
#load "utils/image.csx"
#load "utils/ffmpeg.csx"
#load "utils/task.csx"
#r "nuget: Spectre.Console, 0.54.0"
#r "nuget: Spectre.Console.Cli, 0.53.1"

#nullable enable

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using ImageMagick;
using Spectre.Console;
using Spectre.Console.Cli;
using Xabe.FFmpeg;

return await RunAsyncWithCancellation<CompressCommand>(Args.ToArray());

/// <summary>
/// 压缩命令。
/// </summary>
sealed class CompressCommand : AsyncCommand<CompressCommand.Settings>
{
	/// <summary>
	/// 命令的设置。
	/// </summary>
	public sealed class Settings : CommandSettings
	{
		private const string CompressOpt = @"
- 0: 不处理
- 720: 压缩到不超过 1280×720
- 720r: 压缩到不超过 1280×720 或 720×1280
- 1080: 压缩到不超过 1920×1080
- 1080r: 压缩到不超过 1920×1080 或 1080×1920
- 1440: 压缩到不超过 2560×1440
- 1440r: 压缩到不超过 2560×1440 或 1440×2560
- 2160: 压缩到不超过 3840×2160
- 2160r: 压缩到不超过 3840×2160 或 2160×3840";

		[Description("要处理的文件目录，默认为当前目录。")]
		[CommandArgument(0, "[path]")]
		public string? Path { get; init; }

		[Description("是否递归处理子目录。")]
		[CommandOption("-r|--recursion")]
		[DefaultValue(true)]
		public bool Recursion { get; init; }

		[Description("图片压缩选项" + CompressOpt)]
		[CommandOption("-i|--image [OPTION]")]
		[DefaultValue("2160")]
		public FlagValue<string> ImageOpt { get; init; } = new();

		[Description("视频压缩选项" + CompressOpt)]
		[CommandOption("-v|--video [OPTION]")]
		[DefaultValue("1080r")]
		public FlagValue<string> VideoOpt { get; init; } = new();

		[Description("视频码率限制，会压缩高于指定码率的视频。")]
		[CommandOption("-b|--bitrate [BITRATE]")]
		[DefaultValue(10000000)]
		public FlagValue<int> BitRate { get; init; } = new();

		[Description("压缩目标后缀，使用空格分隔多个后缀，会自动识别类型。")]
		[CommandOption("-e|--ext [EXTION]")]
		[DefaultValue(".jpg .mp4")]
		public FlagValue<string> Extension { get; init; } = new();
	}

	/// <summary>
	/// 备份文件的后缀。
	/// </summary>
	private const string backupExtension = ".backup";

	/// <summary>
	/// 压缩配置。
	/// </summary>
	private static readonly ConfigHolder<Config> configHolder = new();

	/// <summary>
	/// 是否递归遍历子文件夹。
	/// </summary>
	private bool recursion;
	/// <summary>
	/// 图片尺寸。
	/// </summary>
	private Size imageSize;
	/// <summary>
	/// 视频尺寸。
	/// </summary>
	private Size videoSize;
	/// <summary>
	/// 码率限制。
	/// </summary>
	private int bitRate = 10000000;
	/// <summary>
	/// 图片后缀。
	/// </summary>
	private string imageExt = ".jpg";
	/// <summary>
	/// 视频后缀。
	/// </summary>
	private string videoExt = ".mp4";

	/// <summary>
	/// 执行命令。
	/// </summary>
	public override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
	{
		var currentDir = settings.Path ?? Directory.GetCurrentDirectory();
		ParseOptions(settings);
		if (videoSize.IsValid)
		{
			SetupFFmpeg();
		}
		List<Func<TaskContext, Task>> tasks = [];
		await AnsiConsole.Status().StartAsync($"扫描目录...", async ctx =>
		{
			EnumerationOptions options = new()
			{
				RecurseSubdirectories = settings.Recursion
			};
			// 确保先处理备份文件。
			var files = Directory.EnumerateFiles(currentDir, "*", options)
				.Select(file => ScanFile(file)).ToArray();
			var dicLen = currentDir.Length;
			if (!currentDir.EndsWith(Path.DirectorySeparatorChar))
			{
				dicLen++;
			}
			foreach (string file in files)
			{
				string desc = file[dicLen..];
				desc = desc.EscapeMarkup();
				ctx.Status($"检查文件 {desc}...");
				var task = await CheckFile(file, desc);
				if (task != null)
				{
					tasks.Add(task);
				}
			}
		});
		if (tasks.Count == 0)
		{
			AnsiConsole.MarkupLine("[green]没有要压缩的文件[/]");
			return 0;
		}
		await RunTaskAsync(tasks, cancellationToken);
		return 0;
	}

	private void ParseOptions(Settings settings)
	{
		recursion = settings.Recursion;
		var config = configHolder.Config;
		bool needSave = false;
		var imageOpt = "2160";
		if (settings.ImageOpt.IsSet)
		{
			imageOpt = settings.ImageOpt.Value;
			configHolder.Config.ImageOpt = imageOpt;
			needSave = true;
		}
		else if (config.ImageOpt != null)
		{
			imageOpt = config.ImageOpt;
		}
		imageSize = Size.Parse(imageOpt);
		var videoOpt = "1080r";
		if (settings.VideoOpt.IsSet)
		{
			videoOpt = settings.VideoOpt.Value;
			configHolder.Config.VideoOpt = videoOpt;
			needSave = true;
		}
		else if (config.VideoOpt != null)
		{
			videoOpt = config.VideoOpt;
		}
		videoSize = Size.Parse(videoOpt);
		if (settings.BitRate.IsSet)
		{
			bitRate = settings.BitRate.Value;
			configHolder.Config.BitRate = bitRate;
			needSave = true;
		}
		else if (config.BitRate != null)
		{
			bitRate = config.BitRate.Value;
		}
		string extension = "";
		if (settings.Extension.IsSet)
		{
			extension = settings.Extension.Value;
			configHolder.Config.Extension = extension;
			needSave = true;
		}
		else if (config.Extension != null)
		{
			extension = config.Extension;
		}
		if (extension.Length > 0)
		{
			foreach (string ext in extension.Split(' ', StringSplitOptions.RemoveEmptyEntries))
			{
				if (ImageExt.Contains(ext))
				{
					imageExt = ext;
				}
				else if (videoExt.Contains(ext))
				{
					videoExt = ext;
				}
			}
		}
		if (needSave)
		{
			configHolder.Save();
		}
	}

	/// <summary>
	/// 扫描指定文件。
	/// </summary>
	private static string ScanFile(string file)
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
	private async Task<Func<TaskContext, Task>?> CheckFile(string file, string desc)
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
				if (imageSize.NeedCompress(ref width, ref height))
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
					if (videoSize.NeedCompress(ref width, ref height, videoStream.Bitrate > bitRate))
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

private class Config
{
	/// <summary>
	/// 图片压缩选项。
	/// </summary>
	public string? ImageOpt { get; set; }

	/// <summary>
	/// 视频压缩选项。
	/// </summary>
	public string? VideoOpt { get; set; }

	/// <summary>
	/// 视频码率限制。
	/// </summary>
	public int? BitRate { get; set; }

	/// <summary>
	/// 压缩后缀选项。
	/// </summary>
	public string? Extension { get; set; }
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
	public bool NeedCompress(ref int width, ref int height, bool forceCompress = false)
	{
		if (width <= maxWidth && height <= maxHeight)
		{
			return forceCompress;
		}
		double scale;
		if (width < height && rotate)
		{
			if (height <= maxWidth && width <= maxHeight)
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
