namespace Audiobook.Tests;

/// <summary>
/// merge：和音频同名的 lrc 会被合进 m4b（mov_text 字幕轨 + ©lyr 元数据），apply 不改动它们。
/// </summary>
[Collection(AudiobookCollection.Name)]
public sealed class LyricTests(TestEnv env)
{
	/// <summary>合并出一个临时产物，测完自动删掉，避免测试之间互相影响。</summary>
	private string Merge(string dir, params string[] extra)
	{
		string output = env.Out($"{dir}-lyrics-{Guid.NewGuid():N}.m4b");
		env.RunOk(["merge", env.Dir(dir), "-o", output, .. extra]);
		return output;
	}

	/// <summary>和工具里一样的 lrc 时间戳格式，用来算「按章节平移之后」应该是多少。</summary>
	private static string LrcTime(long ms)
	{
		long centiseconds = (ms + 5) / 10;
		return $"{centiseconds / 6000:D2}:{centiseconds / 100 % 60:D2}.{centiseconds % 100:D2}";
	}

	/// <summary>和工具里一样的 srt 时间戳格式。</summary>
	private static string SrtTime(long ms)
	{
		return $"{ms / 3600000:D2}:{ms / 60000 % 60:D2}:{ms / 1000 % 60:D2},{ms % 1000:D3}";
	}

	[Fact(DisplayName = "同名 lrc：字幕轨和 ©lyr 都带上，时间轴按章节平移")]
	public void EmbedsLyrics()
	{
		Probe probe = Probe.Of(Merge("lyrics"));
		// 歌词是额外的一条字幕轨，不能掺进音频流，也不能改写章节轨。
		Assert.Equal(1, probe.SubtitleStreams);
		Assert.Equal("mov_text", probe.SubtitleCodec);
		Assert.Equal(1, probe.AudioStreams);
		Assert.Equal(1, probe.ChapterTracks);
		Assert.Equal(3, probe.ChapterCount);

		string[] lines = probe.Lyrics!.Split('\n');
		// [ti:] 是头信息，不是歌词；[tr:] 语言标记和 <..> 词级时间戳要剥掉；
		// 一行多时间戳要拆成多条；超出章节长度的行要丢掉。
		Assert.Equal(5, lines.Length);
		Assert.Equal("[00:00.50]第一句", lines[0]);
		Assert.Equal("[00:01.00]翻译", lines[1]);
		Assert.Equal("[00:02.00]同一句出现两次", lines[2]);
		Assert.Equal("[00:03.00]同一句出现两次", lines[3]);
		Assert.DoesNotContain("超出这一章", probe.Lyrics);
		// 02.lrc 是 GBK 编码的，按 UTF-8 硬读会变成乱码；它的 [00:01.00] 要平移成「第 2 章起点 + 1 秒」。
		long chapterStart = (long)Math.Round(probe.Starts[1] * 1000);
		Assert.Equal($"[{LrcTime(chapterStart + 1000)}]第二章的歌词", lines[4]);
	}

	[Fact(DisplayName = "同名 lrc：字幕轨的时间轴同样按章节平移，且首尾相接")]
	public void SubtitleTimelineIsShifted()
	{
		string file = Merge("lyrics");
		Probe probe = Probe.Of(file);
		string srt = Probe.ExtractSubtitles(file);
		// 第一句 [00:00.50] 到下一句 [00:01.00]，字幕首尾相接、不互相覆盖。
		Assert.Contains("00:00:00,500 --> 00:00:01,000", srt);
		Assert.Contains("第一句", srt);
		Assert.Contains("翻译", srt);
		// 第 2 章的 lrc 从 [00:01.00] 开始，平移后必须落在第 2 章里，而不是整本书的开头。
		long chapterStart = (long)Math.Round(probe.Starts[1] * 1000);
		Assert.Contains($"{SrtTime(chapterStart + 1000)} --> ", srt);
		Assert.Contains("第二章的歌词", srt);
	}

	[Fact(DisplayName = "没有同名 lrc 时不写字幕轨，也不带歌词元数据")]
	public void NoLyricsWithoutLrc()
	{
		Probe probe = Probe.Of(Merge("uniform"));
		Assert.Equal(0, probe.SubtitleStreams);
		Assert.Null(probe.Lyrics);
	}

	[Fact(DisplayName = "apply 原样保留字幕轨和 ©lyr，音频流仍然逐位不变")]
	public void ApplyPreservesLyrics()
	{
		// apply 是「无损改章节」，不能在重封装时把 merge 写进去的歌词顺手丢掉：
		// -map 0:a 只映射音频，-map_metadata 又会把白名单之外的键丢掉，两个都得显式补上。
		string file = Merge("lyrics");
		string lyrics = Probe.Of(file).Lyrics!;
		string audio = Probe.AudioMd5(file);

		env.RunOk(["export", file, "-y"]);
		env.RunOk(["apply", file]);

		Probe probe = Probe.Of(file);
		Assert.Equal(1, probe.SubtitleStreams);
		Assert.Equal("mov_text", probe.SubtitleCodec);
		Assert.Equal(lyrics, probe.Lyrics);
		Assert.Equal(audio, Probe.AudioMd5(file));
	}
}
