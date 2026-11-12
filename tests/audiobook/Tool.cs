using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Audiobook.Tests;

/// <summary>
/// 一次外部进程调用的结果。
/// </summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
	/// <summary>合并后的输出，断言提示信息时用。</summary>
	public string Output => StdOut + StdErr;

	/// <summary>
	/// 把所有空白折叠成单个空格后的输出。
	/// </summary>
	/// <remarks>
	/// Spectre 会按控制台宽度给人看的消息折行，输出被重定向时也会折，
	/// 于是「不是 m4b 容器」这种短语可能被拆到两行，直接 Contains 就匹配不上。
	/// 断言消息内容时一律用这个。
	/// </remarks>
	public string FlatOutput => Text.Flat(Output);
}

/// <summary>
/// 文本断言的辅助方法。
/// </summary>
public static class Text
{
	/// <summary>
	/// 把所有空白折叠成单个空格。
	/// </summary>
	/// <remarks>
	/// Spectre 会按控制台宽度给人看的消息折行，输出被重定向时同样会折，
	/// 于是「不是 m4b 容器」这种短语可能被拆到两行，直接 Contains 就匹配不上。
	/// </remarks>
	public static string Flat(string text)
	{
		return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
	}
}

/// <summary>
/// 跑外部进程的辅助方法。测试里不用任何 shell，全部走 Process，保证跨平台。
/// </summary>
public static class Proc
{
	/// <summary>
	/// 执行指定的命令。
	/// </summary>
	public static ProcessResult Run(string fileName, IEnumerable<string> args, string? workingDir = null)
	{
		using Process process = new();
		process.StartInfo = new ProcessStartInfo(fileName, args)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory(),
			StandardOutputEncoding = new UTF8Encoding(false),
			StandardErrorEncoding = new UTF8Encoding(false),
		};
		process.StartInfo.Environment["DOTNET_NOLOGO"] = "1";
		process.StartInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
		StringBuilder output = new(), error = new();
		process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
		process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };
		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		process.WaitForExit();
		return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
	}

	/// <summary>
	/// 执行指定的命令，非 0 退出码时让测试失败并带上完整输出。
	/// </summary>
	public static string RunChecked(string fileName, IEnumerable<string> args, string? workingDir = null)
	{
		ProcessResult result = Run(fileName, args, workingDir);
		Assert.True(result.ExitCode == 0,
			$"{Path.GetFileName(fileName)} 退出码 {result.ExitCode}\n参数: {string.Join(' ', args)}\n输出:\n{result.Output}");
		return result.StdOut;
	}

	/// <summary>
	/// 执行指定的命令并返回标准输出的原始字节。用于取 PCM 这类二进制数据，
	/// 按字符串读会被编码转换破坏。
	/// </summary>
	public static byte[] RunBytes(string fileName, IEnumerable<string> args)
	{
		using Process process = new();
		process.StartInfo = new ProcessStartInfo(fileName, args)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		process.Start();
		using MemoryStream buffer = new();
		Task copy = process.StandardOutput.BaseStream.CopyToAsync(buffer);
		string error = process.StandardError.ReadToEnd();
		process.WaitForExit();
		copy.Wait();
		Assert.True(process.ExitCode == 0,
			$"{Path.GetFileName(fileName)} 退出码 {process.ExitCode}\n参数: {string.Join(' ', args)}\n{error}");
		return buffer.ToArray();
	}
}

/// <summary>
/// ffprobe 的结果，压平成方便断言的字段。
/// </summary>
public sealed class Probe
{
	private readonly FFprobeJson json;

	private Probe(FFprobeJson json, string rawJson)
	{
		this.json = json;
		RawJson = rawJson;
	}

	/// <summary>ffprobe 的原始 JSON。</summary>
	public string RawJson { get; }

	/// <summary>探测指定的文件。</summary>
	public static Probe Of(string file)
	{
		string raw = Proc.RunChecked(TestEnv.FFprobe, ["-v", "error", "-print_format", "json",
			"-show_format", "-show_streams", "-show_chapters", file]);
		var parsed = JsonSerializer.Deserialize<FFprobeJson>(raw);
		Assert.NotNull(parsed);
		return new Probe(parsed!, raw);
	}

	private FFprobeFormat Format => json.Format ?? new FFprobeFormat();
	private FFprobeStream[] Streams => json.Streams ?? [];
	private FFprobeChapter[] Chapters => json.Chapters ?? [];
	private FFprobeStream Audio => Streams.FirstOrDefault(s => s.CodecType == "audio") ?? new FFprobeStream();

