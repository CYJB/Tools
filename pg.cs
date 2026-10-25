#!/usr/bin/env -S dotnet --

/**
 * 按站点规则生成固定密码：用主密码 + 站点作为种子，保证同一站点每次都生成同样的密码。
 * 站点配置在 pg.config.json 里。
 */
#:include utils/config-holder.cs
#:package Cyjb
#:package Spectre.Console
#:package Spectre.Console.Cli
// 发布成单文件 exe：`dotnet publish pg.cs` 只产出一个 pg.exe。
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
using Cyjb;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp<PasswordCommand>();
return app.Run(args);

/// <summary>
/// 生成密码命令。
/// </summary>
sealed class PasswordCommand : Command<PasswordCommand.Settings>
{
	/// <summary>
	/// 命令的设置。
	/// </summary>
	public sealed class Settings : CommandSettings
	{
		[Description("站点的键（如 github），省略时从列表中选择。")]
		[CommandOption("-s|--site")]
		public string? Site { get; init; }

		[Description("只列出配置中的站点，不生成密码。")]
		[CommandOption("-l|--list")]
		[DefaultValue(false)]
		public bool List { get; init; }
	}

	/// <summary>
	/// 密码要求的字符范围。
	/// </summary>
	private static readonly Dictionary<char, string> CharRanges = new()
	{
		{ 'a', "abcdefghijklmnopqrstuvwxyz" },
		{ 'A', "ABCDEFGHIJKLMNOPQRSTUVWXYZ" },
		{ '0', "0123456789" },
		// { '+', "+-*/%=" },
	};

	/// <summary>
	/// 合并哈希值用的常量（黄金分割率）。
	/// </summary>
	private const int GoldenRatio = unchecked((int)0x9E3779B9);

	/// <summary>
	/// 配置。
	/// </summary>
	private static readonly ConfigHolder<Config> configHolder = new();

	/// <summary>
	/// 执行命令。
	/// </summary>
	protected override int Execute([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken)
	{
		List<Site> sites = configHolder.Config.Sites;
		if (sites.Count == 0)
		{
			AnsiConsole.MarkupLine("[red]还没有配置站点，请先在 [green]pg.config.json[/] 里填写 Sites。[/]");
			return 1;
		}
		if (settings.List)
		{
			RenderSites(sites);
			return 0;
		}
		// 检查字符范围配置，避免生成时才抛出难以理解的异常。
		foreach (Site site in sites)
		{
			foreach (char ch in site.Range)
			{
				if (!CharRanges.ContainsKey(ch))
				{
					AnsiConsole.MarkupLine($"[red]站点 {site.Key.EscapeMarkup()} 的字符范围含未知字符 {ch.ToString().EscapeMarkup()}[/]");
					return 1;
				}
			}
			if (site.MinLength < 1 || site.MaxLength <= site.MinLength)
			{
				AnsiConsole.MarkupLine($"[red]站点 {site.Key.EscapeMarkup()} 的长度配置无效：{site.MinLength}~{site.MaxLength}[/]");
				return 1;
			}
		}
		Site? selected = SelectSite(sites, settings.Site);
		if (selected == null)
		{
			return 1;
		}
		var masterPassword = AnsiConsole.Prompt(
			new TextPrompt<string>("主密码:").Secret().Validate(value => value.Length > 0
				? ValidationResult.Success()
				: ValidationResult.Error("[red]主密码不能为空[/]")));
		string password = Generate(selected, masterPassword, out int length);
		AnsiConsole.WriteLine();
		AnsiConsole.Write(new Rule($"[green]{selected.DisplayName.EscapeMarkup()}[/]").LeftJustified());
		AnsiConsole.WriteLine(password);
		AnsiConsole.MarkupLine($"[grey]{length} 位，字符范围 {selected.Range.EscapeMarkup()}[/]");
		return 0;
	}

	/// <summary>
	/// 选择要生成密码的站点。
	/// </summary>
	private static Site? SelectSite(List<Site> sites, string? key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return AnsiConsole.Prompt(
				new SelectionPrompt<Site>()
					.Title("请选择[green]站点[/]:")
					.PageSize(15)
					.UseConverter(site => site.DisplayName)
					.AddChoices(sites));
		}
		Site? site = sites.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
		if (site == null)
		{
			AnsiConsole.MarkupLine($"[red]找不到站点 {key.EscapeMarkup()}[/]");
			AnsiConsole.MarkupLine($"[grey]可用的站点：{string.Join("、", sites.Select(item => item.Key)).EscapeMarkup()}[/]");
		}
		return site;
	}

