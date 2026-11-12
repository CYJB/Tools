#!/usr/bin/env -S dotnet --

/**
 * 有声书 m4b 工具：把目录下的音频合并成带章节的单个 m4b，也可以查看、导出和修改 m4b 的元数据与章节。
 * 依赖 utils/ffmpeg 下自带的 ffmpeg / ffprobe。
 *
 * ffmpeg 在这个场景下有多个「退出码为 0 的静默失败」，本工具把它们全部固化成显式处理：
 * - 省略 -map_chapters 时 ffmpeg 会取第一个有章节的输入，导致旧章节被静默保留；
 * - apply 用 -map 0 会把原有的章节轨（一条 codec_type=data 的流）也带上，和新章节表叠加成两条；
 * - major_brand 是 muxer 选项、不走 metadata 映射，每次 -c copy 重封装都会退回 "M4A "；
 * - 混合 codec 走 concat demuxer 会静默丢帧，混合采样率走 -c:a copy 会静默变调；
 * - format.duration 是所有流的最大值，会被章节轨撑大，拿它校验输出等于用输入验证输入。
 */
#:include utils/console.cs
#:include utils/file.cs
#:include utils/process.cs
#:include utils/task.cs
#:include utils/video.cs
#:package Cyjb
#:package Spectre.Console
#:package Spectre.Console.Cli
// Xabe.FFmpeg 只因为 utils/video.cs 顶部有 using，本工具直接用 ProcessRun 调 ffmpeg。
#:package Xabe.FFmpeg
// 发布成单文件 exe：`dotnet publish audiobook.cs` 只产出一个 audiobook.exe。
// 不涉及 Magick.NET 的原生库，所以不需要 IncludeNativeLibrariesForSelfExtract。
#:property PublishAot=false
#:property PublishSingleFile=true
#:property SelfContained=false
#:property RuntimeIdentifier=$(NETCoreSdkRuntimeIdentifier)
#:property DebugType=embedded
#nullable enable

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cyjb;
using Spectre.Console;
using Spectre.Console.Cli;

return await ConsoleApp.RunAsyncWithCancellation(config =>
{
	config.AddCommand<MergeCommand>("merge").WithDescription("把目录下的音频合并成带章节的单个 m4b");
	config.AddCommand<InfoCommand>("info").WithDescription("显示 m4b 的元数据、章节和格式诊断");
	config.AddCommand<ExportCommand>("export").WithDescription("导出 m4b 的元数据和章节，供手工编辑");
	config.AddCommand<ApplyCommand>("apply").WithDescription("把编辑后的元数据和章节写回 m4b");
}, args);

/// <summary>
/// 合并目录下的音频文件。
/// </summary>
sealed class MergeCommand : AsyncCommand<MergeCommand.Settings>
{
	/// <summary>
	/// 命令的设置。
	/// </summary>
	public sealed class Settings : CommandSettings
	{
		[Description("要合并的音频目录，默认为当前目录。目录下没有音频时，会逐个合并它的子目录。")]
		[CommandArgument(0, "[path]")]
		public string? Path { get; init; }

		[Description("输出的 m4b 路径，默认为「<父目录>/<目录名>.m4b」。批量合并时忽略此选项。")]
		[CommandOption("-o|--output")]
		public string? Output { get; init; }

		[Description("书名，默认取目录名。")]
		[CommandOption("-t|--title")]
		public string? Title { get; init; }

		[Description("作者。")]
		[CommandOption("-a|--artist")]
		public string? Artist { get; init; }

		[Description("系列名。")]
		[CommandOption("--album")]
		public string? Album { get; init; }

		[Description("流派，默认「有声书」。")]
		[CommandOption("--genre")]
		[DefaultValue("有声书")]
		public string? Genre { get; init; }

		[Description("出版年份。")]
		[CommandOption("--date")]
		public string? Date { get; init; }

		[Description("简介。")]
		[CommandOption("--comment")]
		public string? Comment { get; init; }

		[Description("AAC 码率，仅在需要重新编码时生效。默认 auto：对齐源文件里最高的码率（无损源用 320k），保证不引入额外的可闻损失。")]
		[CommandOption("-b|--bitrate")]
		[DefaultValue("auto")]
		public string Bitrate { get; init; } = "auto";

		[Description("输出采样率，默认取所有源文件里的最大值（降采样是不可逆的损失）。")]
		[CommandOption("--sample-rate")]
		public int? SampleRate { get; init; }

		[Description("封面图片，默认在目录里自动识别（cover.* / folder.* / 0*.jpg 等）。")]
		[CommandOption("-c|--cover")]
		public string? Cover { get; init; }

		[Description("章节标题来源" + ChapterTitleOpt)]
		[CommandOption("--chapter-title")]
		[DefaultValue("filename")]
		public string ChapterTitle { get; init; } = "filename";

		[Description("覆盖已存在的输出文件。")]
		[CommandOption("-y|--overwrite")]
		[DefaultValue(false)]
		public bool Overwrite { get; init; }

		private const string ChapterTitleOpt = @"
- filename: 用文件名（默认）
- tag: 用音频文件里的 title 标签，缺失时回退到文件名
- number: 用「第 N 章」";
	}

	/// <summary>
	/// 执行命令。
	/// </summary>
	protected override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
	{
		if (!AudiobookUtils.CheckFFmpeg())
		{
			return 1;
		}
		string path = settings.Path ?? Directory.GetCurrentDirectory();
		if (!Directory.Exists(path))
		{
			AnsiConsole.MarkupLine($"[red]无效的目录 {path.EscapeMarkup()}[/]");
			return 1;
		}
		path = Path.GetFullPath(path);
		ChapterTitleMode titleMode;
		switch (settings.ChapterTitle.Trim().ToLowerInvariant())
		{
			case "filename": titleMode = ChapterTitleMode.FileName; break;
			case "tag": titleMode = ChapterTitleMode.Tag; break;
			case "number": titleMode = ChapterTitleMode.Number; break;
			default:
				AnsiConsole.MarkupLine($"[red]无效的章节标题来源 {settings.ChapterTitle.EscapeMarkup()}[/]，可选 filename、tag 或 number");
				return 1;
		}
		// 目录里直接有音频时合并成一个 m4b，否则逐个合并子目录。
		string[] directAudios = AudiobookUtils.ListAudioFiles(path);
		List<Func<TaskContext, Task>> tasks = [];
		if (directAudios.Length > 0)
		{
			// 输出放在源目录的**外面**，这样重复运行不会把上次的结果再合并进去。
			string output = settings.Output != null
				? Path.GetFullPath(settings.Output)
				: Path.Combine(Path.GetDirectoryName(path) ?? path, Path.GetFileName(path) + ".m4b");
			string? extensionError = AudiobookUtils.CheckWritableExtension(output);
			if (extensionError != null)
			{
				AnsiConsole.MarkupLine($"[red]{extensionError.EscapeMarkup()}[/]");
				return 1;
			}
			if (!settings.Overwrite && File.Exists(output))
			{
				AnsiConsole.MarkupLine($"[red]输出文件已存在 {output.EscapeMarkup()}，用 --overwrite 覆盖[/]");
				return 1;
			}
			tasks.Add(ctx => AudiobookUtils.MergeAsync(ctx, path, directAudios, output, settings, titleMode, cancellationToken));
		}
		else
		{
			if (settings.Output != null)
			{
				AnsiConsole.MarkupLine("[yellow]批量合并时忽略 --output，输出文件按子目录名命名[/]");
			}
			foreach (string dir in Directory.EnumerateDirectories(path).OrderBy(d => Path.GetFileName(d), StringNaturalComparer.Default))
			{
				string[] audios = AudiobookUtils.ListAudioFiles(dir);
				if (audios.Length == 0)
				{
					continue;
				}
				string target = dir;
				string output = Path.Combine(path, Path.GetFileName(dir) + ".m4b");
				if (!settings.Overwrite && File.Exists(output))
				{
					AnsiConsole.MarkupLine($"[grey]跳过 {Path.GetFileName(dir).EscapeMarkup()}：输出已存在（用 --overwrite 覆盖）[/]");
					continue;
				}
				tasks.Add(ctx => AudiobookUtils.MergeAsync(ctx, target, audios, output, settings, titleMode, cancellationToken));
			}
		}
		if (tasks.Count == 0)
		{
			AnsiConsole.MarkupLine("[yellow]没有找到要合并的音频文件[/]");
			return 0;
		}
		await TaskRun.RunAsync("合并有声书", tasks, cancellationToken);
		return 0;
	}
}

/// <summary>
/// 显示 m4b 的信息。
/// </summary>
sealed class InfoCommand : AsyncCommand<InfoCommand.Settings>
{
	/// <summary>
	/// 命令的设置。
	/// </summary>
	public sealed class Settings : CommandSettings
	{
		[Description("要查看的 m4b 文件。")]
		[CommandArgument(0, "<file>")]
		public string File { get; init; } = "";

		[Description("直接输出 ffprobe 的原始 JSON。")]
		[CommandOption("--json")]
		[DefaultValue(false)]
		public bool Json { get; init; }
	}

