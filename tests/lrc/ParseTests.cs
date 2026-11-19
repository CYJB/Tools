// 命名空间不能写成 Lrc.Tests：那会声明一个叫 Lrc 的命名空间，和被测的 class Lrc 撞名。
namespace LrcTests;

/// <summary>
/// <see cref="Lrc.Parse"/>：时间戳格式、一行多时间戳、标签剥离、排序。
/// </summary>
public sealed class ParseTests
{
	[Fact(DisplayName = "基本行：分:秒.百分秒")]
	public void ParsesBasicLines()
	{
		List<LrcLine> lines = Lrc.Parse("[00:00.00]第一句\n[00:01.50]第二句\n[00:12]第三句\n");
		Assert.Equal([0L, 1500L, 12_000L], lines.Select(line => line.TimeMs));
		Assert.Equal(["第一句", "第二句", "第三句"], lines.Select(line => line.Text));
	}

	[Theory(DisplayName = "小数位按位数补零到毫秒：.5 = 500ms，.05 = 50ms")]
	[InlineData("[00:01.5]x", 1500)]      // 一位 = 十分之一秒
	[InlineData("[00:01.05]x", 1050)]     // 两位 = 百分之一秒
	[InlineData("[00:01.50]x", 1500)]
	[InlineData("[00:01.005]x", 1005)]    // 三位 = 毫秒
	[InlineData("[00:01:50]x", 1500)]     // 少数工具用 : 分隔百分秒
	[InlineData("[00:12]x", 12_000)]      // 没有小数位
	public void PadsFractionsToMilliseconds(string content, long expected)
	{
		Assert.Equal(expected, Assert.Single(Lrc.Parse(content)).TimeMs);
	}

	[Fact(DisplayName = "分钟可以超过 59：40 小时的书分钟数到 2400")]
	public void AllowsMinutesBeyond59()
	{
		// 标准 lrc 只有「分:秒」，没有小时位。40 小时 = 2400 分钟，按分钟记是合法的，
		// 不能像章节时间戳那样限制在 59 以内。
		Assert.Equal(2400 * 60_000L, Assert.Single(Lrc.Parse("[2400:00.00]长书")).TimeMs);
	}

	[Fact(DisplayName = "一行的多个时间戳拆成多条歌词")]
	public void SplitsMultipleTimestamps()
	{
		List<LrcLine> lines = Lrc.Parse("[00:01.00][00:02.00]同一句\n");
		Assert.Equal([1000L, 2000L], lines.Select(line => line.TimeMs));
		Assert.All(lines, line => Assert.Equal("同一句", line.Text));
	}

	[Fact(DisplayName = "头信息和只有时间戳的占位行不是歌词")]
	public void IgnoresHeadersAndEmptyText()
	{
		Assert.Empty(Lrc.Parse("[ti:标题]\n[ar:某人]\n[al:专辑]\n[by:某人]\n\n[00:05.00]\n"));
	}

	[Fact(DisplayName = "剥掉词级时间戳和语言标记")]
	public void StripsWordTimestampsAndTags()
	{
		// 增强型 lrc：[00:01.00][tr:zh]翻译<00:01.50>内容
		LrcLine line = Assert.Single(Lrc.Parse("[00:01.00][tr:zh]翻译<00:01.50>内容\n"));
		Assert.Equal(1000, line.TimeMs);
		Assert.Equal("翻译内容", line.Text);
	}

	[Fact(DisplayName = "只剥行首的方括号标签，正文里的括号保留")]
	public void KeepsBracketsInsideText()
	{
		Assert.Equal("副歌【重复】", Assert.Single(Lrc.Parse("[00:01.00][Chorus]副歌【重复】\n")).Text);
	}

	[Fact(DisplayName = "输出按时间排序")]
	public void SortsByTime()
	{
		List<LrcLine> lines = Lrc.Parse("[00:03.00]c\n[00:01.00]a\n[00:02.00]b\n");
		Assert.Equal(["a", "b", "c"], lines.Select(line => line.Text));
	}

	[Fact(DisplayName = "CRLF 和 BOM 不影响解析")]
	public void HandlesCrlfAndBom()
	{
		List<LrcLine> lines = Lrc.Parse("\uFEFF[00:01.00]第一句\r\n[00:02.00]第二句\r\n");
		Assert.Equal(2, lines.Count);
		Assert.Equal("第二句", lines[1].Text);
	}

	[Fact(DisplayName = "没有时间戳的行和空内容直接忽略")]
	public void IgnoresLinesWithoutTimestamp()
	{
		Assert.Empty(Lrc.Parse("随便一行\n[abc]文本\n"));
		Assert.Empty(Lrc.Parse(""));
	}
}
