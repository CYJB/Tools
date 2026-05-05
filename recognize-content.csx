/**
 * 识别漫画的目录，并重命名相关图片。
 */
#load "utils/config-holder.csx"
#r "nuget: System.Text.Json, 10.0.5"
#r "nuget: OllamaSharp, 5.4.25"
#r "nuget: Spectre.Console, 0.54.0"
#r "nuget: Spectre.Console.Cli, 0.53.1"

#nullable enable

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.RegularExpressions;
using System.Threading;
using OllamaSharp;
using OllamaSharp.Models.Chat;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp<RecognizeCotnentCommand>();
return await app.RunAsync(Args.ToArray());

/// <summary>
/// 识别漫画目录的命令。
/// </summary>
sealed class RecognizeCotnentCommand : AsyncCommand<RecognizeCotnentCommand.Settings>
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
	}

	private static readonly ConfigHolder<Config> configHolder = new();
	private static readonly Regex PageIdBeforeTitleRegex = new(@"^(\d+)(?:\s+|[^\d])(.*)$");
	private static readonly Regex PageIdAfterTitleRegex = new(@"^(.*)(?:\s+|[^\d])(\d+)$");

	/// <summary>
	/// 执行命令。
	/// </summary>
	public override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
	{
		// 通过 ollama API 识别目录内容。
		Chat chat = new(GetOllamaApi());
		string path = settings.Path;
		string extraPrompt = settings.ExtraPrompt ?? "";
		var imageBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(path));
		var format = JsonSerializerOptions.Default.GetJsonSchemaAsNode(typeof(Result[]));
		StringBuilder result = new();
		await AnsiConsole.Status().StartAsync($"分析目录...", async ctx =>
		{
			var prompt = "识别图中的目录，返回每个条目的标题和相关页码，忽略无法与页面关联的标题。" + extraPrompt;
			var imagesAsBase64 = new string[] { imageBase64 };
			// 最多重试两次。
			for (int i = 0; result.Length == 0 && i < 2; i++)
			{
				await foreach (var token in chat.SendAsync(prompt, null, imagesAsBase64, format, cancellationToken))
				{
					result.Append(token);
				}
			}
		});
		var items = JsonSerializer.Deserialize<Result[]>(result.ToString())!;
		if (items.Length == 0)
		{
			AnsiConsole.MarkupLine("[red]未识别到目录[/]");
			return 0;
		}
		// 识别部分失败模式。
		if (!DetectFailure1(items))
		{
			DetectFailure2(items);
		}
		var fileNameLen = Path.GetFileNameWithoutExtension(path).Length;
		var ext = Path.GetExtension(path);
		var dir = Path.GetDirectoryName(path);
		// 寻找每个目录项对应的文件。
		List<RenamePair> renameItems = new(items.Length);
		Regex invalidFileNameChars = new($"[{Regex.Escape(new string(Path.GetInvalidFileNameChars()))}]");
		for (int i = 0; i < items.Length; i++)
		{
			var originName = items[i].PageId.ToString().PadLeft(fileNameLen, '0');
			var title = items[i].Title;
			// 将换行替换为空格。
			title = title.Replace('\n', ' ');
			// 将非法文件名字符替换为 _。
			title = invalidFileNameChars.Replace(title, "_");
			var targetName = $"{originName} {title}{ext}";
			originName += ext;
			if (File.Exists(Path.Join(dir, originName)))
			{
				renameItems.Add(new RenamePair(originName, targetName));
			}
			else
			{
				AnsiConsole.MarkupLine("[red]未找到文件 {0}[/]", targetName.EscapeMarkup());
			}
		}
		if (renameItems.Count > 0 && AnsiConsole.Confirm("是否重命名文件？"))
		{
			foreach (var item in renameItems)
			{
				try
				{
					File.Move(Path.Join(dir, item.OriginName), Path.Join(dir, item.TargetName));
				}
				catch (Exception e)
				{
					AnsiConsole.MarkupLine("[red]文件重命名失败: {0}[/]", e);
				}
			}
			Console.WriteLine("重命名完毕！");
		}
		return 0;
	}

	/// <summary>
	/// 返回 ollama API。
	/// </summary>
	private static OllamaApiClient GetOllamaApi()
	{
		var saveConfig = false;
		var config = configHolder.Config;
		if (config.OllamaUri == null)
		{
			config.OllamaUri = AnsiConsole.Ask<string>("请输入 Ollama 的 [green]API URL[/]:", "http://localhost:11434");
			saveConfig = true;
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
		return new OllamaApiClient(config.OllamaUri, config.Model);
	}

	/// <summary>
	/// 识别一种常见的失败模式 1：PageId 是顺序编号，此时页码很可能在 Title 里。
	/// </summary>
	private static bool DetectFailure1(Result[] items)
	{
		var len = items.Length;
		int start = items[0].PageId;
		for (int i = 1; i < len; i++)
		{
			if (items[i].PageId != start + i)
			{
				return false;
			}
		}
		// 尝试从 Title 中识别页码，优先识别 Title 前的页码。
		int[] newPageId = new int[len];
		string[] newTitle = new string[len];
		bool success = true;
		for (int i = 0; i < len; i++)
		{
			if (!TryMatchStartPageId(items[i].Title, out newPageId[i], out newTitle[i]))
			{
				success = false;
				break;
			}
		}
		if (!success)
		{
			// 其次识别 Title 后的页码。
			success = true;
			for (int i = 0; i < len; i++)
			{
				if (!TryMatchEndPageId(items[i].Title, out newPageId[i], out newTitle[i]))
				{
					success = false;
					break;
				}
			}
		}
		if (success)
		{
			for (int i = 0; i < len; i++)
			{
				items[i].PageId = newPageId[i];
				items[i].Title = newTitle[i];
			}
		}
		return success;
	}

	/// <summary>
	/// 识别一种常见的失败模式 2：PageId 在 Title 的开头或末尾。
	/// </summary>
	private static bool DetectFailure2(Result[] items)
	{
		var len = items.Length;
		bool isStart = true, isEnd = true;
		for (int i = 0; i < len; i++)
		{
			var item = items[i];
			if (isStart && (!TryMatchStartPageId(item.Title, out int pageId, out _) || pageId != item.PageId))
			{
				isStart = false;
				if (!isEnd)
				{
					return false;
				}
			}
			if (isEnd && (!TryMatchEndPageId(item.Title, out pageId, out _) || pageId != item.PageId))
			{
				isEnd = false;
				if (!isStart)
				{
					return false;
				}
			}
		}
		if (isStart)
		{
			for (int i = 0; i < len; i++)
			{
				var item = items[i];
				if (TryMatchStartPageId(item.Title, out _, out string title))
				{
					item.Title = title;
				}
			}
		}
		if (isEnd)
		{
			for (int i = 0; i < len; i++)
			{
				var item = items[i];
				if (TryMatchEndPageId(item.Title, out _, out string title))
				{
					item.Title = title;
				}
			}
		}
		return true;
	}

	/// <summary>
	/// 尝试匹配起始位置的页码。
	/// </summary>
	private static bool TryMatchStartPageId(string title, out int pageId, out string newTitle)
	{
		var match = PageIdBeforeTitleRegex.Match(title);
		if (match.Success)
		{
			pageId = int.Parse(match.Groups[1].ValueSpan);
			newTitle = match.Groups[2].Value;
			return true;
		}
		pageId = 0;
		newTitle = string.Empty;
		return false;
	}

	/// <summary>
	/// 尝试匹配结束位置的页码。
	/// </summary>
	private static bool TryMatchEndPageId(string title, out int pageId, out string newTitle)
	{
		var match = PageIdAfterTitleRegex.Match(title);
		if (match.Success)
		{
			pageId = int.Parse(match.Groups[2].ValueSpan);
			newTitle = match.Groups[1].Value;
			return true;
		}
		pageId = 0;
		newTitle = string.Empty;
		return false;
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
		public required string Title { get; set; }
	}

	private struct RenamePair(string originName, string targetName)
	{
		public string OriginName { get; init; } = originName;
		public string TargetName { get; init; } = targetName;
	}
}
