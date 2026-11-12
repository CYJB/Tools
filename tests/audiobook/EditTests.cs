using System.Text;

namespace Audiobook.Tests;

/// <summary>
/// export / apply：往返幂等、章节增删改、元数据覆盖、BOM/CRLF、封面替换。
/// </summary>
[Collection(AudiobookCollection.Name)]
public sealed class EditTests(TestEnv env)
{
	private static readonly UTF8Encoding NoBom = new(false);

	/// <summary>
	/// 造一本「刚合并出来」的书，返回 (m4b, 音频裸流 MD5)。每个测试用自己的文件，互不干扰。
	/// </summary>
	private (string Book, string Md5) NewBook()
	{
		string book = env.Out($"book-{Guid.NewGuid():N}.m4b");
		env.RunOk("merge", env.Dir("uniform"), "-o", book, "-t", "测试书", "-a", "测试作者", "--date", "2024");
		return (book, Probe.AudioMd5(book));
	}

	/// <summary>导出章节文件，返回它的路径。</summary>
	private string Export(string book)
	{
		env.RunOk("export", book);
		return Path.ChangeExtension(book, ".txt");
	}

	private static void Edit(string chaptersFile, string oldText, string newText)
	{
		string content = File.ReadAllText(chaptersFile, Encoding.UTF8);
		Assert.Contains(oldText, content);
		File.WriteAllText(chaptersFile, content.Replace(oldText, newText), NoBom);
	}

	[Fact(DisplayName = "export：产出的是按行的友好格式，不含任何 ffmetadata 细节")]
	public void ExportFormat()
	{
		(string book, _) = NewBook();
		string txt = Export(book);
		Assert.True(File.Exists(txt));
		string content = File.ReadAllText(txt, Encoding.UTF8);

		// 无 BOM：Windows 记事本另存会加 BOM，而 BOM 紧贴 [CHAPTER] 时 ffmpeg 会静默丢掉第一章。
		byte[] bytes = File.ReadAllBytes(txt);
		Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "导出的文件不该带 BOM");

		// ffmetadata 的实现细节由工具独占，用户不该看到。
		Assert.DoesNotContain("TIMEBASE", content);
		Assert.DoesNotContain("START=", content);
		Assert.DoesNotContain("END=", content);
		// ffmpeg -f ffmetadata 会连这些 muxer 内部键一起吐出来，原样写回去会污染文件。
		Assert.DoesNotContain("major_brand", content);
		Assert.DoesNotContain("encoder", content);
		Assert.DoesNotContain("compatible_brands", content);