	/// <summary>
	/// 执行命令。
	/// </summary>
	protected override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
	{
		if (!AudiobookUtils.CheckFFmpeg())
		{
			return 1;
		}
		if (!System.IO.File.Exists(settings.File))
		{
			AnsiConsole.MarkupLine($"[red]文件不存在 {settings.File.EscapeMarkup()}[/]");
			return 1;
		}
		string file = Path.GetFullPath(settings.File);
		if (settings.Json)
		{
			// 必须走原始 stdout：AnsiConsole.WriteLine 会把内容当 markup 解析（JSON 里的 [ ] 会被当成标签），
			// 还会按控制台宽度折行，两者都会破坏机器可读的输出。
			Console.Out.WriteLine((await AudiobookUtils.FFprobeJsonAsync(file, cancellationToken)).StdOut);
			return 0;
		}
		FFprobeResult probe = await AudiobookUtils.ProbeAsync(file, cancellationToken);
		var streams = probe.Streams ?? [];
		var tags = probe.Format?.Tags;
		double duration = AudiobookUtils.GetAudioDuration(probe);
		// 章节轨在 ffprobe 里是一条 codec_type=data 的流（Nero chpl），不是 subtitle。
		int chapterTrackCount = streams.Count(s => s.CodecType == "data");
		var covers = streams.Where(s => s.CodecType == "video").ToArray();
		var audio = streams.FirstOrDefault(s => s.CodecType == "audio");
		var chapters = probe.Chapters ?? [];

		AnsiConsole.MarkupLine($"[bold]{Path.GetFileName(file).EscapeMarkup()}[/]  {AudiobookUtils.FormatDuration(duration)}  {AudiobookUtils.FormatSize(new FileInfo(file).Length)}");
		Grid grid = new Grid().AddColumn().AddColumn();
		void AddRow(string name, string? value)
		{
			grid.AddRow($"[grey]{name}[/]", (value ?? "").EscapeMarkup());
		}
		AddRow("书名", AudiobookUtils.GetTag(tags, "title"));
		AddRow("作者", AudiobookUtils.GetTag(tags, "artist"));
		AddRow("系列", AudiobookUtils.GetTag(tags, "album"));
		AddRow("流派", AudiobookUtils.GetTag(tags, "genre"));
		AddRow("年份", AudiobookUtils.GetTag(tags, "date"));
		AddRow("简介", AudiobookUtils.GetTag(tags, "comment"));
		AddRow("音频", audio == null ? null : $"{audio.CodecName} {audio.SampleRate}Hz {audio.Channels}ch {audio.BitRate}");
		AddRow("封面", covers.Length == 0 ? null : covers.Length == 1 && covers[0].IsAttachedPic
			? "1 张"
			: $"{covers.Length} 张（{(covers.All(c => c.IsAttachedPic) ? "" : "有流未标记 attached_pic，")}播放器可能不显示）");
		AnsiConsole.Write(grid);
		AnsiConsole.WriteLine();

		// 有声书标记的诊断：这两项决定了 Apple Books 是否把它当有声书处理（断点续听、章节跳转）。
		string? brand = AudiobookUtils.GetTag(tags, "major_brand");
		string? mediaType = AudiobookUtils.GetTag(tags, "media_type");
		if (brand != AudiobookUtils.M4bBrand)
		{
			AnsiConsole.MarkupLine($"[yellow]major_brand 是 {(brand == null ? "(缺失)" : $"\"{brand.EscapeMarkup()}\"")}，应为 \"{AudiobookUtils.M4bBrand}\"，播放器可能把它当普通音频而非有声书[/]");
		}
		if (mediaType != AudiobookUtils.AudiobookMediaType)
		{
			AnsiConsole.MarkupLine($"[yellow]media_type(stik) 是 {mediaType ?? "(缺失)"}，应为 {AudiobookUtils.AudiobookMediaType}，Apple Books 不会提供断点续听[/]");
		}
		// 没有章节时章节轨也应该是 0 条：残留的 Nero chpl 会让播放器显示过期的章节。
		int expectedTracks = chapters.Length == 0 ? 0 : 1;
		if (chapterTrackCount != expectedTracks)
		{
			AnsiConsole.MarkupLine($"[yellow]有 {chapters.Length} 个章节，但章节轨有 {chapterTrackCount} 条（应为 {expectedTracks} 条），播放器可能显示过期的章节[/]");
		}
		if (covers.Length > 0 && !covers.All(c => c.IsAttachedPic))
		{
			AnsiConsole.MarkupLine("[yellow]有封面流没有标记 attached_pic，播放器可能不显示封面[/]");
		}
		if (brand == AudiobookUtils.M4bBrand && mediaType == AudiobookUtils.AudiobookMediaType
			&& chapterTrackCount == expectedTracks
			&& covers.All(c => c.IsAttachedPic))
		{
			AnsiConsole.MarkupLine("[green]格式检查通过[/]");
		}
		AnsiConsole.WriteLine();

		if (chapters.Length == 0)
		{
			AnsiConsole.MarkupLine("[grey]没有章节[/]");
			return 0;
		}
		Table table = new Table().AddColumn("#").AddColumn("开始").AddColumn("结束").AddColumn("时长").AddColumn("标题");
		for (int i = 0; i < chapters.Length; i++)
		{
			long startMs = (long)Math.Round(AudiobookUtils.ParseDuration(chapters[i].StartTime) * 1000);
			long endMs = (long)Math.Round(AudiobookUtils.ParseDuration(chapters[i].EndTime) * 1000);
			// 开始/结束列统一带小时，否则同一列里会混着 00:00 和 10:00:00 两种格式。
			table.AddRow((i + 1).ToString(), AudiobookUtils.FormatTimestamp(startMs), AudiobookUtils.FormatTimestamp(endMs),
				AudiobookUtils.FormatDuration((endMs - startMs) / 1000.0), AudiobookUtils.GetTag(chapters[i].Tags, "title")?.EscapeMarkup() ?? "");
		}
		AnsiConsole.Write(table);
		return 0;
	}
}

/// <summary>
/// 导出 m4b 的元数据和章节。
/// </summary>
sealed class ExportCommand : AsyncCommand<ExportCommand.Settings>
{
	/// <summary>
	/// 命令的设置。
	/// </summary>
	public sealed class Settings : CommandSettings
	{
		[Description("要导出的 m4b 文件。")]
		[CommandArgument(0, "<file>")]
		public string File { get; init; } = "";

		[Description("导出的文本文件路径，默认为同名的 .txt。")]
		[CommandOption("-o|--output")]
		public string? Output { get; init; }

		[Description("覆盖已存在的输出文件。")]
		[CommandOption("-y|--overwrite")]
		[DefaultValue(false)]
		public bool Overwrite { get; init; }
	}

	/// <summary>
	/// 执行命令。
	/// </summary>
	protected override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
	{
		if (!AudiobookUtils.CheckFFmpeg())
		{
			return 1;
		}
		if (!System.IO.File.Exists(settings.File))
		{
			AnsiConsole.MarkupLine($"[red]文件不存在 {settings.File.EscapeMarkup()}[/]");
			return 1;
		}
		string file = Path.GetFullPath(settings.File);
		string output = Path.GetFullPath(settings.Output ?? Path.ChangeExtension(file, ".txt"));
		if (!settings.Overwrite && System.IO.File.Exists(output))
		{
			AnsiConsole.MarkupLine($"[red]输出文件已存在 {output.EscapeMarkup()}，用 --overwrite 覆盖[/]");
			return 1;
		}
		FFprobeResult probe = await AudiobookUtils.ProbeAsync(file, cancellationToken);
		double duration = AudiobookUtils.GetAudioDuration(probe);
		var chapters = (probe.Chapters ?? [])
			.Select(c => new ChapterInfo((long)Math.Round(AudiobookUtils.ParseDuration(c.StartTime) * 1000), AudiobookUtils.GetTag(c.Tags, "title") ?? ""))
			.ToList();
		// 只导出白名单里的键：ffmpeg -f ffmetadata 会连 major_brand、encoder 这些 muxer 内部键一起吐出来，
		// 原样写回去会变成字面的元数据标签，而且并不会真的设置 ftyp brand。
		var metadata = new List<KeyValuePair<string, string>>();
		foreach (string key in AudiobookUtils.MetadataKeys)
		{
			string? value = AudiobookUtils.GetTag(probe.Format?.Tags, key);
			if (value != null)
			{
				metadata.Add(new KeyValuePair<string, string>(key, value));
			}
		}
		System.IO.File.WriteAllText(output, ChaptersFileWriter.Write(metadata, chapters, duration), AudiobookUtils.Utf8NoBom);
		AnsiConsole.MarkupLine($"[green]已导出[/] {output.EscapeMarkup()}");
		if (chapters.Count == 0)
		{
			AnsiConsole.MarkupLine("[grey]原文件没有章节，导出的是模板，可以直接往里加章节行[/]");
		}
		return 0;
	}
}

/// <summary>
/// 把编辑后的元数据和章节写回 m4b。
/// </summary>
sealed class ApplyCommand : AsyncCommand<ApplyCommand.Settings>
{
	/// <summary>
	/// 命令的设置。
	/// </summary>
	public sealed class Settings : CommandSettings
	{
		[Description("要修改的 m4b 文件。")]
		[CommandArgument(0, "<file>")]
		public string File { get; init; } = "";

