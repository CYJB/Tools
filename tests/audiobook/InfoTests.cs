using System.Text.Json;

namespace Audiobook.Tests;

/// <summary>
/// info：格式诊断要能抓出「文件能播但体验不对」的几种情况。
/// </summary>
[Collection(AudiobookCollection.Name)]
public sealed class InfoTests(TestEnv env)
{
	private string NewBook(string name)
	{
		string book = env.Out($"{name}-{Guid.NewGuid():N}.m4b");
		env.RunOk("merge", env.Dir("uniform"), "-o", book, "-t", "测试书", "-a", "测试作者", "--date", "2024");
		return book;
	}

	/// <summary>造这些「坏文件」时必须显式 -f mp4，否则 ffmpeg 会从 .m4b 扩展名选 ipod muxer 然后拒绝写入。</summary>
	private string Remux(string source, string name, params string[] args)
	{
		string output = env.Out($"{name}-{Guid.NewGuid():N}.m4b");
		env.Ffmpeg(["-i", source, .. args, "-f", "mp4", output]);
		return output;
	}

	[Fact(DisplayName = "正常文件：显示元数据、章节表，并给出通过章")]
	public void HealthyFile()
	{
		string output = Text.Flat(env.RunOk("info", NewBook("info")));
		Assert.Contains("测试书", output);
		Assert.Contains("ch1", output);
		Assert.Contains("格式检查通过", output);
		// mov_text 章节轨在某些 ffmpeg 版本里是 codec_type=subtitle，但工具必须把它报成章节而不是字幕。
		Assert.DoesNotContain("subtitle", output);
		Assert.DoesNotContain("major_brand 是", output);
		Assert.DoesNotContain("media_type(stik) 是", output);
	}

	[Fact(DisplayName = "缺 stik：提示 Apple Books 不会提供断点续听")]
	public void MissingStik()
	{
		string book = NewBook("nostik");
		// -map_metadata -1 才会真的去掉 media_type：不加的话它会随全局 metadata 默认映射被带过去。
		string broken = Remux(book, "nostik", "-map", "0:a", "-map_chapters", "0", "-map_metadata", "-1",
			"-c", "copy", "-brand", "M4B ");
		Assert.Null(Probe.Of(broken).Stik);

		string output = Text.Flat(env.RunOk("info", broken));
		Assert.Contains("media_type(stik) 是", output);
		Assert.Contains("断点续听", output);
		Assert.DoesNotContain("格式检查通过", output);
	}

	[Fact(DisplayName = "brand 退回 M4A：提示播放器会当成普通音频")]
	public void WrongBrand()
	{
		string book = NewBook("badbrand");
		// major_brand 是 muxer 选项、不走 metadata 映射，不显式传就会退回 "M4A "。
		string broken = Remux(book, "badbrand", "-map", "0:a", "-map_chapters", "0", "-c", "copy", "-metadata", "media_type=2");
		Assert.NotEqual("M4B ", Probe.Of(broken).Brand);
		Assert.Contains("major_brand 是", Text.Flat(env.RunOk("info", broken)));
	}

	[Fact(DisplayName = "章节轨重复：apply 用 -map 0 的经典错误")]
	public void DuplicateChapterTrack()
	{
		string book = NewBook("dup");
		string meta = env.Out($"dup-{Guid.NewGuid():N}.txt");
		File.WriteAllText(meta, ";FFMETADATA1\ntitle=X\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=5000\ntitle=a\n");
		// -map 0 会把原有的章节轨（一条 codec_type=data 的流）也带上，再叠加 -map_chapters 1 就变成两条。
		string broken = Remux(book, "dup", "-f", "ffmetadata", "-i", meta, "-map", "0",
			"-map_chapters", "1", "-map_metadata", "1", "-c", "copy", "-brand", "M4B ", "-metadata", "media_type=2");
		Assert.Equal(2, Probe.Of(broken).ChapterTracks);
		Assert.Contains("章节轨有 2 条", Text.Flat(env.RunOk("info", broken)));
	}

	[Fact(DisplayName = "封面没标记 attached_pic")]
	public void CoverWithoutAttachedPic()
	{
		string book = NewBook("noapic");
		string output = env.Out($"noapic-{Guid.NewGuid():N}.m4b");
		env.Ffmpeg(["-i", book, "-i", env.Out("cover.jpg"), "-map", "0:a", "-map_chapters", "0",
			"-map", "1:v", "-c", "copy", "-f", "mp4", output]);
		Assert.False(Probe.Of(output).AllAttachedPic);
		Assert.Contains("attached_pic", Text.Flat(env.RunOk("info", output)));
	}

	[Fact(DisplayName = "有封面的正常文件")]
	public void HealthyFileWithCover()
	{
		string book = env.Out($"cov-{Guid.NewGuid():N}.m4b");
		env.RunOk("merge", env.Dir("covered"), "-o", book, "-c", env.Out("cover.jpg"));
		string output = Text.Flat(env.RunOk("info", book));
		Assert.Contains("1 张", output);
		Assert.Contains("格式检查通过", output);
	}

	[Fact(DisplayName = "--json 输出 ffprobe 的原始 JSON")]
	public void RawJson()
	{
		string output = env.RunOk("info", NewBook("json"), "--json");
		using JsonDocument document = JsonDocument.Parse(output);
		Assert.True(document.RootElement.TryGetProperty("chapters", out JsonElement chapters));
		Assert.Equal(3, chapters.GetArrayLength());
		Assert.True(document.RootElement.TryGetProperty("streams", out _));
	}

	[Fact(DisplayName = "文件不存在时给出提示")]
	public void MissingFile()
	{
		ProcessResult result = env.Run("info", env.Out("不存在.m4b"));
		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("文件不存在", result.FlatOutput);
	}
}