		Assert.Contains("title = 测试书", content);
		Assert.Contains("artist = 测试作者", content);
		Assert.Contains("genre = 有声书", content);
		Assert.Contains("[00:00:00] ch1", content);
		Assert.Contains("[00:00:05] ch2", content);
		Assert.Contains("[00:00:10] ch3", content);
		Assert.Contains("共 3 章", content);
		Assert.Equal(3, content.Split('\n').Count(line => line.StartsWith("[00:00:", StringComparison.Ordinal)));
	}

	[Fact(DisplayName = "export：拒绝覆盖已存在的文件，-y 才覆盖")]
	public void ExportOverwriteProtection()
	{
		(string book, _) = NewBook();
		Export(book);
		ProcessResult refused = env.Run("export", book);
		Assert.NotEqual(0, refused.ExitCode);
		Assert.Contains("已存在", refused.FlatOutput);
		env.RunOk("export", book, "-y");
	}

	[Fact(DisplayName = "export：原文件没有章节时输出模板")]
	public void ExportWithoutChapters()
	{
		(string book, _) = NewBook();
		string noChapters = env.Out($"noch-{Guid.NewGuid():N}.m4b");
		// 造这些素材时必须显式 -f mp4，否则 ffmpeg 会从 .m4b 扩展名选 ipod muxer 然后拒绝写入。
		env.Ffmpeg(["-i", book, "-map", "0:a", "-map_chapters", "-1", "-c", "copy",
			"-brand", "M4B ", "-metadata", "media_type=2", "-f", "mp4", noChapters]);
		Assert.Equal(0, Probe.Of(noChapters).ChapterCount);

		env.RunOk("export", noChapters);
		string content = File.ReadAllText(Path.ChangeExtension(noChapters, ".txt"), Encoding.UTF8);
		Assert.Contains("原文件没有章节", content);
		Assert.Contains("# [00:00:00] 第一章", content);
	}

	[Fact(DisplayName = "apply：export 出来的文件原样写回应完全幂等，音频逐位不变")]
	public void RoundTripIsIdempotent()
	{
		(string book, string md5) = NewBook();
		Probe before = Probe.Of(book);
		Export(book);
		env.RunOk("apply", book);

		Probe after = Probe.Of(book);
		Assert.Equal("M4B ", after.Brand);
		Assert.Equal("2", after.Stik);
		Assert.Equal(before.ChapterCount, after.ChapterCount);
		Assert.Equal(before.Titles, after.Titles);
		Assert.Equal(before.Starts, after.Starts);
		Assert.Equal(before.Title, after.Title);
		Assert.Equal(1, after.ChapterTracks);
		Assert.Equal(md5, Probe.AudioMd5(book));
		Assert.True(File.Exists(book + ".backup"), "原地替换应当留下 .backup");
	}

	[Fact(DisplayName = "apply：改元数据，brand 和 stik 必须保住")]
	public void EditMetadata()
	{
		(string book, string md5) = NewBook();
		string txt = Export(book);
		Edit(txt, "title = 测试书", "title = 改名了");
		Edit(txt, "artist = 测试作者", "artist = 新作者");
		env.RunOk("apply", book);

		Probe probe = Probe.Of(book);
		Assert.Equal("改名了", probe.Title);
		Assert.Equal("新作者", probe.Artist);
		// major_brand 是 muxer 选项、不走 metadata 映射，每次 -c copy 重封装都会退回 "M4A "，
		// 工具必须重新指定，否则用户编辑一次文件就不再被当成有声书。
		Assert.Equal("M4B ", probe.Brand);
		Assert.Equal("2", probe.Stik);
		Assert.Equal(md5, Probe.AudioMd5(book));
		Assert.Equal(["ch1", "ch2", "ch3"], probe.Titles);
	}

	[Fact(DisplayName = "apply：值留空表示删除该键，其余键保留")]
	public void EmptyValueDeletesKey()
	{
		(string book, _) = NewBook();
		string txt = Export(book);
		Edit(txt, "artist = 测试作者", "artist =");
		env.RunOk("apply", book);
		Assert.Null(Probe.Of(book).Artist);
		Assert.Equal("测试书", Probe.Of(book).Title);
	}

	[Fact(DisplayName = "apply：只写章节的文件不会把原有元数据清掉")]
	public void ChaptersOnlyFileKeepsMetadata()
	{
		(string book, _) = NewBook();
		string txt = Export(book);
		File.WriteAllText(txt, "[00:00:00] 甲\n[00:00:05] 乙\n", NoBom);
		env.RunOk("apply", book, "-i", txt);
		Probe probe = Probe.Of(book);
		Assert.Equal("测试书", probe.Title);
		Assert.Equal("测试作者", probe.Artist);
		Assert.Equal(["甲", "乙"], probe.Titles);
	}

	[Fact(DisplayName = "apply：移动章节边界，上一章的结束时间自动跟随（首尾相接）")]
	public void MoveChapterBoundary()
	{
		(string book, string md5) = NewBook();
		string txt = Export(book);
		Edit(txt, "[00:00:05] ch2", "[00:00:07.500] ch2");
		env.RunOk("apply", book);

		Probe probe = Probe.Of(book);
		Assert.Equal([0, 7.5, 10], probe.Starts);
		Assert.Equal(7.5, probe.Ends[0], 3);    // ch1 的结束时间跟着变了
		Assert.Equal(10, probe.Ends[1], 3);     // ch2 的结束时间没变
		Assert.Equal(15.023, probe.Ends[2], 1); // 末章到音频结尾
		Assert.Equal(3, probe.ChapterCount);
		Assert.Equal(md5, Probe.AudioMd5(book));
	}

	[Fact(DisplayName = "apply：MM:SS 短格式也能解析")]
	public void ShortTimestampFormat()
	{
		(string book, _) = NewBook();
		string txt = Export(book);
		Edit(txt, "[00:00:05] ch2", "[00:07] ch2");
		env.RunOk("apply", book);
		Assert.Equal([0, 7, 10], Probe.Of(book).Starts);
	}

	[Fact(DisplayName = "apply：删掉中间一章 → 上一章自动延长")]
	public void DeleteMiddleChapter()
	{
		(string book, _) = NewBook();
		string txt = Export(book);
		File.WriteAllLines(txt, File.ReadAllLines(txt, Encoding.UTF8).Where(line => !line.EndsWith("] ch2")), NoBom);
		env.RunOk("apply", book);
		Probe probe = Probe.Of(book);
		Assert.Equal(2, probe.ChapterCount);
		Assert.Equal(["ch1", "ch3"], probe.Titles);
		Assert.Equal(10, probe.Ends[0], 3);
	}

	[Fact(DisplayName = "apply：中间插入一章 → 拆分")]
	public void InsertChapter()
	{
		(string book, _) = NewBook();
		string txt = Export(book);
		Edit(txt, "[00:00:05] ch2", "[00:00:03] 插入章\n[00:00:05] ch2");
		env.RunOk("apply", book);
		Probe probe = Probe.Of(book);
		Assert.Equal(4, probe.ChapterCount);
		Assert.Equal(["ch1", "插入章", "ch2", "ch3"], probe.Titles);
		Assert.Equal([0, 3, 5, 10], probe.Starts);
	}

	[Fact(DisplayName = "apply：清空所有章节，章节轨也要跟着清掉")]
	public void ClearAllChapters()
	{
		(string book, _) = NewBook();
		string txt = Export(book);
		File.WriteAllLines(txt, File.ReadAllLines(txt, Encoding.UTF8).Where(line => !line.StartsWith("[00:")), NoBom);
		string log = Text.Flat(env.RunOk("apply", book));
		Assert.Contains("没有任何章节", log);
		Probe probe = Probe.Of(book);
		Assert.Equal(0, probe.ChapterCount);
		Assert.Equal(0, probe.ChapterTracks);   // 残留的 Nero chpl 会让播放器显示过期章节
		Assert.Equal("M4B ", probe.Brand);
	}

	[Fact(DisplayName = "apply：标题里的 \\ ; # = 要正确转义并往返一致")]
	public void SpecialCharactersInTitle()
	{
		(string book, _) = NewBook();
		string txt = Export(book);
		Edit(txt, "[00:00:05] ch2", @"[00:00:05] a=b;c#d\e 中文");
		env.RunOk("apply", book);
		Assert.Equal(["ch1", @"a=b;c#d\e 中文", "ch3"], Probe.Of(book).Titles);
	}

	[Fact(DisplayName = "apply：带 UTF-8 BOM 的文件，第一章不能被吃掉")]
	public void ByteOrderMark()
	{
		(string book, _) = NewBook();
		string txt = Export(book);
		string bomFile = env.Out($"bom-{Guid.NewGuid():N}.txt");
		// BOM 紧贴 [CHAPTER] 时 ffmpeg 会静默丢掉第一章并返回 0，所以工具必须自己剥掉 BOM。
		File.WriteAllBytes(bomFile, [0xEF, 0xBB, 0xBF, .. File.ReadAllBytes(txt)]);
		env.RunOk("apply", book, "-i", bomFile);
		Probe probe = Probe.Of(book);
		Assert.Equal(3, probe.ChapterCount);
		Assert.Equal(["ch1", "ch2", "ch3"], probe.Titles);
	}

	[Fact(DisplayName = "apply：CRLF 行尾正常解析")]
	public void CrlfLineEndings()
	{
		(string book, _) = NewBook();
		string txt = Export(book);
		string crlfFile = env.Out($"crlf-{Guid.NewGuid():N}.txt");
		File.WriteAllText(crlfFile, File.ReadAllText(txt, Encoding.UTF8).Replace("\n", "\r\n"), NoBom);
		env.RunOk("apply", book, "-i", crlfFile);
		Assert.Equal(["ch1", "ch2", "ch3"], Probe.Of(book).Titles);
	}

	[Fact(DisplayName = "apply：换封面不会叠加成两张")]
	public void ReplaceCover()
	{
		(string book, string md5) = NewBook();
		string txt = Export(book);
		Assert.Equal(0, Probe.Of(book).VideoStreams);

		env.RunOk("apply", book, "-c", env.Out("cover.jpg"));
		Probe first = Probe.Of(book);
		Assert.Equal(1, first.VideoStreams);
		Assert.True(first.AllAttachedPic);
		Assert.Equal(1, first.ChapterTracks);

		env.RunOk("apply", book, "-c", env.Out("cover.jpg"));
		Assert.Equal(1, Probe.Of(book).VideoStreams);
		Assert.Equal(md5, Probe.AudioMd5(book));
	}

	[Fact(DisplayName = "apply：-o 输出到新文件时原文件不动")]
	public void OutputToNewFile()
	{
		(string book, string md5) = NewBook();
		Export(book);
		string target = env.Out($"copy-{Guid.NewGuid():N}.m4b");
		env.RunOk("apply", book, "-o", target);

		Assert.True(File.Exists(target));
		Assert.Equal(md5, Probe.AudioMd5(book));
		Assert.Equal("测试书", Probe.Of(book).Title);
		Assert.Equal(Probe.Of(book).ChapterCount, Probe.Of(target).ChapterCount);
		Assert.False(File.Exists(book + ".backup"), "输出到新文件时不该产生 .backup");
	}

	[Fact(DisplayName = "apply：--no-backup 不留备份")]
	public void NoBackup()
	{
		(string book, _) = NewBook();
		Export(book);
		env.RunOk("apply", book);
		Assert.True(File.Exists(book + ".backup"));

		File.Delete(book + ".backup");
		env.RunOk("apply", book, "--no-backup");
		Assert.False(File.Exists(book + ".backup"));
	}
}
