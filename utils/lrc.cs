#!/usr/bin/env -S dotnet --
#:include code-pages.cs
#nullable enable

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// lrc 歌词的一行。
/// </summary>
/// <param name="TimeMs">相对所属音频文件开头的毫秒数（lrc 的时间戳不是整篇的）。</param>
/// <param name="Text">歌词文本。</param>
sealed record LrcLine(long TimeMs, string Text);

/// <summary>
/// 一条歌词字幕：整篇时间轴上的开始和结束时间。
/// </summary>
/// <param name="StartMs">开始时间（毫秒）。</param>
/// <param name="EndMs">结束时间（毫秒）。</param>
/// <param name="Text">歌词文本。</param>
sealed record LrcCue(long StartMs, long EndMs, string Text);

/// <summary>
/// 一段歌词：一个音频文件的歌词行 + 它在整篇时间轴上的位置。
/// </summary>
/// <param name="Lines">歌词行，时间相对这个音频文件的开头。</param>
/// <param name="StartMs">这一段在整篇时间轴上的起点。</param>
/// <param name="LengthMs">这一段的长度，超出这个长度的行会被丢掉。</param>
sealed record LrcSegment(IReadOnlyList<LrcLine> Lines, long StartMs, long LengthMs);

/// <summary>
/// lrc 歌词的解析、时间轴平移与序列化。
/// </summary>
/// <remarks>
/// lrc 的时间戳是相对**单个音频文件**的（每个文件都从 <c>[00:00]</c> 开始）。把多段歌词拼成
/// 一篇时不能直接连接，必须用 <see cref="BuildCues"/> 按每一段的起点平移，否则整篇的歌词会
/// 全挤在开头，而且不会有任何报错。
/// </remarks>
class Lrc
{
	/// <summary>
	/// 歌词文件的扩展名。
	/// </summary>
	public const string Extension = ".lrc";

	/// <summary>
	/// 时间戳：<c>[分:秒]</c>、<c>[分:秒.百分秒]</c>，也兼容用 <c>:</c> 分隔小数的写法。
	/// </summary>
	/// <remarks>
	/// 分钟不限 59：标准 lrc 没有小时位，一本 40 小时的书分钟数会到 2400，那是合法的。
	/// 小数部分按「位数补零到毫秒」处理，所以 <c>.5</c> 是 500ms、<c>.34</c> 是 340ms。
	/// </remarks>
	private static readonly Regex TimeTagRegex = new(@"^\s*\[(\d{1,4}):(\d{1,2})(?:[.:](\d{1,3}))?\]");

	/// <summary>
	/// 增强型 lrc 的词级时间戳（<c>&lt;分:秒.百分秒&gt;</c>），播放器用不到，剥掉。
	/// </summary>
	private static readonly Regex WordTimeRegex = new(@"<\d{1,4}:\d{1,2}(?:[.:]\d{1,3})?>");

	/// <summary>
	/// 时间戳后面的标签（<c>[tr:zh]</c> 翻译、<c>[tt]</c> 音轨标记），不是歌词文本，剥掉。
	/// </summary>
	private static readonly Regex LeadingTagRegex = new(@"^\s*\[[^\]]*\]");

