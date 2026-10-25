#!/usr/bin/env -S dotnet --

/**
 * 把当前目录（及子目录）里的媒体文件转换成 epub 阅读器友好的格式：
 * 音频（flac/wav/wv）转 320Kbps 的 mp3，图片（jp2/heic/webp）转 png（可以用 --image-format 改成 jpg）。
 * 音频转换依赖 utils/ffmpeg 下自带的 ffmpeg，图片转换依赖 Magick.NET。
 */
#:include utils/file.cs
#:include utils/task.cs
#:include utils/video.cs
#:package Magick.NET.Core
#:package Magick.NET-Q8-x64
#:package Spectre.Console
#:package Spectre.Console.Cli
#:package Xabe.FFmpeg
// 发布成单文件 exe：`dotnet publish convert-media.cs` 只产出一个 convert-media.exe。
// Magick.NET 的原生库用 IncludeNativeLibrariesForSelfExtract 打进单文件，运行时自解压到临时目录。
#:property PublishAot=false
#:property PublishSingleFile=true
#:property SelfContained=false
#:property RuntimeIdentifier=win-x64
#:property IncludeNativeLibrariesForSelfExtract=true
#:property DebugType=embedded
#nullable enable

using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ImageMagick;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp<ConvertCommand>();
return await app.RunAsync(args);

/// <summary>
/// 转换媒体文件命令。
/// </summary>
sealed class ConvertCommand : AsyncCommand<ConvertCommand.Settings>
{
	/// <summary>
	/// 命令的设置。
	/// </summary>
	public sealed class Settings : CommandSettings
	{
		[Description("要处理的文件目录，默认为当前目录。")]
		[CommandArgument(0, "[path]")]
		public string? Path { get; init; }

		[Description("不递归处理子目录。")]
		[CommandOption("-n|--no-recursion")]
		[DefaultValue(false)]
		public bool NoRecursion { get; init; }

		[Description("图片转换的目标格式，可选 png 或 jpg，默认为 png。")]
		[CommandOption("-i|--image-format")]
		[DefaultValue("png")]
		public string ImageFormat { get; init; } = "png";

		[Description("mp3 的码率，默认为 320k。")]
		[CommandOption("-b|--bitrate")]
		[DefaultValue("320k")]
		public string Bitrate { get; init; } = "320k";

		[Description("覆盖已存在的转换结果。")]
		[CommandOption("-o|--overwrite")]
		[DefaultValue(false)]
		public bool Overwrite { get; init; }
	}

	/// <summary>
	/// 需要转换的音频扩展名。
	/// </summary>
	private static readonly HashSet<string> AudioExt = [".flac", ".wav", ".wv"];

	/// <summary>
	/// 需要转换的图片扩展名。
	/// </summary>
	private static readonly HashSet<string> ImageExt = [".jp2", ".heic", ".webp"];

	/// <summary>
	/// 转换时临时文件的后缀。
	/// </summary>
	private const string BackupExtension = ".backup";

	/// <summary>
	/// 转换为 jpg 时的质量。
	/// </summary>
	private const int JpegQuality = 90;

	/// <summary>
	/// 执行命令。
	/// </summary>
	protected override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
	{
		var path = settings.Path ?? Directory.GetCurrentDirectory();
		if (!Directory.Exists(path))
		{
			AnsiConsole.MarkupLine($"[red]无效的路径 {path.EscapeMarkup()}[/]");
			return 1;
		}
		// 解析图片的目标格式。
		string format = settings.ImageFormat.Trim().TrimStart('.').ToLowerInvariant();
		MagickFormat imageFormat;
		string imageExt;
		switch (format)
		{
			case "png":
				imageFormat = MagickFormat.Png;
				imageExt = ".png";
				break;
			case "jpg":
			case "jpeg":
				imageFormat = MagickFormat.Jpeg;
				imageExt = ".jpg";
				break;
			default:
				AnsiConsole.MarkupLine($"[red]无效的图片格式 {settings.ImageFormat.EscapeMarkup()}[/]，可选 png 或 jpg");
				return 1;
		}
		EnumerationOptions options = new()
		{
			RecurseSubdirectories = !settings.NoRecursion,
		};
		string[] files = Directory.EnumerateFiles(path, "*", options).ToArray();
		int handled = await ConvertAudiosAsync(path, files, settings, cancellationToken);
		handled += await ConvertImagesAsync(path, files, imageFormat, imageExt, settings, cancellationToken);
		if (handled == 0)
		{
			AnsiConsole.MarkupLine("[yellow]没有需要转换的文件[/]");
		}
		return 0;
	}

