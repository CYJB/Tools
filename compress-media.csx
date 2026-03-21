/**
 * 压缩图片或视频。
 * 压缩功能依赖 https://imagemagick.org/
 */
#load "utils/compress.csx"
#load "utils/console.csx"
#load "utils/task.csx"
#r "nuget: Spectre.Console, 0.54.0"
#r "nuget: Spectre.Console.Cli, 0.53.1"

#nullable enable

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Spectre.Console;
using Spectre.Console.Cli;

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
		public FlagValue<bool> Recursion { get; init; } = new();

		[Description("是否将当前压缩配置保存为默认配置。")]
		[CommandOption("-s|--save")]
		[DefaultValue(false)]
		public bool Save { get; init; }

		[Description("图片压缩选项" + CompressOpt)]
		[CommandOption("-i|--image [OPTION]")]
		[DefaultValue("2160")]
		public FlagValue<string> ImageSize { get; init; } = new();

		[Description("视频压缩选项" + CompressOpt)]
		[CommandOption("-v|--video [OPTION]")]
		[DefaultValue("1080r")]
		public FlagValue<string> VideoSize { get; init; } = new();

		[Description("视频码率限制，会压缩高于指定码率的视频。")]
		[CommandOption("-b|--bitrate [BITRATE]")]
		[DefaultValue(10000000)]
		public FlagValue<int> VideoBitRate { get; init; } = new();

		[Description("压缩目标后缀，使用空格分隔多个后缀，会自动识别类型。")]
		[CommandOption("-e|--ext [EXTION]")]
		[DefaultValue(".jpg .mp4")]
		public FlagValue<string> Extension { get; init; } = new();
	}

	/// <summary>
	/// 执行命令。
	/// </summary>
	public override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
	{
		var currentDir = settings.Path ?? Directory.GetCurrentDirectory();
		CompressConfig config = new();
		if (settings.Recursion.IsSet)
		{
			config.Recursion = settings.Recursion.Value;
		}
		if (settings.ImageSize.IsSet)
		{
			config.ImageSize = settings.ImageSize.Value;
		}
		if (settings.VideoSize.IsSet)
		{
			config.VideoSize = settings.VideoSize.Value;
		}
		if (settings.VideoBitRate.IsSet)
		{
			config.VideoBitRate = settings.VideoBitRate.Value;
		}
		if (settings.Extension.IsSet)
		{
			config.Extension = settings.Extension.Value;
		}
		var tasks = await AnsiConsole.Status().StartAsync($"扫描目录...", ctx => GetCompressTasksAsync(currentDir, (desc) =>
		{
			ctx.Status($"检查文件 {desc}...");
		}, config, settings.Save));
		if (tasks.Count == 0)
		{
			AnsiConsole.MarkupLine("[green]没有要压缩的文件[/]");
			return 0;
		}
		await RunTaskAsync("压缩文件", tasks, cancellationToken);
		return 0;
	}
}