	/// <summary>
	/// 判断文件是不是歌词文件（扩展名不区分大小写）。
	/// </summary>
	public static bool IsLyricFile(string path)
	{
		return Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// 找出目录里和音频同名的 lrc（<c>01.mp3</c> → <c>01.lrc</c>），键是去掉扩展名的文件名。
	/// </summary>
	public static Dictionary<string, string> FindSidecarFiles(string dir)
	{
		Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
		if (!Directory.Exists(dir))
		{
			return result;
		}
		// 排序只是为了结果稳定：同一个文件名有多个大小写不同的 lrc 时，认第一个。
		foreach (string file in Directory.EnumerateFiles(dir).OrderBy(file => Path.GetFileName(file), StringComparer.Ordinal))
		{
			if (IsLyricFile(file))
			{
				result.TryAdd(Path.GetFileNameWithoutExtension(file), file);
			}
		}
		return result;
	}

	/// <summary>
	/// 解析歌词文件。
	/// </summary>
	/// <param name="path">lrc 文件路径。</param>
	/// <returns>按时间排序的歌词行。</returns>
	public static List<LrcLine> ParseFile(string path)
	{
		return ParseFile(path, out _);
	}

	/// <summary>
	/// 解析歌词文件，顺带返回是不是按 GBK 读的。
	/// </summary>
	/// <param name="path">lrc 文件路径。</param>
	/// <param name="gbk">是否退化到 GBK 解码（默认按 UTF-8）。</param>
	/// <returns>按时间排序的歌词行。</returns>
	public static List<LrcLine> ParseFile(string path, out bool gbk)
	{
		return Parse(ReadText(path, out gbk));
	}

	/// <summary>
	/// 解析 lrc 文本。
	/// </summary>
	/// <remarks>
	/// 只保留有时间戳且有文本的行：<c>[ti:]</c> / <c>[ar:]</c> 这类头信息、只有时间戳的间奏占位
	/// 都没有可显示的文本，拼进字幕只会变成空行。
	/// </remarks>
	public static List<LrcLine> Parse(string content)
	{
		List<LrcLine> result = [];
		content = content.Replace("\r\n", "\n").Replace('\r', '\n');
		if (content.Length > 0 && content[0] == '\uFEFF')
		{
			content = content[1..];
		}
		foreach (string line in content.Split('\n'))
		{
			string rest = line;
			List<long> times = [];
			// 一行可以带多个时间戳（[00:12.00][01:20.00]同样的一句），每个都要算一条歌词。
			while (true)
			{
				Match match = TimeTagRegex.Match(rest);
				if (!match.Success)
				{
					break;
				}
				times.Add(ParseTime(match));
				rest = rest[match.Length..];
			}
			if (times.Count == 0)
			{
				continue;
			}
			string text = WordTimeRegex.Replace(rest, "");
			// 剥掉行首的标签：[00:12.00][tr:zh]翻译 里的 [tr:zh] 是语言标记，不该进字幕。
			while (true)
			{
				Match tag = LeadingTagRegex.Match(text);
				if (!tag.Success)
				{
					break;
				}
				text = text[tag.Length..];
			}
			text = text.Trim();
			if (text.Length == 0)
			{
				continue;
			}
			foreach (long time in times)
			{
				result.Add(new LrcLine(time, text));
			}
		}
		result.Sort((left, right) => left.TimeMs.CompareTo(right.TimeMs));
		return result;
	}

	/// <summary>
	/// 把各段歌词按每一段的起点平移，得到整篇时间轴的字幕。
	/// </summary>
	/// <remarks>
	/// 每行的结束时间取下一行的开始时间、末行到这一段的结尾，所以字幕首尾相接、不会互相覆盖。
	/// 超出这一段的长度（<see cref="LrcSegment.LengthMs"/>）的行会被丢掉：那说明 lrc 和音频不配套，
	/// 保留下来会让歌词一直显示到下一段。
	/// </remarks>
	public static List<LrcCue> BuildCues(IEnumerable<LrcSegment> segments)
	{
		List<LrcCue> cues = [];
		foreach (LrcSegment segment in segments)
		{
			long segmentEnd = segment.StartMs + segment.LengthMs;
			IReadOnlyList<LrcLine> lines = segment.Lines;
			for (int j = 0; j < lines.Count; j++)
			{
				long start = segment.StartMs + lines[j].TimeMs;
				if (start >= segmentEnd)
				{
					break;
				}
				long end = Math.Min(j + 1 < lines.Count ? segment.StartMs + lines[j + 1].TimeMs : segmentEnd, segmentEnd);
				if (end <= start)
				{
					continue;
				}
				cues.Add(new LrcCue(start, end, lines[j].Text));
			}
		}
		return cues;
	}

	/// <summary>
	/// 把字幕序列化成 lrc 文本，时间戳是整篇的。
	/// </summary>
	/// <remarks>
	/// 整篇一份 lrc 时，时间戳必须能表达「第 n 段」的位置，所以不能用章节时间戳那种
	/// <c>[时:分:秒]</c>：播放器会把 <c>[01:23:45]</c> 里的 01 当成分钟。
	/// </remarks>
	public static string ToLrc(IReadOnlyList<LrcCue> cues)
	{
		StringBuilder builder = new();
		foreach (LrcCue cue in cues)
		{
			builder.Append('[').Append(FormatLrcTimestamp(cue.StartMs)).Append(']').Append(cue.Text).Append('\n');
		}
		return builder.ToString().TrimEnd('\n');
	}

	/// <summary>
	/// 把字幕序列化成 srt 文本（用来转成 mov_text 字幕轨）。
	/// </summary>
	public static string ToSrt(IReadOnlyList<LrcCue> cues)
	{
		StringBuilder builder = new();
		for (int i = 0; i < cues.Count; i++)
		{
			if (i > 0)
			{
				builder.Append('\n');
			}
			builder.Append(i + 1).Append('\n')
				.Append(FormatSrtTimestamp(cues[i].StartMs)).Append(" --> ").Append(FormatSrtTimestamp(cues[i].EndMs)).Append('\n')
				.Append(cues[i].Text).Append('\n');
		}
		return builder.ToString();
	}

	/// <summary>
	/// 把毫秒格式化成 lrc 的时间戳（不含方括号）。
	/// </summary>
	public static string FormatLrcTimestamp(long ms)
	{
		// 四舍五入到百分秒：lrc 的小数位是百分秒，直接截断每行都会比真实时间早一点。
		long centiseconds = (Math.Max(0, ms) + 5) / 10;
		return string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}.{2:D2}",
			centiseconds / 6000, centiseconds / 100 % 60, centiseconds % 100);
	}

	/// <summary>
	/// 把毫秒格式化成 srt 的时间戳（时:分:秒,毫秒）。
	/// </summary>
	public static string FormatSrtTimestamp(long ms)
	{
		ms = Math.Max(0, ms);
		return string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}:{2:D2},{3:D3}",
			ms / 3600000, ms / 60000 % 60, ms / 1000 % 60, ms % 1000);
	}

	/// <summary>
	/// 把时间戳转成毫秒。
	/// </summary>
	private static long ParseTime(Match match)
	{
		long minutes = long.Parse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
		long seconds = long.Parse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture);
		string fraction = match.Groups[3].Value;
		long millis = fraction.Length == 0
			? 0
			: long.Parse(fraction.PadRight(3, '0'), NumberStyles.None, CultureInfo.InvariantCulture);
		return (minutes * 60 + seconds) * 1000 + millis;
	}

	/// <summary>
	/// 读 lrc 文本，顺带猜编码。
	/// </summary>
	/// <remarks>
	/// 歌词文件经常是 GBK（千千静听这类老工具的默认编码），按 UTF-8 硬读会静默变成乱码、
	/// 还会一路写进成品。所以先按严格 UTF-8 解码，失败再退回 GBK；带 BOM 的 UTF-16 也认。
	/// </remarks>
	private static string ReadText(string file, out bool gbk)
	{
		byte[] bytes = File.ReadAllBytes(file);
		gbk = false;
		if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
		{
			return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
		}
		if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
		{
			return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
		}
		try
		{
			return new UTF8Encoding(false, true).GetString(bytes);
		}
		catch (DecoderFallbackException)
		{
			CodePages.RegisterGBK();
			gbk = true;
			return Encoding.GetEncoding("GBK").GetString(bytes);
		}
	}
}
