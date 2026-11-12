namespace Audiobook.Tests;

/// <summary>
/// merge：三条拼接路径、自然序、封面识别、批量、覆盖保护、边界输入。
/// </summary>
[Collection(AudiobookCollection.Name)]
public sealed class MergeTests(TestEnv env)
{
	/// <summary>合并出一个临时产物，测完自动删掉，避免测试之间互相影响。</summary>
	private string Merge(string dir, params string[] extra)
	{
		string output = env.Out($"{dir}-{Guid.NewGuid():N}.m4b");
		env.RunOk(["merge", env.Dir(dir), "-o", output, .. extra]);
		return output;
	}

	[Fact(DisplayName = "uniform mp3：容器标记、章节、元数据都正确")]
	public void UniformMp3()
	{
		string file = Merge("uniform", "-t", "测试书", "-a", "测试作者", "--date", "2024");
		Probe probe = Probe.Of(file);
		Assert.Equal("M4B ", probe.Brand);
		Assert.Equal("2", probe.Stik);
		Assert.Equal(1, probe.ChapterTracks);
		Assert.Equal(0, probe.VideoStreams);
		Assert.Equal(1, probe.AudioStreams);
		Assert.Equal(3, probe.ChapterCount);
		Assert.Equal(["ch1", "ch2", "ch3"], probe.Titles);
		Assert.Equal("测试书", probe.Title);
		Assert.Equal("测试作者", probe.Artist);
		Assert.Equal("有声书", probe.Genre);   // --genre 的默认值
		Assert.Equal("2024", probe.Date);
		Assert.Equal(15, probe.AudioDuration, 1);
		Assert.Equal("aac", probe.Codec);
	}

	[Fact(DisplayName = "--chapter-title tag：章节名取音频里的 title 标签")]
	public void ChapterTitleFromTag()
	{
		Probe probe = Probe.Of(Merge("uniform", "--chapter-title", "tag"));
		Assert.Equal(["第 1 章", "第 2 章", "第 3 章"], probe.Titles);
	}

	[Fact(DisplayName = "--chapter-title number：章节名用「第 N 章」")]
	public void ChapterTitleFromNumber()
	{
		Probe probe = Probe.Of(Merge("uniform", "--chapter-title", "number"));
		Assert.Equal(["第 1 章", "第 2 章", "第 3 章"], probe.Titles);
	}

	[Fact(DisplayName = "自然序：ch2 排在 ch10 之前")]
	public void NaturalOrder()
	{
		Probe probe = Probe.Of(Merge("order"));
		// 字符串排序会得到 ch1 < ch10 < ch2，整本书顺序错乱而且不会有任何提示。
		Assert.Equal(["ch1", "ch2", "ch10"], probe.Titles);
		Assert.Equal([0, 2, 4], probe.Starts.Select(s => Math.Round(s)).ToArray());
	}

	[Fact(DisplayName = "同 codec 但采样率/声道不同：重编码且不丢帧")]
	public void MixedSampleRate()
	{
		string output = env.Out($"mixedrate-{Guid.NewGuid():N}.m4b");
		string log = env.RunOk("merge", env.Dir("mixedrate"), "-o", output);
		// 参数不一致就不能复制音频流：混合采样率走 -c:a copy 会静默变调。
		Assert.DoesNotContain("直接复制音频流", Text.Flat(log));
		Probe probe = Probe.Of(output);
		Assert.Equal(6, probe.AudioDuration, 1);
		Assert.Equal(2, probe.ChapterCount);
	}

	[Fact(DisplayName = "混合 codec：必须走 filter_complex，否则静默丢帧")]
	public void MixedCodecUsesFilter()
	{
		string output = env.Out($"mixedcodec-{Guid.NewGuid():N}.m4b");
		string log = env.RunOk("merge", env.Dir("mixedcodec"), "-o", output);
		Assert.Contains("filter_complex", Text.Flat(log));
		Assert.Equal(6, Probe.Of(output).AudioDuration, 1);
	}

	[Fact(DisplayName = "封面：-c 指定，attached_pic 置位，章节轨不受影响")]
	public void ExplicitCover()
	{
		Probe probe = Probe.Of(Merge("covered", "-c", env.Out("cover.jpg")));
		Assert.Equal(1, probe.VideoStreams);
		Assert.True(probe.AllAttachedPic, "封面流必须标记 attached_pic，否则播放器不显示");
		Assert.Equal(1, probe.ChapterTracks);
	}

