using System.Runtime.InteropServices;

namespace Audiobook.Tests;

/// <summary>
/// 章节边界的累积漂移：样本级验证。
/// </summary>
/// <remarks>
/// 素材是 tone/silence 交替的 6 个文件（00=tone 01=silence 02=tone ...），
/// 所以 tone 段的真实起点就是第 0、2、4 章的开始时间。把合并结果解码成 PCM，
/// 逐样本找出每个 tone 段的起点作为真值，再和写进 m4b 的章节起点比对。
/// <para>
/// 判据是**极差**而不是绝对误差：AAC 编码有一帧 priming（约 23ms），那是恒定偏移、
/// 无法也不需要消除；真正要抓的是误差随章节序号增长——缺 Xing 头的 VBR mp3
/// 按容器时长累加时每章能多漂约 18ms，上百章就是几秒。
/// </para>
/// </remarks>
[Collection(AudiobookCollection.Name)]
public sealed class DriftTests(TestEnv env)
{
	private const int SampleRate = 44100;
	private const short Threshold = 300;
	private static readonly int Guard = (int)(0.05 * SampleRate);
	/// <summary>各章误差的极差超过这个值就认为在累积漂移。</summary>
	private const double SpreadToleranceMs = 20;

	[Theory(DisplayName = "章节边界不随文件数累积漂移")]
	[InlineData("cbr_xing")]      // 有 Xing 头，容器时长准确
	[InlineData("cbr_noxing")]    // 无 Xing 头但 CBR
	[InlineData("vbr_noxing")]    // 无 Xing 头且 VBR：最坏情况，必须触发解码路径
	[InlineData("vbr0_noxing")]
	public void ChapterBoundariesDoNotAccumulate(string fixture)
	{
		string file = env.Out($"drift-{fixture}-{Guid.NewGuid():N}.m4b");
		string log = Text.Flat(env.RunOk("merge", env.Dir(fixture), "-o", file));
		if (fixture.Contains("noxing", StringComparison.Ordinal))
		{
			Assert.Contains("缺少 Xing 头", log);
		}

		double[] truth = ToneOnsets(file);
		double[] marks = Probe.Of(file).Starts;
		Assert.True(truth.Length > 0, "没能在合并结果里找到任何 tone 段，素材可能造错了");

		List<double> errors = [];
		for (int k = 0; k < truth.Length; k++)
		{
			int index = 2 * k;   // tone 段 k 对应第 2k 章
			if (index < marks.Length) errors.Add((marks[index] - truth[k]) * 1000);
		}
		Assert.True(errors.Count >= 2, $"只对齐到 {errors.Count} 个 tone 段，不足以判断是否累积");

		double spread = errors.Max() - errors.Min();
		Assert.True(spread < SpreadToleranceMs,
			$"{fixture}：章节边界在累积漂移，极差 {spread:F1}ms\n" +
			$"  tone 段真值(样本级): [{string.Join(", ", truth.Select(t => t.ToString("F4")))}]\n" +
			$"  章节起点:            [{string.Join(", ", marks.Select(m => m.ToString("F4")))}]\n" +
			$"  逐章误差(ms):        [{string.Join(", ", errors.Select(e => e.ToString("F1")))}]");
	}

	[Fact(DisplayName = "自动检测的依据是 ffprobe 的告警文本，措辞变了要能发现")]
	public void DetectionReliesOnFFprobeWarning()
	{
		// 工具靠 ffprobe 在 -v warning 下输出的「Estimating duration from bitrate」判断容器时长是否可靠。
		// 这条断言守的是「ffmpeg 改了措辞就会静默退化成按容器时长累加」的风险。
		Assert.True(ProbeWarningCount("vbr_noxing") > 0, "缺 Xing 头的 VBR mp3 应当让 ffprobe 告警");
		Assert.Equal(0, ProbeWarningCount("cbr_xing"));
	}

	private int ProbeWarningCount(string fixture)
	{
		string file = Directory.GetFiles(env.Dir(fixture), "*.mp3")[0];
		ProcessResult result = Proc.Run(TestEnv.FFprobe, ["-v", "warning", "-i", file]);
		return result.Output.Split('\n').Count(line => line.Contains("Estimating duration", StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// 把合并结果解码成 PCM，逐样本找出每个 tone 段的起点（秒）。
	/// </summary>
	private static double[] ToneOnsets(string file)
	{
		byte[] pcm = Proc.RunBytes(TestEnv.FFmpeg,
			["-v", "error", "-i", file, "-map", "0:a", "-f", "s16le", "-ac", "1", "-ar", SampleRate.ToString(), "-"]);
		// s16le 是小端，x86 和 ARM64 的原生字节序都是小端，可以直接重解释。
		ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(pcm);
		List<double> onsets = [];
		int quiet = Guard;   // 开头视为已静音，这样第一个 tone 段也能被检测到
		for (int i = 0; i < samples.Length; i++)
		{
			if (Math.Abs(samples[i]) > Threshold)
			{
				if (quiet >= Guard) onsets.Add((double)i / SampleRate);
				quiet = 0;
			}
			else
			{
				quiet++;
			}
		}
		return [.. onsets];
	}
}
