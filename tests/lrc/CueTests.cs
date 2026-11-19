namespace LrcTests;

/// <summary>
/// <see cref="Lrc.BuildCues"/> / <see cref="Lrc.ToLrc"/> / <see cref="Lrc.ToSrt"/>：
/// 按段平移、首尾相接、两种格式的序列化。
/// </summary>
public sealed class CueTests
{
	/// <summary>一段歌词：内容是 lrc 文本，从 <paramref name="startMs"/> 开始、长 <paramref name="lengthMs"/>。</summary>
	private static LrcSegment Segment(string content, long startMs, long lengthMs)
	{
		return new LrcSegment(Lrc.Parse(content), startMs, lengthMs);
	}

	[Fact(DisplayName = "每行的结束时间取下一行的开始时间，末行到这一段结尾")]
	public void ChainsEndTimes()
	{
		List<LrcCue> cues = Lrc.BuildCues([Segment("[00:01.00]a\n[00:03.00]b\n", 0, 10_000)]);
		Assert.Equal(2, cues.Count);
		Assert.Equal(new LrcCue(1000, 3000, "a"), cues[0]);
		Assert.Equal(new LrcCue(3000, 10_000, "b"), cues[1]);
	}

	[Fact(DisplayName = "按每一段的起点平移：第 2 段的歌词不能留在开头")]
	public void ShiftsBySegmentStart()
	{
		// lrc 的时间戳是相对单个文件的，不按段起点平移的话整篇歌词会全挤在开头。
		List<LrcCue> cues = Lrc.BuildCues([Segment("[00:01.00]a\n", 0, 5000), Segment("[00:01.00]b\n", 5000, 5000)]);
		Assert.Equal([1000L, 6000L], cues.Select(cue => cue.StartMs));
		Assert.Equal([5000L, 10_000L], cues.Select(cue => cue.EndMs));
		Assert.Equal(["a", "b"], cues.Select(cue => cue.Text));
	}

	[Fact(DisplayName = "超出这一段长度的行丢掉")]
	public void DropsLinesBeyondSegment()
	{
		// lrc 和音频不配套时最典型的症状；保留下来会让歌词一直显示到下一章。
		LrcCue cue = Assert.Single(Lrc.BuildCues([Segment("[00:01.00]留着\n[00:03.00]超出\n", 0, 2000)]));
		Assert.Equal("留着", cue.Text);
		Assert.Equal(2000, cue.EndMs);
	}

	[Fact(DisplayName = "同一时间戳的重复行不会产生零长度字幕")]
	public void SkipsZeroLengthCues()
	{
		LrcCue cue = Assert.Single(Lrc.BuildCues([Segment("[00:01.00]第一遍\n[00:01.00]第二遍\n", 0, 3000)]));
		Assert.Equal("第二遍", cue.Text);
		Assert.Equal(new LrcCue(1000, 3000, "第二遍"), cue);
	}

	[Fact(DisplayName = "没有歌词行时没有字幕")]
	public void EmptySegments()
	{
		Assert.Empty(Lrc.BuildCues([]));
		Assert.Empty(Lrc.BuildCues([Segment("[ti:只有头信息]\n", 0, 3000)]));
	}

	[Fact(DisplayName = "ToLrc：整篇时间轴，末尾没有多余换行")]
	public void WritesLrc()
	{
		List<LrcCue> cues = [new(1000, 2000, "第一句"), new(3_660_500, 3_700_000, "第二句")];
		Assert.Equal("[00:01.00]第一句\n[61:00.50]第二句", Lrc.ToLrc(cues));
	}

	[Fact(DisplayName = "ToSrt：编号、箭头、空行分隔")]
	public void WritesSrt()
	{
		List<LrcCue> cues = [new(1500, 2000, "第一句"), new(2000, 3000, "第二句")];
		Assert.Equal(
			"1\n00:00:01,500 --> 00:00:02,000\n第一句\n\n2\n00:00:02,000 --> 00:00:03,000\n第二句\n",
			Lrc.ToSrt(cues));
	}

	[Theory(DisplayName = "lrc 时间戳四舍五入到百分秒")]
	[InlineData(0, "00:00.00")]
	[InlineData(1094, "00:01.09")]        // 1094ms 截断会得到 1.09，四舍五入也是 1.09
	[InlineData(1096, "00:01.10")]        // 四舍五入进到下一秒
	[InlineData(3_599_990, "59:59.99")]
	[InlineData(144_000_000, "2400:00.00")]
	public void FormatsLrcTimestamps(long ms, string expected)
	{
		Assert.Equal(expected, Lrc.FormatLrcTimestamp(ms));
	}

	[Fact(DisplayName = "srt 时间戳带小时、毫秒三位，超过 24 小时不回绕")]
	public void FormatsSrtTimestamps()
	{
		Assert.Equal("00:00:00,000", Lrc.FormatSrtTimestamp(0));
		Assert.Equal("01:02:03,004", Lrc.FormatSrtTimestamp(3_723_004));
		Assert.Equal("25:00:00,000", Lrc.FormatSrtTimestamp(90_000_000));
	}

	[Fact(DisplayName = "往返：ToLrc 出来的文本能被 Parse 读回同样的字幕")]
	public void RoundTripsThroughLrc()
	{
		List<LrcCue> cues = [new(1000, 6000, "第一句"), new(6000, 8000, "第二句")];
		List<LrcCue> again = Lrc.BuildCues([new LrcSegment(Lrc.Parse(Lrc.ToLrc(cues)), 0, 8000)]);
		Assert.Equal(cues, again);
	}
}