	[Fact(DisplayName = "封面：自动识别目录里的 cover.jpg")]
	public void AutoDetectCover()
	{
		Assert.Equal(1, Probe.Of(Merge("autocover")).VideoStreams);
	}

	[Fact(DisplayName = "批量：目录里没有音频时逐个合并子目录，产物放在子目录外")]
	public void BatchSubdirectories()
	{
		// 输出放在**每本书的子目录外面**（即 batch/ 下），否则重复运行会把上次的结果再合并进去。
		string first = Path.Combine(env.Dir("batch"), "书一.m4b");
		string second = Path.Combine(env.Dir("batch"), "书二.m4b");
		File.Delete(first);
		File.Delete(second);
		string log = env.RunOk("merge", env.Dir("batch"));
		Assert.True(File.Exists(first), $"没有生成 {first}\n{log}");
		Assert.True(File.Exists(second), $"没有生成 {second}\n{log}");
		Assert.Equal(3, Probe.Of(first).ChapterCount);
		Assert.Equal(3, Probe.Of(second).ChapterCount);
		Assert.Equal(["ch1", "ch2", "ch10"], Probe.Of(second).Titles);
		File.Delete(first);
		File.Delete(second);
	}

	[Fact(DisplayName = "单个文件也能合成一本书")]
	public void SingleFile()
	{
		Probe probe = Probe.Of(Merge("single", "-t", "单文件"));
		Assert.Equal(1, probe.ChapterCount);
		Assert.Equal("aac", probe.Codec);
	}

	[Fact(DisplayName = "-o 指到源目录内时，输出文件本身要被排除")]
	public void OutputInsideSourceDirectoryIsExcluded()
	{
		string output = Path.Combine(env.Dir("uniform"), "inner.m4b");
		File.Delete(output);
		env.RunOk("merge", env.Dir("uniform"), "-o", output);
		Assert.Equal(3, Probe.Of(output).ChapterCount);   // 不是 4 章
		File.Delete(output);
	}

	[Fact(DisplayName = "默认拒绝覆盖已存在的输出，-y 才覆盖")]
	public void OverwriteProtection()
	{
		string output = env.Out($"overwrite-{Guid.NewGuid():N}.m4b");
		env.RunOk("merge", env.Dir("uniform"), "-o", output);
		string before = Probe.AudioMd5(output);

		ProcessResult refused = env.Run("merge", env.Dir("uniform"), "-o", output);
		Assert.NotEqual(0, refused.ExitCode);
		Assert.Contains("已存在", refused.FlatOutput);
		Assert.Equal(before, Probe.AudioMd5(output));

		env.RunOk("merge", env.Dir("uniform"), "-o", output, "-y");
	}

	[Fact(DisplayName = "空目录与无效路径给出清晰提示")]
	public void EmptyAndInvalidDirectory()
	{
		ProcessResult empty = env.Run("merge", env.Dir("empty"));
		Assert.Contains("没有找到", empty.FlatOutput);

		ProcessResult invalid = env.Run("merge", Path.Combine(env.Ws, "不存在的目录"));
		Assert.NotEqual(0, invalid.ExitCode);
		Assert.Contains("无效的目录", invalid.FlatOutput);
	}

	[Fact(DisplayName = "缺 Xing 头的 mp3：自动改用解码时长算章节边界")]
	public void AutoDetectUnreliableDuration()
	{
		// ffprobe 对缺 Xing/VBRI 头的 mp3 会提示「Estimating duration from bitrate」，
		// 工具据此自动改用完整解码得到的时长，否则章节边界会逐章累积漂移。
		string vbr = env.Out($"vbr-{Guid.NewGuid():N}.m4b");
		Assert.Contains("缺少 Xing 头", Text.Flat(env.RunOk("merge", env.Dir("vbr_noxing"), "-o", vbr)));

		string cbr = env.Out($"cbr-{Guid.NewGuid():N}.m4b");
		// 有 Xing 头时容器时长精确到 0.1ms 以内，不该白付一遍完整解码的代价。
		Assert.DoesNotContain("缺少 Xing 头", Text.Flat(env.RunOk("merge", env.Dir("cbr_xing"), "-o", cbr)));
	}
}