	/// <summary>
	/// 用表格显示配置的站点。
	/// </summary>
	private static void RenderSites(List<Site> sites)
	{
		var table = new Table().Border(TableBorder.Rounded);
		table.AddColumn("[green]键[/]");
		table.AddColumn("[green]名称[/]");
		table.AddColumn("[green]字符范围[/]");
		table.AddColumn(new TableColumn("[green]长度[/]").RightAligned());
		foreach (Site site in sites)
		{
			string length = site.MinLength == site.MaxLength ? site.MinLength.ToString() : $"{site.MinLength}~{site.MaxLength}";
			table.AddRow(
				new Markup(site.Key.EscapeMarkup()),
				new Markup(site.Name == null ? "" : site.Name.EscapeMarkup()),
				new Markup(site.Range.EscapeMarkup()),
				new Markup(length));
		}
		AnsiConsole.Write(table);
	}

	/// <summary>
	/// 生成指定站点的密码。
	/// </summary>
	private static string Generate(Site site, string masterPassword, out int length)
	{
		// 使用 主密码 + 站点 作为种子，保证同一站点每次生成的密码相同。
		string seedString = masterPassword + '|' + site.Key.ToLowerInvariant();
		int hashCode = seedString.Select(ch => (int)ch).Aggregate(Combine);
		var random = new MersenneTwister(hashCode);
		// 使用随机长度。
		length = random.Next(site.MinLength, site.MaxLength);
		// 计算字符范围。
		string[] requiredRanges = site.Range.Select(ch => CharRanges[ch]).ToArray();
		string charRange = string.Concat(requiredRanges);
		// 滚动生成随机序列，直到每个要求的字符范围里的字符都至少出现一次。
		foreach (string str in RandomString(random, charRange, length))
		{
			if (requiredRanges.All(range => range.IndexOfAny(str.ToCharArray()) >= 0))
			{
				return str;
			}
		}
		throw new Exception("无法生成密码");
	}

	/// <summary>
	/// 合并两个哈希值。
	/// </summary>
	private static int Combine(int seed, int hash)
	{
		return seed ^ (hash + GoldenRatio + (seed << 6) + (seed >> 2));
	}

	/// <summary>
	/// 由指定字符范围无限生成随机字符。
	/// </summary>
	private static IEnumerable<char> RandomChars(MersenneTwister random, string range)
	{
		while (true)
		{
			int idx = random.Next(0, range.Length);
			yield return range[idx];
		}
	}

	/// <summary>
	/// 由随机字符生成指定长度的滚动窗口。
	/// </summary>
	private static IEnumerable<string> RandomString(MersenneTwister random, string range, int length)
	{
		var text = new StringBuilder(length);
		foreach (char ch in RandomChars(random, range))
		{
			if (text.Length < length)
			{
				text.Append(ch);
			}
			else
			{
				yield return text.ToString();
				text.Remove(0, 1);
				text.Append(ch);
			}
		}
	}

	/// <summary>
	/// 配置。
	/// </summary>
	private class Config
	{
		/// <summary>
		/// 站点列表。
		/// </summary>
		public List<Site> Sites { get; set; } = [];
	}

	/// <summary>
	/// 站点的配置。
	/// </summary>
	private class Site
	{
		/// <summary>
		/// 站点的键，参与生成密码。
		/// </summary>
		public string Key { get; set; } = "";
		/// <summary>
		/// 站点名称，只用于显示，省略时用 Key。
		/// </summary>
		public string? Name { get; set; }
		/// <summary>
		/// 需要的字符范围，a 为小写字母、A 为大写字母、0 为数字。
		/// </summary>
		public string Range { get; set; } = "aA0";
		/// <summary>
		/// 需要的最短长度。
		/// </summary>
		public int MinLength { get; set; } = 12;
		/// <summary>
		/// 需要的最长长度。
		/// </summary>
		public int MaxLength { get; set; } = 20;
		/// <summary>
		/// 是否是旧的账号，只用于标记。
		/// </summary>
		public bool Old { get; set; }

		/// <summary>
		/// 用于显示的名称。
		/// </summary>
		public string DisplayName => string.IsNullOrEmpty(Name) ? Key : $"{Name} ({Key})";
	}
}
