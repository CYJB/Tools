namespace Audiobook.Tests;

/// <summary>
/// 音质：码率对齐源最高值、采样率/声道取最大值、无损源告警、异构格式。
/// </summary>
[Collection(AudiobookCollection.Name)]
public sealed class QualityTests(TestEnv env)
{
	private string Merge(string dir, params string[] extra)
	{
		string output = env.Out($"{dir}-q-{Guid.NewGuid():N}.m4b");
		env.RunOk(["merge", env.Dir(dir), "-o", output, .. extra]);
		return output;
	}

	private string MergeWithLog(string dir, out string log, params string[] extra)
	{
		string output = env.Out($"{dir}-q-{Guid.NewGuid():N}.m4b");
		// 折叠空白：Spectre 会按控制台宽度折行，直接 Contains 短语可能匹配不上。
		log = Text.Flat(env.RunOk(["merge", env.Dir(dir), "-o", output, .. extra]));
		return output;
	}

	[Fact(DisplayName = "码率：mp3 128k → 对齐源的 128k，不再是固定的 64k")]
	public void BitrateMatchesSource()
	{
		string file = MergeWithLog("uniform", out string log);
		Assert.Contains("128000bps", log);
		Assert.Equal("aac", Probe.Of(file).Codec);
	}

	[Fact(DisplayName = "码率：128k + 320k 混合 → 请求值取最高的 320k")]
	public void BitrateTakesMaximum()
	{
		MergeWithLog("brmix", out string log);
		// 断言的是「请求值」而不是「实际输出值」：ffmpeg 原生 aac 编码器会自己饱和，
		// 单声道请求 320k 实际只出约 130k、立体声约 207k，那是编码器的上限、不是工具的问题。
		Assert.Contains("320000bps", log);
	}

	[Fact(DisplayName = "码率：-b 显式覆盖仍然生效")]
	public void ExplicitBitrateOverride()
	{
		Probe probe = Probe.Of(Merge("uniform", "-b", "64k"));
		Assert.InRange(probe.BitRate, 49_000, 79_000);
	}

	[Fact(DisplayName = "无损源：告警有损、提醒备份、用封顶码率")]
	public void LosslessSourceWarns()
	{
		string file = MergeWithLog("lossless", out string log);
		Assert.Contains("这一步是有损的", log);
		Assert.Contains("备份", log);
		// 无损源没有「对齐码率」的意义（1411k 的 wav 和 130k 的 flac 都是无损），所以用封顶值。
		Assert.Contains("320000bps", log);
		Assert.True(Probe.Of(file).BitRate > 100_000,
			$"无损源应当用高码率，实际 {Probe.Of(file).BitRate}");
	}

	[Fact(DisplayName = "无损源：纯 flac 也要告警")]
	public void FlacOnlyWarns()
	{
		MergeWithLog("flu", out string log);
		Assert.Contains("无损源文件", log);
	}

	[Fact(DisplayName = "关键回归：低参数的源排在前面时不能被降采样")]
	public void LowParameterSourceFirst()
	{
		// 不固定 -ar/-ac 时 ffmpeg 按第一个输入协商，22050/mono 排在前面会让整本书输出成 22050/mono。
		string file = MergeWithLog("rate", out string log);
		Assert.Contains("44100Hz 2ch", log);
		Probe probe = Probe.Of(file);
		Assert.Equal(44100, probe.SampleRate);
		Assert.Equal(2, probe.Channels);
	}

	[Fact(DisplayName = "关键回归：filter_complex 路径也要归一化")]
	public void FilterPathNormalizesParameters()
	{
		// concat 滤镜同样按第一个输入协商，会先把后面的输入降下来、拼完再升回去，
		// -v verbose 下能看到 44100Hz stereo -> 22050Hz mono -> 44100Hz stereo，11kHz 以上永久丢失。
		string file = MergeWithLog("rate2", out string log);
		Assert.Contains("filter_complex", log);
		Probe probe = Probe.Of(file);
		Assert.Equal(44100, probe.SampleRate);
		Assert.Equal(2, probe.Channels);
		Assert.Equal(6, probe.AudioDuration, 1);
	}

	[Fact(DisplayName = "关键回归：全是 AAC 但采样率不同 → 绝不能走 copy（会丢掉一半音频）")]
	public void MixedRateAacMustNotCopy()
	{
		string file = MergeWithLog("aacmix", out string log);
		Assert.DoesNotContain("直接复制音频流", log);
		Assert.Contains("filter_complex", log);
		Probe probe = Probe.Of(file);
		Assert.Equal(44100, probe.SampleRate);
		Assert.Equal(2, probe.Channels);
		// AAC(m4a) 源混合采样率走 concat demuxer 时实测会静默丢掉约一半音频（6 秒素材只出 3 秒）。
		Assert.Equal(6, probe.AudioDuration, 1);
	}

	[Fact(DisplayName = "全是 AAC 且参数一致 → 仍应无损 copy")]
	public void UniformAacUsesCopy()
	{
		string source = Path.Combine(env.Dir("aacsrc"), "ch1.m4a");
		long sourceBitrate = Probe.Of(source).BitRate;
		string file = MergeWithLog("aacsrc", out string log);
		Assert.Contains("直接复制音频流", log);
		Assert.InRange(Probe.Of(file).BitRate, sourceBitrate - 3000, sourceBitrate + 3000);
	}

	[Fact(DisplayName = "--sample-rate 覆盖仍然生效")]
	public void SampleRateOverride()
	{
		Assert.Equal(22050, Probe.Of(Merge("uniform", "--sample-rate", "22050")).SampleRate);
	}

	[Fact(DisplayName = "异构格式混合：flac/wav/mp3/m4a/opus[/ogg] 都能吃进去")]
	public void HeterogeneousFormats()
	{
		int count = env.FormatCount;
		Assert.InRange(count, 5, 6);   // ogg 取决于当前 ffmpeg 有没有 vorbis 编码器
		string file = Merge("fmts");
		Probe probe = Probe.Of(file);
		Assert.Equal(count, probe.ChapterCount);
		// opus 是 48000Hz，所以最大采样率应该是 48000 而不是第一个文件的 44100。
		Assert.Equal(48000, probe.SampleRate);
		Assert.Equal(count * 2, probe.AudioDuration, 1);
		Assert.Equal("M4B ", probe.Brand);
		Assert.Equal("2", probe.Stik);
	}
}
