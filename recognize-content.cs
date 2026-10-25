#!/usr/bin/env -S dotnet --

/**
 * 识别漫画的目录，并重命名相关图片。
 */
#:include utils/config-holder.cs
#:package OllamaSharp
#:package Spectre.Console
#:package Spectre.Console.Cli
// 基于文件的应用默认按 AOT 发布配置构建，会关掉「基于反射的 JSON 序列化」；
// 这里既要读配置文件、又要反序列化模型输出，所以关掉 AOT，让 JsonSerializer 能用反射。
#:property PublishAot=false
// 发布成单文件 exe：`dotnet publish recognize-content.cs` 只产出一个 recognize-content.exe。
// 采用依赖框架（framework-dependent）的方式，体积只有几 MB，运行时用本机安装的 .NET 10。
// 注意：单文件压缩（EnableCompressionInSingleFile）只在自包含发布时可用。
#:property PublishSingleFile=true
#:property SelfContained=false
#:property RuntimeIdentifier=win-x64
#:property DebugType=embedded
#nullable enable

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp<RecognizeContentCommand>();
return await app.RunAsync(args);

/// <summary>
/// 识别漫画目录的命令。
/// </summary>
sealed class RecognizeContentCommand : AsyncCommand<RecognizeContentCommand.Settings>
{
	/// <summary>
	/// 命令的设置。
	/// </summary>
	public sealed class Settings : CommandSettings
	{
		[Description("要处理的包含目录的图片路径。")]
		[CommandArgument(0, "<path>")]
		public required string Path { get; init; }

		[Description("识别目录时使用的额外提示。")]
		[CommandArgument(1, "[extra-prompt]")]
		public string? ExtraPrompt { get; init; }

		[Description("要使用的模型，默认使用配置文件中记录的值。")]
		[CommandOption("-m|--model <MODEL>")]
		public string? Model { get; init; }

		[Description("开启思考模式，可指定程度（low/medium/high）。默认关闭。")]
		[CommandOption("--think <LEVEL>")]
		public string? Think { get; init; }

		[Description("识别结果不合格时最多尝试几次，默认 3 次。")]
		[CommandOption("--retries <COUNT>")]
		public int Retries { get; init; } = 3;

		[Description("单次识别的超时秒数，默认 300 秒。")]
		[CommandOption("--timeout <SECONDS>")]
		public int Timeout { get; init; } = 300;

		[Description("只显示识别结果，不重命名文件。")]
		[CommandOption("--dry-run")]
		public bool DryRun { get; init; }

		[Description("标题里不追加「第N話」的话数信息（默认追加）。")]
		[CommandOption("--no-chapter")]
		public bool NoChapter { get; init; }

		[Description("不再询问确认，直接重命名文件。")]
		[CommandOption("-y|--yes")]
		public bool Yes { get; init; }
	}

	/// <summary>
	/// 提示词。要点：说明每个字段的含义并给出示例，点出容易混淆的数字（话数、作者），
	/// 要求逐字转录原文，避免模型自行翻译、编造或把页码写成序号。
	/// `{chapterRule}` 会按是否追加话数替换成不同的说明。
	/// </summary>
	private const string DefaultPrompt = """
		你是漫画杂志目录页（目次）的转录工具。请把图中的作品条目逐条转录为 JSON 数组。

		每条记录有两个字段：
		- PageId：该作品起始页的页码，即该条目所在行最右侧那个黑色圆形（或方框、色块）里的粗体数字，例如图中印着 003 就写 3。
		- Title：作品名。如果作品名下面还有一行副标题（通常被 ～ 或 — 之类的符号包住），用一个空格把它接在作品名后面，合并成一行。{chapterRule}

		必须遵守：
		1. 严格按图中从上到下的顺序输出全部条目，条数要与图中作品条目数一致，不要遗漏、合并或重复。
		2. PageId 取的是「页码」，不是序号，也不是「第16話」这类话数；一行里出现多个数字时，只取最右侧那个表示页面位置的数字。
		3. Title 必须逐字转录图中原文（日文或繁体中文），禁止翻译、改写、润色；除上面要求的话数外，不要包含作者名、「原案協力」等内容。
		4. 页码写成纯整数，去掉前导零。
		5. 只输出 JSON 数组，不要输出解释文字，不要输出 Markdown 代码块。
		""";