		[Description("章节文件，默认为同名的 .txt（即 export 的输出）。")]
		[CommandOption("-i|--input")]
		public string? Input { get; init; }

		[Description("替换封面图片。")]
		[CommandOption("-c|--cover")]
		public string? Cover { get; init; }

		[Description("输出到新文件，默认原地替换。")]
		[CommandOption("-o|--output")]
		public string? Output { get; init; }

		[Description("原地替换成功后删除 .backup 备份。")]
		[CommandOption("--no-backup")]
		[DefaultValue(false)]
		public bool NoBackup { get; init; }
	}

	/// <summary>
	/// 执行命令。
	/// </summary>
	protected override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
	{
		if (!AudiobookUtils.CheckFFmpeg())
		{
			return 1;
		}
		if (!System.IO.File.Exists(settings.File))
		{
			AnsiConsole.MarkupLine($"[red]文件不存在 {settings.File.EscapeMarkup()}[/]");
			return 1;
		}
		string file = Path.GetFullPath(settings.File);
		if (!AudiobookUtils.IsContainerFile(file))
		{
			AnsiConsole.MarkupLine($"[red]{Path.GetFileName(file).EscapeMarkup()} 不是 m4b 容器，apply 只能修改 .m4b / .m4a / .mp4 / .m4v 里的元数据和章节。[/]");
			AnsiConsole.MarkupLine("[grey]如果手上是散落的 mp3，用 merge 合并成 m4b（会重新编码为 AAC）；单个 mp3 也可以放进一个目录后 merge。[/]");
			return 1;
		}
		string input = Path.GetFullPath(settings.Input ?? Path.ChangeExtension(file, ".txt"));
		if (!System.IO.File.Exists(input))
		{
			AnsiConsole.MarkupLine($"[red]章节文件不存在 {input.EscapeMarkup()}，先用 export 导出[/]");
			return 1;
		}
		if (settings.Cover != null && !System.IO.File.Exists(settings.Cover))
		{
			AnsiConsole.MarkupLine($"[red]封面文件不存在 {settings.Cover.EscapeMarkup()}[/]");
			return 1;
		}
		string output = settings.Output != null ? Path.GetFullPath(settings.Output) : file;
		string? extensionError = AudiobookUtils.CheckWritableExtension(output);
		if (extensionError != null)
		{
			AnsiConsole.MarkupLine($"[red]{extensionError.EscapeMarkup()}[/]");
			return 1;
		}
		if (settings.Output != null && output != file && System.IO.File.Exists(output))
		{
			AnsiConsole.MarkupLine($"[red]输出文件已存在 {output.EscapeMarkup()}[/]");
			return 1;
		}
		FFprobeResult probe = await AudiobookUtils.ProbeAsync(file, cancellationToken);
		double duration = AudiobookUtils.GetAudioDuration(probe);
		if (duration <= 0)
		{
			AnsiConsole.MarkupLine("[red]无法读取文件时长，可能不是有效的音频文件[/]");
			return 1;
		}
		ChaptersFile chaptersFile;
		try
		{
			chaptersFile = ChaptersFileParser.Parse(System.IO.File.ReadAllText(input), input);
		}
		catch (ChaptersFileException e)
		{
			AnsiConsole.MarkupLine($"[red]{e.Message.EscapeMarkup()}[/]");
			return 1;
		}
		foreach (string warning in chaptersFile.Warnings)
		{
			AnsiConsole.MarkupLine($"[yellow]{warning.EscapeMarkup()}[/]");
		}
		// 以原文件的元数据为基础，用章节文件里的键覆盖；值留空表示删除该键。
		// 这样只写了章节的文件不会把原有的书名、作者等一起清掉。
		var metadata = new List<KeyValuePair<string, string>>();
		foreach (string key in AudiobookUtils.MetadataKeys)
		{
			if (chaptersFile.Metadata.TryGetValue(key, out string? value))
			{
				if (value.Length > 0)
				{
					metadata.Add(new KeyValuePair<string, string>(key, value));
				}
			}
			else
			{
				string? origin = AudiobookUtils.GetTag(probe.Format?.Tags, key);
				if (origin != null)
				{
					metadata.Add(new KeyValuePair<string, string>(key, origin));
				}
			}
		}
		// 章节边界校验：首尾相接由工具保证（每章结束时间等于下一章开始时间），所以只需要检查开始时间。
		long durationMs = (long)Math.Round(duration * 1000);
		List<string> errors = [];
		for (int i = 0; i < chaptersFile.Chapters.Count; i++)
		{
			ChapterInfo chapter = chaptersFile.Chapters[i];
			if (i == 0 && chapter.StartMs != 0)
			{
				errors.Add($"第 {chapter.Line} 行：第一章的开始时间必须是 [00:00:00]，当前是 [{AudiobookUtils.FormatTimestamp(chapter.StartMs)}]");
			}
			if (chapter.StartMs >= durationMs)
			{
				errors.Add($"第 {chapter.Line} 行：开始时间 [{AudiobookUtils.FormatTimestamp(chapter.StartMs)}] 超出了文件总时长 {AudiobookUtils.FormatDuration(duration)}");
			}
			if (i > 0 && chapter.StartMs <= chaptersFile.Chapters[i - 1].StartMs)
			{
				errors.Add($"第 {chapter.Line} 行：开始时间 [{AudiobookUtils.FormatTimestamp(chapter.StartMs)}] 必须晚于第 {chaptersFile.Chapters[i - 1].Line} 行的 [{AudiobookUtils.FormatTimestamp(chaptersFile.Chapters[i - 1].StartMs)}]");
			}
		}
		if (errors.Count > 0)
		{
			AnsiConsole.MarkupLine($"[red]{input.EscapeMarkup()} 有 {errors.Count} 处错误：[/]");
			foreach (string error in errors)
			{
				AnsiConsole.MarkupLine($"  [red]{error.EscapeMarkup()}[/]");
			}
			return 1;
		}
		if (chaptersFile.Chapters.Count == 0)
		{
			AnsiConsole.MarkupLine("[yellow]章节文件里没有任何章节，将清空原有章节、只写入元数据[/]");
		}

		string tempFile = output + ".tmp.m4b";
		string metaFile = Path.Combine(Path.GetTempPath(), $"audiobook-{Guid.NewGuid():N}.txt");
		try
		{
			System.IO.File.WriteAllText(metaFile, AudiobookUtils.BuildFFMetadata(metadata, chaptersFile.Chapters, durationMs), AudiobookUtils.Utf8NoBom);
			List<string> args = ["-hide_banner", "-nostdin", "-y", "-v", "error"];
			args.AddRange(["-i", file, "-f", "ffmetadata", "-i", metaFile]);
			int coverIndex = -1;
			if (settings.Cover != null)
			{
				coverIndex = 2;
				args.AddRange(["-i", Path.GetFullPath(settings.Cover)]);
			}
			// 显式列举要保留的流：只取音频和封面。
			// 不能用 -map 0，那样会把原有的章节轨（一条 codec_type=data 的流）也带上，
			// 再叠加 -map_chapters 1 就变成两条章节轨，播放器会显示过期的那份。
			args.AddRange(["-map", "0:a"]);
			if (coverIndex >= 0)
			{
				// 换封面时只映射新的封面流，旧的会被自然丢掉。
				args.AddRange(["-map", $"{coverIndex}:v", "-c:v", "copy", "-disposition:v:0", "attached_pic"]);
			}
			else
			{
				// 保留原有封面，? 表示没有视频流时不报错。
				args.Add("-map");
				args.Add("0:v?");
			}
			// 没有章节时用 -map_chapters -1 清空，否则 ffmpeg 会保留原文件的章节。
			args.AddRange(["-map_chapters", chaptersFile.Chapters.Count > 0 ? "1" : "-1", "-map_metadata", "1"]);
			args.AddRange(["-c", "copy"]);
			// major_brand 是 muxer 选项、不走 metadata 映射，每次重封装都必须重新指定，
			// 否则会退回 "M4A "，文件就不再被当成有声书。
			args.AddRange(AudiobookUtils.M4bOutputFlags);
			args.Add(tempFile);
			await ProcessRun.RunAsync(VideoUtils.FFmpegPath, args, null, cancellationToken);
			int expectedVideos = coverIndex >= 0 ? 1 : (probe.Streams ?? []).Count(s => s.CodecType == "video");
			await AudiobookUtils.VerifyOutputAsync(tempFile, chaptersFile.Chapters, expectedVideos, duration, cancellationToken);
			if (AudiobookUtils.PathEquals(output, file))
			{
				string backup = file + ".backup";
				if (System.IO.File.Exists(backup))
				{
					await FileUtils.SafeDeleteFile(backup);
				}
				if (settings.NoBackup)
				{
					System.IO.File.Move(tempFile, file, true);
				}
				else
				{
					// File.Replace 一步完成「原文件改名成备份 + 新文件就位」，
					// 不会留下「原文件已经改名、新文件还没到位」的中间状态。
					try
					{
						System.IO.File.Replace(tempFile, file, backup, true);
					}
					catch (PlatformNotSupportedException)
					{
						System.IO.File.Move(file, backup);
						System.IO.File.Move(tempFile, file);
					}
					AnsiConsole.MarkupLine($"[grey]原文件已备份到 {backup.EscapeMarkup()}[/]");
				}
			}
			else
			{
				System.IO.File.Move(tempFile, output, true);
			}
		}
		catch (Exception e) when (e is not OperationCanceledException)
		{
			// 回退用的两步 Move 可能已经把原文件改名成备份了，先还原再报错，
			// 否则「原文件未改动」这句话就是假的。
			string backup = file + ".backup";
			if (!System.IO.File.Exists(file) && System.IO.File.Exists(backup))
			{
				System.IO.File.Move(backup, file);
			}
			AnsiConsole.MarkupLine($"[red]写入失败，原文件未改动[/]");
			AnsiConsole.WriteException(e, ExceptionFormats.ShortenPaths);
			return 1;
		}
		finally
		{
			// 取消时也要清理临时文件（可能有几百 MB），而且清理失败不能盖掉真正的错误。
			try
			{
				if (System.IO.File.Exists(tempFile))
				{
					await FileUtils.SafeDeleteFile(tempFile);
				}
				if (System.IO.File.Exists(metaFile))
				{
					await FileUtils.SafeDeleteFile(metaFile);
				}
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
			}
		}
		AnsiConsole.MarkupLine($"[green]已写入[/] {output.EscapeMarkup()}  {chaptersFile.Chapters.Count} 章");
		return 0;
	}
}