	/// <summary>
	/// 把音频转换为 mp3，返回处理的文件数。
	/// </summary>
	private static async Task<int> ConvertAudiosAsync(string rootPath, string[] files, Settings settings, CancellationToken cancellationToken)
	{
		string[] audioFiles = files
			.Where(file => AudioExt.Contains(Path.GetExtension(file).ToLowerInvariant()))
			.ToArray();
		if (audioFiles.Length == 0)
		{
			return 0;
		}
		if (!File.Exists(VideoUtils.FFmpegPath))
		{
			AnsiConsole.MarkupLine($"[yellow]找不到 ffmpeg，跳过 {audioFiles.Length} 个音频：{VideoUtils.FFmpegPath.EscapeMarkup()}[/]");
			return 0;
		}
		List<string> convertFiles = [];
		int skipped = 0;
		foreach (string file in audioFiles)
		{
			// 已经转换过的文件默认跳过，避免重复压制。
			if (!settings.Overwrite && File.Exists(Path.ChangeExtension(file, ".mp3")))
			{
				skipped++;
				continue;
			}
			convertFiles.Add(file);
		}
		if (skipped > 0)
		{
			AnsiConsole.MarkupLine($"[grey]跳过 {skipped} 个已存在 mp3 的文件（用 --overwrite 覆盖）[/]");
		}
		if (convertFiles.Count == 0)
		{
			return audioFiles.Length;
		}
		List<Func<TaskContext, Task>> tasks = [];
		foreach (string file in convertFiles)
		{
			string target = file;
			tasks.Add(ctx => ConvertAudioAsync(ctx, rootPath, target, settings.Bitrate));
		}
		await TaskRun.RunAsync("转换音频", tasks, cancellationToken);
		return audioFiles.Length;
	}

	/// <summary>
	/// 把图片转换为目标格式，返回处理的文件数。
	/// </summary>
	private static async Task<int> ConvertImagesAsync(string rootPath, string[] files, MagickFormat imageFormat,
		string imageExt, Settings settings, CancellationToken cancellationToken)
	{
		HashSet<string> fileSet = new(files);
		List<string> convertFiles = [];
		int skipped = 0;
		foreach (string file in files)
		{
			string ext = Path.GetExtension(file).ToLowerInvariant();
			string source;
			if (ImageExt.Contains(ext))
			{
				source = file;
			}
			else if (ext == BackupExtension)
			{
				// 上次转换中途失败留下的备份，先把原文件恢复出来。
				string originFile = Path.ChangeExtension(file, null)!;
				if (fileSet.Contains(originFile))
				{
					// 原图还在，说明上次已经恢复过了，丢掉备份。
					File.Delete(originFile);
				}
				File.Move(file, originFile);
				source = originFile;
			}
			else
			{
				continue;
			}
			// 已经转换过的文件默认跳过，避免重复转换。
			if (!settings.Overwrite && File.Exists(Path.ChangeExtension(source, imageExt)))
			{
				skipped++;
				continue;
			}
			convertFiles.Add(source);
		}
		if (skipped > 0)
		{
			AnsiConsole.MarkupLine($"[grey]跳过 {skipped} 个已存在 {imageExt} 的图片（用 --overwrite 覆盖）[/]");
		}
		if (convertFiles.Count == 0)
		{
			return 0;
		}
		List<Func<TaskContext, Task>> tasks = [];
		foreach (string file in convertFiles)
		{
			string target = file;
			tasks.Add(ctx => ConvertImageAsync(ctx, rootPath, target, imageFormat, imageExt));
		}
		await TaskRun.RunAsync("转换图片", tasks, cancellationToken);
		return convertFiles.Count;
	}

	/// <summary>
	/// 转换指定的音频文件。
	/// </summary>
	private static async Task ConvertAudioAsync(TaskContext ctx, string rootPath, string file, string bitrate)
	{
		string desc = FileUtils.GetRelativePath(rootPath, file).EscapeMarkup();
		ctx.Status($"转换 {desc} ...");
		string target = Path.ChangeExtension(file, ".mp3");
		using Process process = new();
		List<string> args = [
			"-hide_banner", "-nostdin", "-loglevel", "error", "-y",
			"-i", file,
			"-ab", bitrate,
			"-map_metadata", "0",
			"-id3v2_version", "3",
			target,
		];
		process.StartInfo = new ProcessStartInfo(VideoUtils.FFmpegPath, args)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		process.Start();
		// ffmpeg 会把信息都写到错误流，必须读掉，避免管道写满卡死。
		Task outputTask = process.StandardOutput.ReadToEndAsync();
		Task<string> errorTask = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync(ctx.CancellationToken);
		await outputTask;
		string error = (await errorTask).Trim();
		if (process.ExitCode != 0)
		{
			throw new Exception(error.Length > 0 ? error : $"ffmpeg 退出码 {process.ExitCode}");
		}
	}

	/// <summary>
	/// 转换指定的图片。
	/// </summary>
	private static async Task ConvertImageAsync(TaskContext ctx, string rootPath, string file, MagickFormat imageFormat, string imageExt)
	{
		string desc = FileUtils.GetRelativePath(rootPath, file).EscapeMarkup();
		ctx.Status($"转换 {desc} ...");
		// 先把文件改名，避免转换过程中出错把原图弄坏。
		string backupFile = file + BackupExtension;
		File.Move(file, backupFile);
		try
		{
			using (var image = new MagickImage(backupFile))
			{
				if (imageFormat == MagickFormat.Jpeg)
				{
					image.Quality = JpegQuality;
					// jpg 不支持透明，先铺一层白底，避免透明区域变黑。
					if (image.HasAlpha)
					{
						image.BackgroundColor = MagickColors.White;
						image.Alpha(AlphaOption.Remove);
					}
				}
				image.Format = imageFormat;
				File.WriteAllBytes(Path.ChangeExtension(file, imageExt), image.ToByteArray());
			}
			// 正常结束，删除备份文件。
			await FileUtils.SafeDeleteFile(backupFile);
		}
		catch
		{
			// 转换失败，把原文件还原，避免丢文件。
			if (File.Exists(backupFile) && !File.Exists(file))
			{
				File.Move(backupFile, file);
			}
			throw;
		}
	}
}