	/// <summary>
	/// 标题末尾追加话数时追加在提示词里的说明。
	/// </summary>
	private const string ChapterRule = "如果条目里还有「第16話」这类话数（通常在作品名下方，或位于行的开头、末尾），把它接在标题的最后，各部分之间用一个空格分隔，例如「東京黑盒子 ～虐待狂教授的困難事件報告～ 第16話」；有多话时按图中顺序用「、」连接。";

	/// <summary>
	/// 不需要话数时追加在提示词里的说明。
	/// </summary>
	private const string NoChapterRule = "标题里不要包含「第16話」这类话数。";

	/// <summary>
	/// 结果的结构化输出约束。这里手写 Schema，而不是从类型反射生成：
	/// 反射生成的 Schema 会把数组和元素都允许为 null，模型就可能只返回 null。
	/// </summary>
	private static readonly JsonObject ResponseSchema = new()
	{
		["type"] = "array",
		["items"] = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["PageId"] = new JsonObject
				{
					["type"] = "integer",
					["minimum"] = 0,
					["maximum"] = 99999,
					["description"] = "该作品起始页的页码，取目录行最右侧色块中的数字",
				},
				["Title"] = new JsonObject
				{
					["type"] = "string",
					["minLength"] = 1,
					["description"] = "作品名，副标题和话数用单个空格连接在同一行",
				},
			},
			["required"] = new JsonArray("PageId", "Title"),
			["additionalProperties"] = false,
		},
	};

	/// <summary>
	/// 反序列化识别结果的选项：容忍大小写差异、注释、尾逗号和数字字符串。
	/// </summary>
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		NumberHandling = JsonNumberHandling.AllowReadingFromString,
	};

	private static readonly ConfigHolder<Config> configHolder = new();
	private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif",
	};
	/// <summary>
	/// 匹配文件名开头的数字。
	/// </summary>
	private static readonly Regex FileNumberRegex = new(@"^(\d+)");
	/// <summary>
	/// 匹配标题开头的页码，例如「003 東京黑盒子」。
	/// </summary>
	private static readonly Regex TitleLeadingNumberRegex = new(@"^(\d+)[\s.、,．:：\-]+(\S.*)$");
	/// <summary>
	/// 匹配标题末尾的页码，例如「東京黑盒子 3」。
	/// </summary>
	private static readonly Regex TitleTrailingNumberRegex = new(@"^(\S.*?)[\s.、,．:：\-]+(\d+)$");
	private static readonly Regex WhitespaceRegex = new(@"\s+");
	private static readonly Regex InvalidFileNameCharsRegex = new($"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]");

	/// <summary>
	/// 执行命令。
	/// </summary>
	protected override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
	{
		var path = settings.Path;
		if (Directory.Exists(path))
		{
			AnsiConsole.MarkupLine("[red]请传入目录页图片的路径，而不是目录：{0}[/]", path.EscapeMarkup());
			return 1;
		}
		if (!File.Exists(path))
		{
			AnsiConsole.MarkupLine("[red]找不到图片文件：{0}[/]", path.EscapeMarkup());
			return 1;
		}
		var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
		if (dir is null)
		{
			AnsiConsole.MarkupLine("[red]无法确定图片所在目录：{0}[/]", path.EscapeMarkup());
			return 1;
		}
		// 目录中已有的、以数字命名的图片，用来校验识别结果。
		var files = GetNumberedFiles(dir);
		if (files.Count == 0)
		{
			AnsiConsole.MarkupLine("[red]目录中没有以数字命名的图片文件：{0}[/]", dir.EscapeMarkup());
			return 1;
		}

		var api = GetOllamaApi(settings.Model, out var model);
		var imageBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(path, cancellationToken));
		var maxAttempts = Math.Clamp(settings.Retries, 1, 10);
		var timeoutSeconds = Math.Clamp(settings.Timeout, 10, 3600);
		List<Result>? items = null;
		List<Result>? rejected = null;
		string? problem = null;
		await AnsiConsole.Status().StartAsync("正在识别目录...", async ctx =>
		{
			for (var attempt = 1; attempt <= maxAttempts && items is null; attempt++)
			{
				ctx.Status($"第 {attempt}/{maxAttempts} 次识别目录（{model}）...");
				var prompt = BuildPrompt(settings.ExtraPrompt, problem, !settings.NoChapter);
				// 每次尝试都用新的会话，避免上一次的错误输出影响这次的结果。
				var chat = CreateChat(api, settings.Think);
				string? output;
				// 模型跑飞时不要一直等下去。
				using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				attemptCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
				try
				{
					output = await SendAsync(chat, prompt, imageBase64, attemptCts.Token);
				}
				catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
				{
					problem = $"本次识别 {timeoutSeconds} 秒仍未结束，已中止";
					ctx.Status($"第 {attempt} 次识别超时，正在重试...");
					continue;
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception e)
				{
					problem = $"调用模型失败：{e.Message}";
					continue;
				}
				if (!TryParseResults(output, out var parsed, out var parseError))
				{
					problem = parseError;
					ctx.Status($"第 {attempt} 次输出无法解析，正在重试...");
					continue;
				}
				// 兼容模型把真正的页码写进标题、只输出 0,1,2… 序号的毛病。
				TryRepairSequentialIndex(parsed);
				var failure = Validate(parsed);
				if (failure is null)
				{
					// 少量页码对不上文件是正常的（缺页、目录里有下期预告等），
					// 但对不上的太多就说明页码本身识别错了。
					var unmatched = GetUnmatched(parsed, files, out _);
					if (unmatched.Count > 0 && unmatched.Count * 3 > parsed.Count)
					{
						failure = $"这些页码找不到对应的图片文件：{string.Join("、", unmatched)}";
					}
				}
				if (failure is null)
				{
					items = parsed;
					break;
				}
				problem = failure;
				rejected = parsed;
				ctx.Status($"第 {attempt} 次结果未通过校验，正在重试...");
			}
		});

		if (items is null)
		{
			AnsiConsole.MarkupLine("[red]识别失败：{0}[/]", (problem ?? "未知原因").EscapeMarkup());
			if (rejected is not null && rejected.Count > 0)
			{
				AnsiConsole.MarkupLine("[yellow]最后一次识别的结果如下（仅供参考，未重命名）：[/]");
				Render(rejected, files, 0);
			}
			AnsiConsole.MarkupLine("[yellow]可以试着调整提示词，例如：dotnet run recognize-content.cs -- <path> \"页码是右侧圆形里的数字\"[/]");
			return 1;
		}

		// 目录上的页码有时与文件名并不完全一致（缺封面页、编号从 0 开始等），这里自动对齐。
		var offset = ChooseOffset(items.Select(item => item.PageId).ToList(), files);
		if (offset != 0)
		{
			AnsiConsole.MarkupLine($"[yellow]目录页码与文件编号相差 {offset:+#;-#;0}，已自动对齐。[/]");
		}
		var plan = BuildPlan(items, files, offset, out var missing);
		Render(items, files, offset);
		if (missing.Count > 0)
		{
			AnsiConsole.MarkupLine("[red]这些页码找不到对应的图片文件：{0}[/]", string.Join("、", missing).EscapeMarkup());
		}
		if (plan.Count == 0)
		{
			AnsiConsole.MarkupLine("[red]没有需要重命名的文件。[/]");
			return 1;
		}
		if (settings.DryRun)
		{
			AnsiConsole.MarkupLine("[yellow]dry-run：只显示结果，没有重命名文件。[/]");
			return 0;
		}
		if (!settings.Yes)
		{
			// 重定向输入或 CI 环境中无法交互，此时不要瞎猜，直接停下。
			if (!AnsiConsole.Profile.Capabilities.Interactive)
			{
				AnsiConsole.MarkupLine("[yellow]当前不是交互式终端，为避免误改文件已停止；确认结果无误后可以加 --yes 直接重命名。[/]");
				return 1;
			}
			if (!AnsiConsole.Confirm("是否重命名文件？"))
			{
				return 0;
			}
		}
		var renamed = 0;
		var failed = 0;
		foreach (var pair in plan)
		{
			var error = TryRename(System.IO.Path.Join(dir, pair.OriginName), System.IO.Path.Join(dir, pair.TargetName));
			if (error is null)
			{
				renamed++;
				continue;
			}
			failed++;
			AnsiConsole.MarkupLine("[red]文件重命名失败：{0} → {1}[/]", pair.OriginName.EscapeMarkup(), pair.TargetName.EscapeMarkup());
			AnsiConsole.MarkupLine("    {0}", error.EscapeMarkup());
		}
		AnsiConsole.MarkupLine($"[green]重命名完成：成功 {renamed} 个[/]" + (failed > 0 ? $"[red]，失败 {failed} 个[/]" : "。"));
		if (failed > 0)
		{
			AnsiConsole.MarkupLine("[yellow]如果全部失败并且提示「拒绝访问」，说明当前进程对目录 {0} 没有写权限：可能运行在受限的终端/沙箱里，或被「受控文件夹访问」等安全策略拦下；只有个别文件失败则是被看图软件、资源管理器预览占用。[/]", dir.EscapeMarkup());
		}
		return failed == 0 ? 0 : 1;
	}

	/// <summary>
	/// 重命名文件。占用类失败会重试一次，并把系统错误翻译成好排查的说明。
	/// </summary>
	private static string? TryRename(string source, string target)
	{
		// 不覆盖已有文件，避免把上次的结果冲掉。
		if (File.Exists(target))
		{
			return "目标文件已存在，未覆盖。";
		}
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				File.Move(source, target);
				return null;
			}
			catch (Exception) when (attempt < 2)
			{
				// 刚下载或刚解压的文件可能正被缩略图、杀毒扫描短暂占用，稍等再试一次。
				Thread.Sleep(300);
			}
			catch (Exception e)
			{
				return e switch
				{
					UnauthorizedAccessException => $"{e.Message}（当前进程对所在目录没有写权限，或被安全策略阻止）",
					IOException => $"{e.Message}（文件可能被其他程序占用）",
					_ => e.Message,
				};
			}
		}
	}

	/// <summary>
	/// 返回 ollama API。
	/// </summary>
	private static OllamaApiClient GetOllamaApi(string? model, out string selectedModel)
	{
		var saveConfig = false;
		var config = configHolder.Config;
		if (config.OllamaUri == null)
		{
			config.OllamaUri = AnsiConsole.Ask<string>("请输入 Ollama 的 [green]API URL[/]:", "http://localhost:11434");
			saveConfig = true;
		}
		if (!string.IsNullOrWhiteSpace(model))
		{
			// 命令行显式指定的模型优先，并记到配置里方便下次使用。
			if (config.Model != model)
			{
				config.Model = model;
				saveConfig = true;
			}
		}
		if (config.Model == null)
		{
			config.Model = AnsiConsole.Ask<string>("请输入[green]要使用的模型[/]:");
			saveConfig = true;
		}
		if (saveConfig)
		{
			configHolder.Save();
		}
		selectedModel = config.Model;
		return new OllamaApiClient(config.OllamaUri, config.Model);
	}

	/// <summary>
	/// 创建会话。采样参数固定为确定性输出：温度 0、贪心解码，避免同一张图每次识别结果都不一样。
	/// </summary>
	private static Chat CreateChat(OllamaApiClient api, string? think)
	{
		var chat = new Chat(api)
		{
			Options = new RequestOptions
			{
				Temperature = 0,
				TopP = 1,
				TopK = 1,
				RepeatPenalty = 1,
				NumCtx = 8192,
				NumPredict = 4096,
				Seed = 42,
			},
		};
		if (!string.IsNullOrWhiteSpace(think))
		{
			chat.Think = think;
		}
		return chat;
	}

	/// <summary>
	/// 组装提示词：默认提示 + 用户额外提示 + 上一次失败的原因。
	/// </summary>
	private static string BuildPrompt(string? extraPrompt, string? problem, bool includeChapter)
	{
		var sb = new StringBuilder(DefaultPrompt.Replace("{chapterRule}", includeChapter ? ChapterRule : NoChapterRule));
		if (!string.IsNullOrWhiteSpace(extraPrompt))
		{
			sb.AppendLine().AppendLine().AppendLine("额外要求：").Append(extraPrompt.Trim());
		}
		if (!string.IsNullOrWhiteSpace(problem))
		{
			sb.AppendLine().AppendLine()
				.AppendLine("注意：上一次的输出不合格，原因是：")
				.AppendLine(problem.Trim())
				.AppendLine("请重新仔细辨认图中每一行的页码与标题，按上面的要求重新输出完整的 JSON 数组。");
		}
		return sb.ToString();
	}

	/// <summary>
	/// 发送请求并收集输出。
	/// </summary>
	private static async Task<string?> SendAsync(Chat chat, string prompt, string imageBase64, CancellationToken cancellationToken)
	{
		var sb = new StringBuilder();
		await foreach (var token in chat.SendAsync(prompt, null, new[] { imageBase64 }, ResponseSchema, cancellationToken))
		{
			sb.Append(token);
		}
		return sb.ToString();
	}

	/// <summary>
	/// 解析输出。容忍模型加上 Markdown 代码块或前后说明文字。
	/// </summary>
	private static bool TryParseResults(string? output, out List<Result> results, out string? error)
	{
		results = new();
		if (string.IsNullOrWhiteSpace(output))
		{
			error = "模型没有输出任何内容。";
			return false;
		}
		var json = ExtractJsonArray(output);
		if (json is null)
		{
			error = $"输出里没有找到 JSON 数组：{Truncate(output.Trim(), 200)}";
			return false;
		}
		try
		{
			var items = JsonSerializer.Deserialize<List<Result>>(json, JsonOptions);
			if (items is null)
			{
				error = "输出是 null，不是条目数组。";
				return false;
			}
			// 去掉模型可能插入的 null 条目。
			results = items.Where(item => item is not null).ToList();
			error = null;
			return true;
		}
		catch (JsonException e)
		{
			error = $"输出不是合法的 JSON（{e.Message}）：{Truncate(json, 200)}";
			return false;
		}
	}

	/// <summary>
	/// 从输出里截取第一个 `[` 到最后一个 `]` 之间的内容。
	/// </summary>
	private static string? ExtractJsonArray(string output)
	{
		var start = output.IndexOf('[');
		var end = output.LastIndexOf(']');
		if (start < 0 || end <= start)
		{
			return null;
		}
		return output.Substring(start, end - start + 1);
	}

	/// <summary>
	/// 校验识别结果的结构，返回 null 表示通过，否则返回可以回填给模型的原因。
	/// </summary>
	private static string? Validate(List<Result> items)
	{
		if (items.Count == 0)
		{
			return "没有输出任何条目（数组为空）。";
		}
		List<string> problems = new();
		for (var i = 0; i < items.Count; i++)
		{
			var item = items[i];
			if (string.IsNullOrWhiteSpace(item.Title))
			{
				problems.Add($"第 {i + 1} 条没有标题");
			}
			if (item.PageId <= 0)
			{
				problems.Add($"第 {i + 1} 条的页码是「{item.PageId}」，不是有效的页码");
			}
		}
		var duplicates = items.GroupBy(item => item.PageId).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
		if (duplicates.Length > 0)
		{
			problems.Add($"页码重复：{string.Join("、", duplicates)}");
		}
		for (var i = 1; i < items.Count; i++)
		{
			if (items[i].PageId <= items[i - 1].PageId)
			{
				problems.Add("页码没有按从小到大的顺序排列，顺序可能错乱");
				break;
			}
		}
		if (IsSequentialIndex(items))
		{
			problems.Add($"页码看起来是 0,1,2… 这样的序号（{string.Join(",", items.Select(item => item.PageId))}），不是图上印的页码");
		}
		return problems.Count == 0 ? null : string.Join("；", problems) + "。";
	}

	/// <summary>
	/// 判断页码是否是 0,1,2… 或 1,2,3… 这样的序号。
	/// </summary>
	private static bool IsSequentialIndex(List<Result> items)
	{
		if (items.Count < 3)
		{
			return false;
		}
		var start = items[0].PageId;
		if (start != 0 && start != 1)
		{
			return false;
		}
		for (var i = 1; i < items.Count; i++)
		{
			if (items[i].PageId != start + i)
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>
	/// 当页码是序号时，把标题里真正的页码提到 PageId 上。
	/// </summary>
	private static bool TryRepairSequentialIndex(List<Result> items)
	{
		if (!IsSequentialIndex(items))
		{
			return false;
		}
		// 先尝试标题开头的页码，再尝试标题末尾的页码。
		foreach (var fromStart in new[] { true, false })
		{
			List<Result> repaired = new(items.Count);
			var success = true;
			foreach (var item in items)
			{
				var title = item.Title ?? string.Empty;
				var match = fromStart ? TitleLeadingNumberRegex.Match(title) : TitleTrailingNumberRegex.Match(title);
				if (!match.Success || !int.TryParse(match.Groups[fromStart ? 1 : 2].ValueSpan, out var pageId))
				{
					success = false;
					break;
				}
				var newTitle = match.Groups[fromStart ? 2 : 1].Value.Trim();
				if (newTitle.Length == 0)
				{
					success = false;
					break;
				}
				repaired.Add(new Result { PageId = pageId, Title = newTitle });
			}
			if (!success)
			{
				continue;
			}
			// 真正的页码应该是递增的。
			var increasing = true;
			for (var i = 1; i < repaired.Count; i++)
			{
				if (repaired[i].PageId <= repaired[i - 1].PageId)
				{
					increasing = false;
					break;
				}
			}
			if (!increasing)
			{
				continue;
			}
			items.Clear();
			items.AddRange(repaired);
			return true;
		}
		return false;
	}

	/// <summary>
	/// 选择页码与文件编号之间的偏移量，使尽可能多的页码能对应到已有文件。
	/// </summary>
	private static int ChooseOffset(IReadOnlyList<int> pageIds, Dictionary<int, string> files)
	{
		if (pageIds.Count == 0)
		{
			return 0;
		}
		var best = 0;
		var bestScore = 0;
		for (var offset = -20; offset <= 20; offset++)
		{
			var score = pageIds.Count(id => files.ContainsKey(id + offset));
			if (score > bestScore || (score == bestScore && score > 0 && Math.Abs(offset) < Math.Abs(best)))
			{
				best = offset;
				bestScore = score;
			}
		}
		return bestScore == 0 ? 0 : best;
	}

	/// <summary>
	/// 找出在当前偏移量下找不到对应文件的页码。
	/// </summary>
	private static List<int> GetUnmatched(List<Result> items, Dictionary<int, string> files, out int offset)
	{
		var chosen = ChooseOffset(items.Select(item => item.PageId).ToList(), files);
		offset = chosen;
		return items.Where(item => !files.ContainsKey(item.PageId + chosen)).Select(item => item.PageId).ToList();
	}

	/// <summary>
	/// 生成重命名计划。
	/// </summary>
	private static List<RenamePair> BuildPlan(List<Result> items, Dictionary<int, string> files, int offset, out List<int> missing)
	{
		missing = new();
		List<RenamePair> plan = new(items.Count);
		HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);
		foreach (var item in items)
		{
			if (!files.TryGetValue(item.PageId + offset, out var fileName))
			{
				missing.Add(item.PageId);
				continue;
			}
			var number = FileNumberRegex.Match(fileName).Value;
			var title = NormalizeTitle(StripPageId(item.Title ?? string.Empty, item.PageId));
			if (title.Length == 0 || string.IsNullOrEmpty(number))
			{
				continue;
			}
			var ext = System.IO.Path.GetExtension(fileName);
			// Windows 单个文件名最长 255 个字符，这里留足余量。
			var targetName = Truncate($"{number} {title}", 200 - ext.Length) + ext;
			if (string.Equals(targetName, fileName, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			// 目标文件名重复时追加序号，避免互相覆盖。
			for (var i = 2; targets.Contains(targetName); i++)
			{
				targetName = Truncate($"{number} {title}", 195 - ext.Length) + $" ({i})" + ext;
			}
			targets.Add(targetName);
			plan.Add(new RenamePair(fileName, targetName));
		}
		return plan;
	}

	/// <summary>
	/// 规范化标题：换行合并为空格、替换非法文件名字符。
	/// </summary>
	private static string NormalizeTitle(string title)
	{
		var normalized = WhitespaceRegex.Replace(title, " ").Trim();
		normalized = InvalidFileNameCharsRegex.Replace(normalized, "_");
		// Windows 不允许文件名以空格或点结尾。
		return normalized.TrimEnd('.', ' ').Trim();
	}

	/// <summary>
	/// 标题里已经带了页码时去掉它。
	/// </summary>
	private static string StripPageId(string title, int pageId)
	{
		var match = TitleLeadingNumberRegex.Match(title);
		if (match.Success && int.TryParse(match.Groups[1].ValueSpan, out var leading) && leading == pageId)
		{
			title = match.Groups[2].Value.Trim();
		}
		match = TitleTrailingNumberRegex.Match(title);
		if (match.Success && int.TryParse(match.Groups[2].ValueSpan, out var trailing) && trailing == pageId)
		{
			title = match.Groups[1].Value.Trim();
		}
		return title;
	}

	/// <summary>
	/// 列出目录中已有的、以数字命名的图片文件，键为文件名开头的数字。
	/// </summary>
	private static Dictionary<int, string> GetNumberedFiles(string dir)
	{
		Dictionary<int, string> files = new();
		foreach (var file in Directory.EnumerateFiles(dir))
		{
			if (!ImageExtensions.Contains(System.IO.Path.GetExtension(file)))
			{
				continue;
			}
			var name = System.IO.Path.GetFileName(file);
			var match = FileNumberRegex.Match(name);
			if (!match.Success || !int.TryParse(match.Groups[1].ValueSpan, out var number))
			{
				continue;
			}
			files[number] = name;
		}
		return files;
	}

	/// <summary>
	/// 以表格形式显示识别结果。
	/// </summary>
	private static void Render(List<Result> items, Dictionary<int, string> files, int offset)
	{
		var table = new Table().Border(TableBorder.Rounded);
		table.AddColumn(new TableColumn("[green]页码[/]").RightAligned());
		table.AddColumn(new TableColumn("[green]标题[/]"));
		table.AddColumn(new TableColumn("[green]对应文件[/]"));
		foreach (var item in items)
		{
			var exists = files.TryGetValue(item.PageId + offset, out var fileName);
			table.AddRow(
				new Markup(item.PageId.ToString()),
				new Markup(Markup.Escape(item.Title ?? string.Empty)),
				exists ? new Markup(Markup.Escape(fileName!)) : new Markup("[red]未找到[/]"));
		}
		AnsiConsole.Write(table);
	}

	/// <summary>
	/// 截断过长的文本。
	/// </summary>
	private static string Truncate(string text, int length)
	{
		return text.Length <= length ? text : text[..length] + "…";
	}

	/// <summary>
	/// 配置。
	/// </summary>
	private class Config
	{
		/// <summary>
		/// ollama 接口 URI。
		/// </summary>
		public string? OllamaUri { get; set; }
		/// <summary>
		/// 使用的模型。
		/// </summary>
		public string? Model { get; set; }
	}

	private class Result
	{
		/// <summary>
		/// 页码。
		/// </summary>
		public int PageId { get; set; }
		/// <summary>
		/// 标题。
		/// </summary>
		public string? Title { get; set; }
	}

	private struct RenamePair(string originName, string targetName)
	{
		public string OriginName { get; init; } = originName;
		public string TargetName { get; init; } = targetName;
	}
}