/// <summary>
/// 章节标题的来源。
/// </summary>
enum ChapterTitleMode
{
	/// <summary>用文件名。</summary>
	FileName,
	/// <summary>用音频文件的 title 标签。</summary>
	Tag,
	/// <summary>用序号。</summary>
	Number,
}

/// <summary>
/// 有声书相关的工具方法。
/// </summary>
static class AudiobookUtils
{
	/// <summary>
	/// m4b 的 major_brand，必须是 4 个字符（末尾的空格是关键），否则 ffmpeg 会静默忽略并退回 isom。
	/// </summary>
	public const string M4bBrand = "M4B ";

	/// <summary>
	/// 有声书的 media_type（写入 stik atom），Apple Books 据此提供断点续听。
	/// </summary>
	public const string AudiobookMediaType = "2";

	/// <summary>
	/// 所有 m4b 输出都要带的参数。
	/// </summary>
	public static readonly string[] M4bOutputFlags = [
		"-metadata", $"media_type={AudiobookMediaType}",
		"-brand", M4bBrand,
		"-movflags", "+faststart",
		"-f", "mp4",
	];

	/// <summary>
	/// 无 BOM 的 UTF-8 编码。
	/// </summary>
	public static readonly UTF8Encoding Utf8NoBom = new(false);

	/// <summary>
	/// 自动对齐源码率时的下限。
	/// </summary>
	private const long MinBitrate = 32_000;

	/// <summary>
	/// 自动对齐源码率时的上限。AAC-LC 再往上没有收益，而且 ffmpeg 的原生 aac 编码器
	/// 会自己饱和（mono 请求 320k 实际只出约 132k，stereo 约 207k），所以封顶不会浪费体积。
	/// </summary>
	private const long MaxBitrate = 320_000;

	/// <summary>
	/// 无损编码的名字，pcm_ 系列另外按前缀判断。
	/// </summary>
	private static readonly HashSet<string> LosslessCodecs = ["flac", "alac", "wavpack", "ape", "tta", "truehd", "mlp", "shorten"];