	public string? Brand => Tag(Format.Tags, "major_brand");
	public string? Stik => Tag(Format.Tags, "media_type");
	public string? Title => Tag(Format.Tags, "title");
	public string? Artist => Tag(Format.Tags, "artist");
	public string? Album => Tag(Format.Tags, "album");
	public string? Genre => Tag(Format.Tags, "genre");
	public string? Date => Tag(Format.Tags, "date");
	public string? Comment => Tag(Format.Tags, "comment");

	/// <summary>章节轨的条数。它在 ffprobe 里是 codec_type=data 的流（Nero chpl），不是 subtitle。</summary>
	public int ChapterTracks => Streams.Count(s => s.CodecType == "data");
	public int VideoStreams => Streams.Count(s => s.CodecType == "video");
	public int AudioStreams => Streams.Count(s => s.CodecType == "audio");
	public bool AllAttachedPic => Streams.Where(s => s.CodecType == "video").All(s => s.Disposition?.GetValueOrDefault("attached_pic") == 1);

	public int ChapterCount => Chapters.Length;
	public string[] Titles => [.. Chapters.Select(c => Tag(c.Tags, "title") ?? "")];
	public double[] Starts => [.. Chapters.Select(c => Seconds(c.StartTime))];
	public double[] Ends => [.. Chapters.Select(c => Seconds(c.EndTime))];

	public string? Codec => Audio.CodecName;
	public int SampleRate => ParseInt(Audio.SampleRate);
	public int Channels => Audio.Channels ?? 0;
	public long BitRate => FirstPositive(ParseLong(Audio.BitRate), ParseLong(Format.BitRate));

	/// <summary>
	/// 音频流自己的时长。不能用 format.duration：它是所有流的最大值，会被章节轨撑大。
	/// </summary>
	public double AudioDuration => FirstPositive(Seconds(Audio.Duration), Seconds(Format.Duration));
	public double FormatDuration => Seconds(Format.Duration);

	/// <summary>不区分大小写地读标签。</summary>
	private static string? Tag(Dictionary<string, string>? tags, string key)
	{
		if (tags == null) return null;
		foreach (var pair in tags)
		{
			if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
		}
		return null;
	}

	private static double Seconds(string? value)
	{
		return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : 0.0;
	}

	private static int ParseInt(string? value)
	{
		return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : 0;
	}

	private static long ParseLong(string? value)
	{
		return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) ? result : 0;
	}

	private static long FirstPositive(long first, long second) => first > 0 ? first : second;
	private static double FirstPositive(double first, double second) => first > 0 ? first : second;

	/// <summary>
	/// 音频裸流的 MD5。判断 apply 是否真的无损（逐位搬运）就靠它。
	/// </summary>
	public static string AudioMd5(string file)
	{
		return Proc.RunChecked(TestEnv.FFmpeg, ["-v", "error", "-i", file, "-map", "0:a", "-c", "copy", "-f", "md5", "-"]).Trim();
	}
}

#region ffprobe JSON 模型

sealed class FFprobeJson
{
	[JsonPropertyName("format")] public FFprobeFormat? Format { get; set; }
	[JsonPropertyName("streams")] public FFprobeStream[]? Streams { get; set; }
	[JsonPropertyName("chapters")] public FFprobeChapter[]? Chapters { get; set; }
}

sealed class FFprobeFormat
{
	[JsonPropertyName("duration")] public string? Duration { get; set; }
	[JsonPropertyName("bit_rate")] public string? BitRate { get; set; }
	[JsonPropertyName("tags")] public Dictionary<string, string>? Tags { get; set; }
}

sealed class FFprobeStream
{
	[JsonPropertyName("codec_name")] public string? CodecName { get; set; }
	[JsonPropertyName("codec_type")] public string? CodecType { get; set; }
	[JsonPropertyName("sample_rate")] public string? SampleRate { get; set; }
	[JsonPropertyName("channels")] public int? Channels { get; set; }
	[JsonPropertyName("bit_rate")] public string? BitRate { get; set; }
	[JsonPropertyName("duration")] public string? Duration { get; set; }
	[JsonPropertyName("disposition")] public Dictionary<string, int>? Disposition { get; set; }
}

sealed class FFprobeChapter
{
	[JsonPropertyName("start_time")] public string? StartTime { get; set; }
	[JsonPropertyName("end_time")] public string? EndTime { get; set; }
	[JsonPropertyName("tags")] public Dictionary<string, string>? Tags { get; set; }
}

#endregion
