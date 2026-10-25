#!/usr/bin/env -S dotnet --

/**
 * 把当前目录（及子目录）里的文本文件（txt）转换为 UTF-8 编码。
 * 编码识别依赖 UTF.Unknown。
 */
#:include utils/file.cs
#:include utils/task.cs
#:package Spectre.Console
#:package Spectre.Console.Cli
#:package UTF.Unknown
// 发布成单文件 exe：`dotnet publish convert2utf8.cs` 只产出一个 convert2utf8.exe。
// 采用依赖框架（framework-dependent）的方式，运行时用本机安装的 .NET 10。
#:property PublishAot=false
#:property PublishSingleFile=true
#:property SelfContained=false
#:property RuntimeIdentifier=win-x64
#:property DebugType=embedded
#nullable enable

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Spectre.Console;
using Spectre.Console.Cli;
using UtfUnknown;

var app = new CommandApp<ConvertCommand>();
return await app.RunAsync(args);

/// <summary>
/// 转换编码命令。
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

		[Description("编码检测的最低可信度，默认 0.95。")]
		[CommandOption("-c|--min-confidence")]
		[DefaultValue(0.95)]
		public double MinConfidence { get; init; } = 0.95;
	}

	/// <summary>
	/// 已是 UTF-8 的文件数。
	/// </summary>
	private int utf8Count = 0;

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
		EnumerationOptions options = new()
		{
			RecurseSubdirectories = !settings.NoRecursion,
		};
		string[] files = Directory.EnumerateFiles(path, "*", options)
			.Where(file => Path.GetExtension(file).Equals(".txt", StringComparison.OrdinalIgnoreCase))
			.ToArray();
		if (files.Length == 0)
		{
			AnsiConsole.MarkupLine("[yellow]没有需要转换的文本文件[/]");
			return 0;
		}
		double minConfidence = Math.Clamp(settings.MinConfidence, 0.5, 1.0);
		List<Func<TaskContext, Task>> tasks = [];
		foreach (string file in files)
		{
			string target = file;
			tasks.Add(ctx => ConvertAsync(ctx, path, target, minConfidence));
		}
		await TaskRun.RunAsync("转换编码", tasks, cancellationToken);
		if (utf8Count > 0)
		{
			AnsiConsole.MarkupLine($"[grey]其中 {utf8Count} 个已经是 UTF-8，未改动[/]");
		}
		return 0;
	}

	/// <summary>
	/// 转换指定文件的编码。
	/// </summary>
	private async Task ConvertAsync(TaskContext ctx, string rootPath, string file, double minConfidence)
	{
		string desc = FileUtils.GetRelativePath(rootPath, file).EscapeMarkup();
		ctx.Status($"检测 {desc} ...");
		DetectionResult result = CharsetDetector.DetectFromFile(file);
		DetectionDetail? detected = result.Detected;
		if (detected == null)
		{
			ctx.MarkupLine($"[yellow]跳过 {desc}：识别不出编码[/]");
			return;
		}
		if (detected.Encoding.CodePage is 65001 or 20127)
		{
			// 已经是 UTF-8（或纯 ASCII），不用改。
			Interlocked.Increment(ref utf8Count);
			return;
		}
		// 可信度足够高才转换；只有一个候选编码时要求可以放宽 0.05。
		bool reliable = detected.Confidence >= minConfidence
			|| (result.Details.Count == 1 && detected.Confidence >= minConfidence - 0.05);
		if (!reliable)
		{
			ctx.MarkupLine($"[yellow]跳过 {desc}：编码检测不可靠（{detected.Encoding.WebName.EscapeMarkup()} {detected.Confidence:P0}）[/]");
			return;
		}
		ctx.Status($"转换 {desc} ...");
		string content = await File.ReadAllTextAsync(file, detected.Encoding, ctx.CancellationToken);
		await File.WriteAllTextAsync(file, content, Encoding.UTF8, ctx.CancellationToken);
	}
}