	/// <summary>
	/// 判断是否是无损编码。无损源编码成 AAC 一定是有损的，需要单独提示用户备份。
	/// </summary>
	public static bool IsLosslessCodec(string codec)
	{
		return codec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase)
			|| LosslessCodecs.Contains(codec.ToLowerInvariant());
	}

	/// <summary>
	/// 返回音频流的码率。
	/// </summary>
	/// <remarks>
	/// flac 的 <c>stream.bit_rate</c> 是 N/A，必须回退到 <c>format.bit_rate</c>；
	/// 反过来 mp3/m4a 的 <c>format.bit_rate</c> 会因为容器和标签开销虚高 2~4%
	/// （实测 mp3 stream=128000 而 format=129838），所以优先用 stream 的。
	/// </remarks>
	private static long GetAudioBitrate(FFprobeStream stream, FFprobeFormat? format)
	{
		if (long.TryParse(stream.BitRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) && value > 0)
		{
			return value;
		}
		return long.TryParse(format?.BitRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0 ? value : 0;
	}

	/// <summary>
	/// 支持的音频扩展名。
	/// </summary>
	private static readonly HashSet<string> AudioExt = [
		".mp3", ".mp2", ".m4a", ".m4b", ".aac", ".flac", ".wav", ".ogg", ".oga", ".opus", ".wma", ".aif", ".aiff",
	];

	/// <summary>
	/// 支持的封面扩展名。
	/// </summary>
	private static readonly HashSet<string> ImageExt = [".jpg", ".jpeg", ".png"];

	/// <summary>
	/// 可以写入的容器扩展名。输出到别的扩展名（例如 .mp3）会得到一个扩展名与内容不符的文件，
	/// 所以直接拒绝。
	/// </summary>
	private static readonly HashSet<string> WritableExt = [".m4b", ".m4a", ".mp4", ".m4v"];

	/// <summary>
	/// 检查输出文件的扩展名是否是可写入的容器，不合法时返回错误信息。
	/// </summary>
	public static string? CheckWritableExtension(string output)
	{
		string ext = Path.GetExtension(output).ToLowerInvariant();
		return WritableExt.Contains(ext)
			? null
			: $"输出文件 {Path.GetFileName(output)} 的扩展名 {ext} 不是 m4b 容器，本工具只能写入 .m4b / .m4a / .mp4 / .m4v";
	}

	/// <summary>
	/// 判断文件是否是 mp4 系的容器（m4b / m4a / mp4 / m4v）。
	/// </summary>
	/// <remarks>
	/// apply 只能改这些容器：ffmpeg 用 <c>-f mp4</c> 时会把 mp3 音频也塞进去并打上 mp4a 的 codec tag，
	/// 它自己能解码，但 Apple Books 之类的播放器不认这种文件，而且不会报错。
	/// </remarks>
	public static bool IsContainerFile(string path) => WritableExt.Contains(Path.GetExtension(path).ToLowerInvariant());

	/// <summary>
	/// 允许写入的元数据键，顺序就是 export 的输出顺序。
	/// </summary>
	public static readonly string[] MetadataKeys = [
		"title", "artist", "album", "album_artist", "genre", "date",
		"comment", "description", "composer", "copyright", "publisher", "synopsis",
	];

	/// <summary>
	/// 元数据键的集合，用于快速判断。
	/// </summary>
	private static readonly HashSet<string> MetadataKeySet = new(MetadataKeys, StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// 列出目录下的音频文件，按自然序排列（ch2 排在 ch10 之前）。
	/// </summary>
	/// <remarks>
	/// 用 <c>StringNaturalComparer</c>（<c>pack-epub</c> 排漫画页用的也是它）：普通的字符串排序
	/// 会把 ch10 排在 ch2 前面，整本书的章节顺序错乱而且不会有任何提示。
	/// </remarks>
	public static string[] ListAudioFiles(string dir)
	{
		return Directory.EnumerateFiles(dir)
			.Where(file => AudioExt.Contains(Path.GetExtension(file).ToLowerInvariant()))
			.OrderBy(file => Path.GetFileName(file), StringNaturalComparer.Default)
			.ToArray();
	}

	/// <summary>
	/// 在目录里查找封面图片。
	/// </summary>
	public static string? FindCover(string dir)
	{
		string[] images = Directory.EnumerateFiles(dir)
			.Where(file => ImageExt.Contains(Path.GetExtension(file).ToLowerInvariant()))
			.ToArray();
		// 优先 cover.* / folder.* / 0*.* 这类明确的封面命名。
		return images.FirstOrDefault(file => FileUtils.IsCoverFile(Path.GetFileName(file)))
			?? images.FirstOrDefault(file =>
			{
				string name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
				return name is "cover" or "folder" or "front";
			})
			?? (images.Length == 1 ? images[0] : null);
	}

	/// <summary>
	/// 执行 ffprobe 并返回原始输出。
	/// </summary>
	/// <remarks>
	/// 用 <c>-v warning</c> 而不是 <c>-v error</c>：没有 Xing 头的 mp3 会让 ffprobe 在这里提示
	/// 「Estimating duration from bitrate」，据此可以判断容器时长是否可靠。
	/// </remarks>
	public static Task<ProcessResult> FFprobeJsonAsync(string file, CancellationToken cancellationToken)
	{
		List<string> args = [
			"-v", "warning", "-print_format", "json",
			"-show_format", "-show_streams", "-show_chapters",
			file,
		];
		return ProcessRun.RunAsync(VideoUtils.FFprobePath, args, null, cancellationToken);
	}

	/// <summary>
	/// 探测指定的媒体文件。
	/// </summary>
	public static async Task<FFprobeResult> ProbeAsync(string file, CancellationToken cancellationToken)
	{
		ProcessResult result = await FFprobeJsonAsync(file, cancellationToken);
		FFprobeResult probe = JsonSerializer.Deserialize<FFprobeResult>(result.StdOut)
			?? throw new Exception($"无法解析 ffprobe 的输出：{file}");
		probe.DurationEstimated = result.StdErr.Contains("Estimating duration", StringComparison.OrdinalIgnoreCase);
		return probe;
	}

	/// <summary>
	/// 用 ffmpeg 完整解码一遍，返回解码后的时长（毫秒）。
	/// </summary>
	/// <remarks>
	/// 缺少 Xing/VBRI 头的 mp3 只能按码率估算容器时长，实测每个文件能差 26ms，
	/// 累加起来会让章节边界逐章漂移（6 个文件就到 97ms，上百个文件能差出几秒）。
	/// 解码后的时长没有这个问题，代价是要完整解码一遍，所以只对这类文件使用。
	/// </remarks>
	public static async Task<long> GetDecodedDurationMsAsync(string file, CancellationToken cancellationToken)
	{
		long lastUs = 0;
		List<string> args = [
			"-hide_banner", "-nostdin", "-v", "error", "-progress", "pipe:1",
			"-i", file, "-map", "0:a", "-f", "null", "-",
		];
		await ProcessRun.RunAsync(VideoUtils.FFmpegPath, args, line =>
		{
			if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
				long.TryParse(line["out_time_us=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long us))
			{
				lastUs = us;
			}
		}, cancellationToken);
		// 四舍五入而不是截断：每个文件都向下偏 1ms 的话，累积起来又变成漂移了。
		return (lastUs + 500) / 1000;
	}

	/// <summary>
	/// 解析 ffprobe 的时长字符串（秒）。
	/// </summary>
	public static double ParseDuration(string? value)
	{
		return value != null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : 0.0;
	}

	/// <summary>
	/// 返回音频流自己的时长（秒）。
	/// </summary>
	/// <remarks>
	/// 不能用 <c>format.duration</c>：它是所有流的最大值，而章节轨的结尾是按我们写进去的
	/// 章节表来的，用它就等于拿自己的输入验证自己的输出。
	/// </remarks>
	public static double GetAudioDuration(FFprobeResult probe)
	{
		var audio = (probe.Streams ?? []).FirstOrDefault(s => s.CodecType == "audio");
		double duration = ParseDuration(audio?.Duration);
		return duration > 0 ? duration : ParseDuration(probe.Format?.Duration);
	}

	/// <summary>
	/// 不区分大小写地读取标签。
	/// </summary>
	public static string? GetTag(Dictionary<string, string>? tags, string key)
	{
		if (tags == null)
		{
			return null;
		}
		foreach (var pair in tags)
		{
			if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
			{
				return pair.Value;
			}
		}
		return null;
	}

	/// <summary>
	/// 判断指定的键是否是允许的元数据键。
	/// </summary>
	public static bool IsMetadataKey(string key) => MetadataKeySet.Contains(key);

	/// <summary>
	/// 把秒数格式化成 [H:]MM:SS[.mmm]，超过 24 小时时小时数会继续增长。
	/// </summary>
	public static string FormatDuration(double seconds)
	{
		if (seconds < 0)
		{
			seconds = 0;
		}
		return FormatMilliseconds((long)Math.Round(seconds * 1000), false);
	}

	/// <summary>
	/// 把毫秒格式化成时间戳 HH:MM:SS[.mmm]（不含方括号）。
	/// </summary>
	public static string FormatTimestamp(long ms)
	{
		return FormatMilliseconds(ms, true);
	}

	/// <summary>
	/// 格式化毫秒数。
	/// </summary>
	/// <param name="ms">毫秒数。</param>
	/// <param name="alwaysHours">是否总是显示小时，章节时间戳需要，时长显示不需要。</param>
	/// <remarks>
	/// 不能用 <c>TimeSpan.ToString(@"hh\:mm\:ss")</c>：那里的 <c>h</c> 是「不含天数的小时部分」，
	/// 超过 24 小时会回绕，而 40 小时的有声书很常见。
	/// </remarks>
	private static string FormatMilliseconds(long ms, bool alwaysHours)
	{
		if (ms < 0)
		{
			ms = 0;
		}
		int millis = (int)(ms % 1000);
		long totalSeconds = ms / 1000;
		long hours = totalSeconds / 3600;
		long minutes = (totalSeconds % 3600) / 60;
		long seconds = totalSeconds % 60;
		string text = alwaysHours || hours > 0
			? string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}:{2:D2}", hours, minutes, seconds)
			: string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}", minutes, seconds);
		return millis == 0 ? text : $"{text}.{millis.ToString(CultureInfo.InvariantCulture).PadLeft(3, '0')}";
	}

	/// <summary>
	/// 格式化文件大小。
	/// </summary>
	public static string FormatSize(long size)
	{
		string[] units = ["B", "KiB", "MiB", "GiB"];
		double value = size;
		int unit = 0;
		while (value >= 1024 && unit < units.Length - 1)
		{
			value /= 1024;
			unit++;
		}
		return unit == 0
			? $"{size.ToString(CultureInfo.InvariantCulture)} {units[0]}"
			: $"{value.ToString("F1", CultureInfo.InvariantCulture)} {units[unit]}";
	}

	/// <summary>
	/// 比较两个路径是否指向同一个文件。Windows 的文件系统不区分大小写。
	/// </summary>
	public static bool PathEquals(string left, string right)
	{
		return string.Equals(left, right,
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
	}

	/// <summary>
	/// 把值里的换行替换成空格。章节文件和 ffmetadata 都是按行解析的，
	/// 而 mp4 的简介、版权这类标签里是可以带换行的。
	/// </summary>
	public static string FlattenNewlines(string value)
	{
		return value.Replace('\r', ' ').Replace('\n', ' ');
	}

	/// <summary>
	/// 检查 ffmpeg / ffprobe 是否存在，缺失时输出提示并返回 <c>false</c>。
	/// </summary>
	public static bool CheckFFmpeg()
	{
		if (File.Exists(VideoUtils.FFmpegPath) && File.Exists(VideoUtils.FFprobePath))
		{
			return true;
		}
		AnsiConsole.MarkupLine($"[red]找不到 ffmpeg / ffprobe：{VideoUtils.FFmpegFolder.EscapeMarkup()}[/]");
		return false;
	}

	/// <summary>
	/// 生成 ffmpeg 的 ffmetadata 内容。
	/// </summary>
	/// <remarks>
	/// 这个文件只由工具生成、从不解析：TIMEBASE 固定为 1/1000（毫秒），
	/// 每章的结束时间取下一章的开始时间，最后一章取文件总时长，所以章节总是首尾相接。
	/// </remarks>
	public static string BuildFFMetadata(IReadOnlyList<KeyValuePair<string, string>> metadata, IReadOnlyList<ChapterInfo> chapters, long durationMs)
	{
		StringBuilder builder = new();
		builder.Append(";FFMETADATA1\n");
		foreach (var pair in metadata)
		{
			builder.Append(pair.Key).Append('=').Append(EscapeMetadata(pair.Value)).Append('\n');
		}
		for (int i = 0; i < chapters.Count; i++)
		{
			long end = i + 1 < chapters.Count ? chapters[i + 1].StartMs : durationMs;
			builder.Append("[CHAPTER]\nTIMEBASE=1/1000\nSTART=").Append(chapters[i].StartMs)
				.Append("\nEND=").Append(Math.Max(end, chapters[i].StartMs)).Append('\n');
			builder.Append("title=").Append(EscapeMetadata(chapters[i].Title)).Append('\n');
		}
		return builder.ToString();
	}

	/// <summary>
	/// 转义 ffmetadata 的值。ffmpeg 的 reader 比 writer 宽松，但按 writer 的标准来总是安全的。
	/// </summary>
	private static string EscapeMetadata(string value)
	{
		StringBuilder builder = new(value.Length + 8);
		foreach (char c in value)
		{
			// ffmetadata 没有换行转义，值里的换行会让后面的内容被当成新记录，甚至吃掉 [CHAPTER]。
			if (c is '\r' or '\n')
			{
				builder.Append(' ');
				continue;
			}
			if (c is '\\' or ';' or '#' or '=')
			{
				builder.Append('\\');
			}
			builder.Append(c);
		}
		return builder.ToString();
	}

	/// <summary>
	/// 转义 concat 列表里的路径。路径用单引号包裹，内部的单引号要写成 '\''。
	/// </summary>
	public static string EscapeConcatPath(string path)
	{
		return "'" + path.Replace("'", "'\\''") + "'";
	}

	/// <summary>
	/// 校验 ffmpeg 的输出，失败时抛出异常。
	/// </summary>
	/// <remarks>
	/// ffmpeg 在多种静默损坏的场景下都返回 0（混合 codec 丢帧、混合采样率 copy 变调、
	/// BOM 吃掉第一章、章节被钳制），所以每次输出都必须回读检查。
	/// </remarks>
	public static async Task<FFprobeResult> VerifyOutputAsync(string file, IReadOnlyList<ChapterInfo> chapters,
		int expectedVideos, double expectedDuration, CancellationToken cancellationToken)
	{
		FFprobeResult probe = await ProbeAsync(file, cancellationToken);
		var streams = probe.Streams ?? [];
		var tags = probe.Format?.Tags;
		List<string> problems = [];
		string? brand = GetTag(tags, "major_brand");
		if (brand != M4bBrand)
		{
			problems.Add($"major_brand 应为 \"{M4bBrand}\"，实际为 {(brand == null ? "(缺失)" : $"\"{brand}\"")}");
		}
		string? mediaType = GetTag(tags, "media_type");
		if (mediaType != AudiobookMediaType)
		{
			problems.Add($"media_type(stik) 应为 {AudiobookMediaType}，实际为 {mediaType ?? "(缺失)"}");
		}
		// 章节轨在 ffprobe 里是一条 codec_type=data 的流（Nero chpl），不是 subtitle。
		int chapterTracks = streams.Count(s => s.CodecType == "data");
		int expectedTracks = chapters.Count > 0 ? 1 : 0;
		if (chapterTracks != expectedTracks)
		{
			problems.Add($"章节轨应为 {expectedTracks} 条，实际为 {chapterTracks} 条");
		}
		int videos = streams.Count(s => s.CodecType == "video");
		if (videos != expectedVideos)
		{
			problems.Add($"封面流应为 {expectedVideos} 条，实际为 {videos} 条");
		}
		var actual = probe.Chapters ?? [];
		if (actual.Length != chapters.Count)
		{
			problems.Add($"章节数应为 {chapters.Count}，实际为 {actual.Length}");
		}
		for (int i = 0; i < Math.Min(actual.Length, chapters.Count); i++)
		{
			string title = GetTag(actual[i].Tags, "title") ?? "";
			if (title != chapters[i].Title)
			{
				problems.Add($"第 {i + 1} 章标题应为 \"{chapters[i].Title}\"，实际为 \"{title}\"");
			}
			long startMs = (long)Math.Round(ParseDuration(actual[i].StartTime) * 1000);
			if (Math.Abs(startMs - chapters[i].StartMs) > 500)
			{
				problems.Add($"第 {i + 1} 章开始时间应为 {FormatTimestamp(chapters[i].StartMs)}，实际为 {FormatTimestamp(startMs)}");
			}
		}
		double duration = GetAudioDuration(probe);
		// 容差随章节数放大：AAC 编码有一帧 priming，加上毫秒取整，几十个文件也可能差出几百毫秒，
		// 但那不是损坏；真正要抓的是 concat 丢帧那种成比例的短缺。
		double durationTolerance = Math.Max(0.5, 0.03 * Math.Max(1, chapters.Count));
		if (expectedDuration > 0 && Math.Abs(duration - expectedDuration) > durationTolerance)
		{
			problems.Add($"音频时长应为 {FormatDuration(expectedDuration)}，实际为 {FormatDuration(duration)}");
		}
		if (problems.Count > 0)
		{
			throw new Exception("输出校验失败：\n  " + string.Join("\n  ", problems));
		}
		return probe;
	}

	/// <summary>
	/// 合并一个目录下的音频文件。
	/// </summary>
	public static async Task MergeAsync(TaskContext ctx, string dir, string[] audios, string output,
		MergeCommand.Settings settings, ChapterTitleMode titleMode, CancellationToken cancellationToken)
	{
		string desc = Path.GetFileName(dir).EscapeMarkup();
		ctx.Status($"探测 {desc} ...");
		// 排除输出文件本身，避免把上次合并的结果再合进去。
		string outputPath = Path.GetFullPath(output);
		audios = audios.Where(file => !PathEquals(Path.GetFullPath(file), outputPath)).ToArray();
		if (audios.Length == 0)
		{
			throw new Exception($"{dir} 里没有可合并的音频文件");
		}
		var probes = new FFprobeResult[audios.Length];
		for (int i = 0; i < audios.Length; i++)
		{
			probes[i] = await ProbeAsync(audios[i], cancellationToken);
		}
		// 判断源文件是否一致：混合 codec 走 concat demuxer 会静默丢帧，
		// 混合采样率走 -c:a copy 会静默变调（48kHz 的采样按 44.1kHz 解释，约 9% 变调，
		// 而时长显示还正常），都必须提前识别出来。
		string? codec = null;
		bool uniformCodec = true;
		bool hasVideoStream = false;
		bool allAac = true;
		// 重编码时的目标参数取所有源的最大值：降采样和 downmix 是不可逆的损失，升采样只是补零。
		// 不显式固定的话 ffmpeg 会按第一个输入协商，于是「哪个文件排在前面」会决定整本书的音质——
		// 实测 22050/mono 排在 44100/stereo 前面时，不固定参数会让全书输出变成 22050/mono。
		int maxSampleRate = 0;
		int maxChannels = 0;
		long maxBitrate = 0;
		int losslessCount = 0;
		bool uniformParams = true;
		for (int i = 0; i < probes.Length; i++)
		{
			var streams = probes[i].Streams ?? [];
			var audio = streams.FirstOrDefault(s => s.CodecType == "audio");
			if (audio?.CodecName == null)
			{
				throw new Exception($"{Path.GetFileName(audios[i])} 里没有音频流");
			}
			if (codec == null)
			{
				codec = audio.CodecName;
			}
			else if (!string.Equals(codec, audio.CodecName, StringComparison.Ordinal))
			{
				uniformCodec = false;
			}
			if (!string.Equals(audio.CodecName, "aac", StringComparison.Ordinal))
			{
				allAac = false;
			}
			if (IsLosslessCodec(audio.CodecName))
			{
				losslessCount++;
			}
			if (streams.Any(s => s.CodecType == "video"))
			{
				// 带封面的 mp3 有 2 条流，会让 concat demuxer 的流匹配错位。
				hasVideoStream = true;
			}
			int sampleRate = 0;
			if (int.TryParse(audio.SampleRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedRate))
			{
				sampleRate = parsedRate;
			}
			int channels = audio.Channels ?? 0;
			if (i > 0 && (sampleRate != maxSampleRate || channels != maxChannels))
			{
				uniformParams = false;
			}
			maxSampleRate = Math.Max(maxSampleRate, sampleRate);
			maxChannels = Math.Max(maxChannels, channels);
			// flac 的 stream.bit_rate 是 N/A，必须回退到 format.bit_rate；
			// 反过来 mp3/m4a 的 format.bit_rate 会因为容器和标签开销虚高 2~4%，所以 stream 优先。
			maxBitrate = Math.Max(maxBitrate, GetAudioBitrate(audio, probes[i].Format));
		}
		// concat demuxer 只有在 codec、采样率、声道全部一致时才安全：
		// - codec 不一致会静默丢帧（日志刷 PTS invalid dropping，退出码 0）；
		// - 采样率/声道不一致时，mp3 源看似能靠自动重采样蒙对，但 AAC(m4a) 源实测会丢掉一半音频
		//   （6 秒的素材只出 3 秒），所以一并改走 filter_complex，由每个输入显式归一化后再拼接。
		bool useFilter = !uniformCodec || hasVideoStream || !uniformParams;
		// 只有走 demuxer 且全是 AAC 时才能直接复制音频流（真正无损）。
		bool copyAudio = !useFilter && allAac;
		// 目标码率：有损源对齐源里最高的码率（AAC-LC 效率高于 mp3，同码率下不会更差）；
		// 无损源没有「对齐码率」的意义（1411k 的 wav 和 130k 的 flac 都是无损），直接用封顶值。
		// ffmpeg 的原生 aac 编码器会自己饱和（mono 请求 320k 实际只出约 132k），所以封顶不浪费体积。
		string bitrateArg;
		if (string.Equals(settings.Bitrate.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
		{
			long target = losslessCount > 0 ? MaxBitrate : Math.Clamp(maxBitrate, MinBitrate, MaxBitrate);
			bitrateArg = target.ToString(CultureInfo.InvariantCulture);
		}
		else
		{
			bitrateArg = settings.Bitrate.Trim();
		}
		int sampleRateTarget = settings.SampleRate is int requestedRate ? requestedRate : maxSampleRate;
		string route = useFilter ? "filter_complex 拼接" : copyAudio ? "concat 直接复制音频流" : "concat 重编码为 AAC";
		ctx.MarkupLine($"[grey]{desc}：{audios.Length} 个文件，{route}{(copyAudio ? "" : $"，{bitrateArg}bps {sampleRateTarget}Hz {maxChannels}ch")}[/]");
		if (losslessCount > 0)
		{
			ctx.MarkupLine($"[yellow]{desc}：有 {losslessCount} 个无损源文件，将编码为 AAC —— 这一步是有损的，请确认已经备份原始无损文件[/]");
		}

		// 章节边界。缺少 Xing/VBRI 头的 mp3 只能按码率估算容器时长，实测每个文件能差 26ms，
		// 逐章累积后上百个文件能差出几秒，所以这类文件改用完整解码得到的时长。
		long[] durations = new long[audios.Length];
		int estimatedCount = 0;
		for (int i = 0; i < audios.Length; i++)
		{
			if (probes[i].DurationEstimated)
			{
				estimatedCount++;
				durations[i] = await GetDecodedDurationMsAsync(audios[i], cancellationToken);
			}
			else
			{
				durations[i] = (long)Math.Round(ParseDuration(probes[i].Format?.Duration) * 1000);
			}
		}
		if (estimatedCount > 0)
		{
			ctx.MarkupLine($"[grey]{desc}：{estimatedCount} 个文件缺少 Xing 头、容器时长不可靠，已改用解码时长计算章节边界[/]");
		}
		var chapters = new List<ChapterInfo>();
		long start = 0;
		for (int i = 0; i < audios.Length; i++)
		{
			string title = titleMode switch
			{
				ChapterTitleMode.Tag => GetTag(probes[i].Format?.Tags, "title") ?? Path.GetFileNameWithoutExtension(audios[i]),
				ChapterTitleMode.Number => $"第 {i + 1} 章",
				_ => Path.GetFileNameWithoutExtension(audios[i]),
			};
			chapters.Add(new ChapterInfo(start, title));
			start += durations[i];
		}
		long totalMs = start;

		// 元数据。
		var metadata = new List<KeyValuePair<string, string>>
		{
			new("title", settings.Title ?? Path.GetFileName(dir)),
		};
		void AddMeta(string key, string? value)
		{
			if (!string.IsNullOrEmpty(value))
			{
				metadata.Add(new KeyValuePair<string, string>(key, value));
			}
		}
		AddMeta("artist", settings.Artist);
		AddMeta("album", settings.Album);
		AddMeta("genre", settings.Genre);
		AddMeta("date", settings.Date);
		AddMeta("comment", settings.Comment);

		string? cover = settings.Cover != null ? Path.GetFullPath(settings.Cover) : FindCover(dir);
		if (cover != null && !File.Exists(cover))
		{
			throw new Exception($"封面文件不存在：{cover}");
		}

		string tempDir = Path.Combine(Path.GetTempPath(), $"audiobook-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);
		string listFile = Path.Combine(tempDir, "list.txt");
		string metaFile = Path.Combine(tempDir, "meta.txt");
		string tempOutput = output + ".tmp.m4b";
		try
		{
			File.WriteAllText(metaFile, BuildFFMetadata(metadata, chapters, totalMs), Utf8NoBom);
			List<string> args = ["-hide_banner", "-nostdin", "-y", "-v", "error", "-progress", "pipe:1"];
			// ffmpeg 按顺序解析参数，-map 这类输出选项一旦出现在 -i 之前就会报错，
			// 所以必须先把所有输入声明完，再写输出选项。
			int metaIndex;
			if (useFilter)
			{
				foreach (string audio in audios)
				{
					args.AddRange(["-i", audio]);
				}
				metaIndex = audios.Length;
			}
			else
			{
				StringBuilder list = new("ffconcat version 1.0\n");
				foreach (string audio in audios)
				{
					list.Append("file ").Append(EscapeConcatPath(Path.GetFullPath(audio).Replace('\\', '/'))).Append('\n');
				}
				File.WriteAllText(listFile, list.ToString(), Utf8NoBom);
				args.AddRange(["-f", "concat", "-safe", "0", "-i", listFile]);
				metaIndex = 1;
			}
			args.AddRange(["-f", "ffmetadata", "-i", metaFile]);
			int coverIndex = -1;
			if (cover != null)
			{
				coverIndex = metaIndex + 1;
				args.AddRange(["-i", cover]);
			}
			// 以下是输出选项。
			if (useFilter)
			{
				// concat 滤镜按第一个输入协商参数：低采样率的源排在前面时，后面高采样率的源
				// 会被先降采样/downmix 再升回来（实测日志 44100Hz stereo -> 22050Hz mono -> 44100Hz stereo），
				// 那是不可逆的损失。所以每个输入都先显式归一化到目标参数再拼接。
				// 显式标注 [i:a] 也顺带解决了带封面的源文件有 2 条流时自动匹配错位的问题。
				string layout = maxChannels switch { 1 => "mono", 2 => "stereo", _ => "" };
				List<string> segments = [];
				for (int i = 0; i < audios.Length; i++)
				{
					string normalize = sampleRateTarget > 0 ? $"[{i}:a]aresample={sampleRateTarget.ToString(CultureInfo.InvariantCulture)}" : $"[{i}:a]anull";
					normalize += layout.Length > 0
						? $",aformat=sample_fmts=fltp:channel_layouts={layout}"
						: ",aformat=sample_fmts=fltp";
					segments.Add($"{normalize}[a{i}]");
				}
				string labels = string.Concat(segments.Select((_, i) => $"[a{i}]"));
				args.AddRange(["-filter_complex", $"{string.Join(";", segments)};{labels}concat=n={audios.Length}:v=0:a=1[aout]"]);
				args.AddRange(["-map", "[aout]"]);
			}
			else
			{
				args.AddRange(["-map", "0:a"]);
			}
			if (coverIndex >= 0)
			{
				args.AddRange(["-map", $"{coverIndex}:v", "-c:v", "copy", "-disposition:v:0", "attached_pic"]);
			}
			args.AddRange(["-map_chapters", metaIndex.ToString(), "-map_metadata", metaIndex.ToString()]);
			if (copyAudio)
			{
				args.AddRange(["-c:a", "copy"]);
				// .aac 是 ADTS 封装，直接复制进 mp4 需要转成 ASC。
				if (audios.Any(file => Path.GetExtension(file).Equals(".aac", StringComparison.OrdinalIgnoreCase)))
				{
					args.AddRange(["-bsf:a", "aac_adtstoasc"]);
				}
			}
			else
			{
				args.AddRange(["-c:a", "aac", "-b:a", bitrateArg]);
				// 采样率和声道数必须显式固定为源里的最大值：不固定时 ffmpeg 按第一个输入协商，
				// 实测 22050/mono 的文件排在 44100/stereo 前面时，整本书会输出成 22050/mono。
				if (sampleRateTarget > 0)
				{
					args.AddRange(["-ar", sampleRateTarget.ToString(CultureInfo.InvariantCulture)]);
				}
				if (maxChannels > 0)
				{
					args.AddRange(["-ac", maxChannels.ToString(CultureInfo.InvariantCulture)]);
				}
			}
			args.AddRange(M4bOutputFlags);
			args.Add(tempOutput);

			ctx.Status($"合并 {desc} ...");
			double totalSeconds = totalMs / 1000.0;
			await ProcessRun.RunAsync(VideoUtils.FFmpegPath, args, line =>
			{
				if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
					long.TryParse(line["out_time_us=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) &&
					totalSeconds > 0)
				{
					int percent = (int)Math.Clamp(us / 1000000.0 / totalSeconds * 100, 0, 100);
					ctx.Status($"合并 {desc} [olive]{percent}%[/]");
				}
			}, cancellationToken);
			FFprobeResult outputProbe = await VerifyOutputAsync(tempOutput, chapters, cover != null ? 1 : 0, totalSeconds, cancellationToken);
			double actualDuration = GetAudioDuration(outputProbe);
			string? outputDir = Path.GetDirectoryName(output);
			if (outputDir != null)
			{
				Directory.CreateDirectory(outputDir);
			}
			File.Move(tempOutput, output, true);
			ctx.MarkupLine($"[green]{desc}[/] → {Path.GetFileName(output).EscapeMarkup()}  {chapters.Count} 章  {FormatDuration(actualDuration)}  {FormatSize(new FileInfo(output).Length)}");
		}
		finally
		{
			// 取消或失败时也要清理（临时输出可能有几百 MB），而且清理失败不能盖掉真正的错误。
			try
			{
				if (File.Exists(tempOutput))
				{
					await FileUtils.SafeDeleteFile(tempOutput);
				}
				Directory.Delete(tempDir, true);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
			}
		}
	}
}

/// <summary>
/// 一个章节的信息。
/// </summary>
/// <param name="startMs">开始时间（毫秒）。结束时间总是取下一章的开始时间，所以章节首尾相接。</param>
/// <param name="title">章节标题。</param>
/// <param name="line">在章节文件里的行号，用于报错。</param>
sealed record ChapterInfo(long StartMs, string Title, int Line = 0);

/// <summary>
/// 章节文件的解析结果。
/// </summary>
sealed class ChaptersFile
{
	/// <summary>元数据，键统一为小写。</summary>
	public Dictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);
	/// <summary>章节列表。</summary>
	public List<ChapterInfo> Chapters { get; } = [];
	/// <summary>解析过程中的警告。</summary>
	public List<string> Warnings { get; } = [];
}

/// <summary>
/// 章节文件的解析错误。
/// </summary>
sealed class ChaptersFileException(string message) : Exception(message);

/// <summary>
/// 章节文件的解析器。
/// </summary>
static class ChaptersFileParser
{
	/// <summary>
	/// 章节行：以 [时间戳] 开头，后面跟标题。时间戳本身在解析时校验，
	/// 这样写错格式（例如 [00:xx:05]）能得到针对性的错误提示，而不是「无法识别的内容」。
	/// </summary>
	private static readonly Regex ChapterRegex = new(@"^\s*\[([^\]]*)\]\s*(.*)$");

	/// <summary>
	/// 元数据行：键 = 值。
	/// </summary>
	private static readonly Regex MetadataRegex = new(@"^\s*([A-Za-z_][A-Za-z0-9_-]*)\s*=\s*(.*)$");

	/// <summary>
	/// 解析章节文件的内容。
	/// </summary>
	/// <remarks>
	/// 读文件时用 ReadAllText 再解析，而不是直接把文件交给 ffmpeg：Windows 记事本另存会加 UTF-8 BOM，
	/// BOM 紧贴 [CHAPTER] 时 ffmpeg 会静默丢掉第一章且退出码为 0。
	/// </remarks>
	public static ChaptersFile Parse(string content, string path)
	{
		ChaptersFile result = new();
		// 剥掉 BOM，统一换行符。
		content = content.Replace("\r\n", "\n").Replace('\r', '\n');
		if (content.Length > 0 && content[0] == '\uFEFF')
		{
			content = content[1..];
		}
		string[] lines = content.Split('\n');
		List<string> errors = [];
		for (int i = 0; i < lines.Length; i++)
		{
			string line = lines[i];
			string trimmed = line.Trim();
			int lineNumber = i + 1;
			if (trimmed.Length == 0 || trimmed.StartsWith('#'))
			{
				continue;
			}
			var chapterMatch = ChapterRegex.Match(line);
			if (chapterMatch.Success)
			{
				if (!TryParseTimestamp(chapterMatch.Groups[1].Value, out long ms))
				{
					errors.Add($"第 {lineNumber} 行：无法解析时间戳 [{chapterMatch.Groups[1].Value}]，格式应为 [时:分:秒] 或 [分:秒]，可以带 .毫秒");
					continue;
				}
				string title = chapterMatch.Groups[2].Value.Trim();
				if (title.Length == 0)
				{
					result.Warnings.Add($"第 {lineNumber} 行：章节标题为空");
				}
				result.Chapters.Add(new ChapterInfo(ms, title, lineNumber));
				continue;
			}
			var metadataMatch = MetadataRegex.Match(line);
			if (metadataMatch.Success)
			{
				string key = metadataMatch.Groups[1].Value.ToLowerInvariant();
				if (!AudiobookUtils.IsMetadataKey(key))
				{
					errors.Add($"第 {lineNumber} 行：未知的元数据键 \"{metadataMatch.Groups[1].Value}\"，可用：{string.Join("、", AudiobookUtils.MetadataKeys)}");
					continue;
				}
				// 值留空表示删除这个键（原有的值不会被带过来）。
				result.Metadata[key] = metadataMatch.Groups[2].Value.Trim();
				continue;
			}
			errors.Add($"第 {lineNumber} 行：无法识别的内容 \"{trimmed}\"。元数据写成「键 = 值」，章节写成「[时:分:秒] 标题」，注释以 # 开头");
		}
		if (errors.Count > 0)
		{
			throw new ChaptersFileException($"{Path.GetFileName(path)} 有 {errors.Count} 处错误：\n  " + string.Join("\n  ", errors));
		}
		return result;
	}

	/// <summary>
	/// 解析时间戳，返回毫秒。
	/// </summary>
	public static bool TryParseTimestamp(string text, out long ms)
	{
		ms = 0;
		string body = text;
		int millis = 0;
		int dot = body.IndexOf('.');
		if (dot >= 0)
		{
			string fraction = body[(dot + 1)..];
			// 毫秒最多 3 位，多出来的静默截断会让人以为写进去的值就是自己填的值。
			// NumberStyles.None 拒绝正负号和空白，避免 [1:-2:3] 这种被解析成合法时间。
			if (fraction.Length > 3 ||
				!int.TryParse(fraction.PadRight(3, '0'), NumberStyles.None, CultureInfo.InvariantCulture, out millis))
			{
				return false;
			}
			body = body[..dot];
		}
		string[] parts = body.Split(':');
		if (parts.Length is < 2 or > 3)
		{
			return false;
		}
		int[] values = new int[parts.Length];
		for (int i = 0; i < parts.Length; i++)
		{
			if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]))
			{
				return false;
			}
		}
		int hours, minutes, seconds;
		if (parts.Length == 3)
		{
			hours = values[0];
			minutes = values[1];
			seconds = values[2];
		}
		else
		{
			hours = 0;
			minutes = values[0];
			seconds = values[1];
		}
		if (minutes > 59 || seconds > 59)
		{
			return false;
		}
		ms = (hours * 3600L + minutes * 60 + seconds) * 1000 + millis;
		return true;
	}
}

/// <summary>
/// 章节文件的写出。
/// </summary>
static class ChaptersFileWriter
{
	/// <summary>
	/// 生成章节文件的内容。
	/// </summary>
	public static string Write(IReadOnlyList<KeyValuePair<string, string>> metadata, IReadOnlyList<ChapterInfo> chapters, double duration)
	{
		StringBuilder builder = new();
		builder.Append("""
			# 有声书信息与章节列表
			# 编辑后用 audiobook apply 写回 m4b。以 # 开头的是注释，空行会被忽略。
			#
			# 元数据写成「键 = 值」，不需要的行可以直接删除；把值留空（例如 artist =）表示删除原有的值。
			# 章节每行一条，行首的 [时:分:秒] 是这一章的开始时间，可以带 .毫秒，也可以只写 [分:秒]。
			#   每章的结束时间自动等于下一章的开始时间，最后一章到文件结尾，所以章节总是首尾相接。
			#   改时间戳 = 移动边界；删掉一行 = 并入上一章；插入一行 = 拆分章节。
			#   第一章必须从 [00:00:00] 开始。

			""");
		foreach (var pair in metadata)
		{
			builder.Append(pair.Key).Append(" = ").Append(AudiobookUtils.FlattenNewlines(pair.Value)).Append('\n');
		}
		builder.Append('\n');
		builder.Append($"# 共 {chapters.Count} 章，总时长 {AudiobookUtils.FormatDuration(duration)}\n");
		if (chapters.Count == 0)
		{
			builder.Append("# 原文件没有章节。取消下面的注释并按需增删：\n");
			builder.Append("# [00:00:00] 第一章\n");
			builder.Append("# [00:10:00] 第二章\n");
		}
		else
		{
			foreach (var chapter in chapters)
			{
				builder.Append('[').Append(AudiobookUtils.FormatTimestamp(chapter.StartMs))
					.Append("] ").Append(AudiobookUtils.FlattenNewlines(chapter.Title)).Append('\n');
			}
		}
		return builder.ToString();
	}
}

/// <summary>
/// ffprobe 的输出。
/// </summary>
sealed class FFprobeResult
{
	[JsonPropertyName("format")]
	public FFprobeFormat? Format { get; set; }

	[JsonPropertyName("streams")]
	public FFprobeStream[]? Streams { get; set; }

	[JsonPropertyName("chapters")]
	public FFprobeChapter[]? Chapters { get; set; }

	/// <summary>
	/// ffprobe 是否提示时长是按码率估算的（源文件缺少 Xing/VBRI 头时为真），
	/// 此时 <see cref="FFprobeFormat.Duration"/> 不可靠，实测每个文件能差 26ms 并且会随文件数累积。
	/// </summary>
	[JsonIgnore]
	public bool DurationEstimated { get; set; }
}

/// <summary>
/// ffprobe 的 format 段。
/// </summary>
sealed class FFprobeFormat
{
	[JsonPropertyName("duration")]
	public string? Duration { get; set; }

	[JsonPropertyName("bit_rate")]
	public string? BitRate { get; set; }

	[JsonPropertyName("tags")]
	public Dictionary<string, string>? Tags { get; set; }
}

/// <summary>
/// ffprobe 的 stream 段。
/// </summary>
sealed class FFprobeStream
{
	[JsonPropertyName("codec_name")]
	public string? CodecName { get; set; }

	[JsonPropertyName("codec_type")]
	public string? CodecType { get; set; }

	[JsonPropertyName("sample_rate")]
	public string? SampleRate { get; set; }

	[JsonPropertyName("channels")]
	public int? Channels { get; set; }

	[JsonPropertyName("bit_rate")]
	public string? BitRate { get; set; }

	[JsonPropertyName("duration")]
	public string? Duration { get; set; }

	[JsonPropertyName("disposition")]
	public Dictionary<string, int>? Disposition { get; set; }

	/// <summary>
	/// 是否被标记为封面图。播放器据此决定把这条视频流当封面还是当视频轨显示。
	/// </summary>
	[JsonIgnore]
	public bool IsAttachedPic => Disposition?.GetValueOrDefault("attached_pic") == 1;
}

/// <summary>
/// ffprobe 的 chapter 段。
/// </summary>
sealed class FFprobeChapter
{
	[JsonPropertyName("start_time")]
	public string? StartTime { get; set; }

	[JsonPropertyName("end_time")]
	public string? EndTime { get; set; }

	[JsonPropertyName("tags")]
	public Dictionary<string, string>? Tags { get; set; }
}
