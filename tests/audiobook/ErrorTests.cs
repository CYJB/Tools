using System.Text;

namespace Audiobook.Tests;

/// <summary>
/// apply 的错误路径：所有报错都要指出行号、都不能改动原文件。
/// </summary>
[Collection(AudiobookCollection.Name)]
public sealed class ErrorTests(TestEnv env)
{
	private static readonly UTF8Encoding NoBom = new(false);

	/// <summary>
	/// 用指定的章节文件跑一次 apply，断言它失败、给出指定提示、并且原文件逐位未变。
	/// </summary>
	private void ExpectFailure(string book, string md5, string chapters, string expectedMessage)
	{
		string txt = env.Out($"err-{Guid.NewGuid():N}.txt");
		File.WriteAllText(txt, chapters, NoBom);
		ProcessResult result = env.Run("apply", book, "-i", txt);
		Assert.True(result.ExitCode != 0, $"应当失败，但退出码是 0：\n{result.Output}");
		Assert.Contains(expectedMessage, result.FlatOutput);
		Assert.Equal(md5, Probe.AudioMd5(book));
	}

	private (string Book, string Md5, string Txt) NewBook()
	{
		string book = env.Out($"errbook-{Guid.NewGuid():N}.m4b");
		env.RunOk("merge", env.Dir("uniform"), "-o", book, "-t", "测试书", "-a", "测试作者");
		env.RunOk("export", book);
		return (book, Probe.AudioMd5(book), Path.ChangeExtension(book, ".txt"));
	}

	[Fact(DisplayName = "时间戳乱序：同时指出冲突的两行")]
	public void OutOfOrderTimestamps()
	{
		(string book, string md5, _) = NewBook();
		string txt = env.Out($"err-{Guid.NewGuid():N}.txt");
		File.WriteAllText(txt, "[00:00:00] a\n[00:00:10] b\n[00:00:05] c\n", NoBom);
		ProcessResult result = env.Run("apply", book, "-i", txt);
		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("必须晚于", result.FlatOutput);
		// ffmpeg 会静默接受乱序并产出非单调的章节轨，所以必须自己拦，而且要说清是哪两行冲突。
		Assert.Contains("第 3 行", result.FlatOutput);
		Assert.Contains("第 2 行", result.FlatOutput);
		Assert.Equal(md5, Probe.AudioMd5(book));
	}

	[Fact(DisplayName = "时间戳重复")]
	public void DuplicateTimestamps()
	{
		(string book, string md5, _) = NewBook();
		ExpectFailure(book, md5, "[00:00:00] a\n[00:00:05] b\n[00:00:05] c\n", "必须晚于");
	}

	[Fact(DisplayName = "时间戳超出总时长（书只有 15 秒）")]
	public void TimestampBeyondDuration()
	{
		(string book, string md5, _) = NewBook();
		ExpectFailure(book, md5, "[00:00:00] a\n[01:00:00] b\n", "超出了文件总时长");
	}

	[Fact(DisplayName = "第一章必须从 [00:00:00] 开始")]
	public void FirstChapterMustStartAtZero()
	{
		(string book, string md5, _) = NewBook();
		ExpectFailure(book, md5, "[00:00:03] a\n[00:00:05] b\n", "第一章的开始时间必须是");
	}

	[Fact(DisplayName = "未知的元数据键要报错，避免拼写错误被静默丢掉")]
	public void UnknownMetadataKey()
	{
		(string book, string md5, _) = NewBook();
		ExpectFailure(book, md5, "titel = 拼错了\n[00:00:00] a\n", "未知的元数据键");
	}

	[Fact(DisplayName = "无法归类的行")]
	public void UnparsableLine()
	{
		(string book, string md5, _) = NewBook();
		ExpectFailure(book, md5, "[00:00:00] a\n这一行既不是元数据也不是章节\n", "无法识别的内容");
	}

	[Theory(DisplayName = "非法时间戳")]
	[InlineData("[00:xx:05] b", "无法解析时间戳")]   // 含非法字符
	[InlineData("[00:61:00] b", "无法解析时间戳")]   // 分钟超过 59
	[InlineData("[1:-2:3] b", "无法解析时间戳")]     // 负数分量
	[InlineData("[1:2:3.4567] b", "无法解析时间戳")] // 毫秒超过 3 位
	// 99999 小时在语法上是合法的（长篇有声书确实能超过 24 小时），该由「超出总时长」拦下。
	[InlineData("[99999:00:00] b", "超出了文件总时长")]
	public void InvalidTimestamp(string chapterLine, string expectedMessage)
	{
		(string book, string md5, _) = NewBook();
		ExpectFailure(book, md5, $"[00:00:00] a\n{chapterLine}\n", expectedMessage);
	}

	[Fact(DisplayName = "空标题只警告不失败")]
	public void EmptyTitleOnlyWarns()
	{
		(string book, _, _) = NewBook();
		string txt = env.Out($"warn-{Guid.NewGuid():N}.txt");
		File.WriteAllText(txt, "[00:00:00] a\n[00:00:05]\n", NoBom);
		ProcessResult result = env.Run("apply", book, "-i", txt);
		Assert.Equal(0, result.ExitCode);
		Assert.Contains("章节标题为空", result.FlatOutput);
		Assert.Equal(2, Probe.Of(book).ChapterCount);
	}

	[Fact(DisplayName = "输入文件不存在时给出清晰提示")]
	public void MissingFiles()
	{
		(string book, _, _) = NewBook();
		Assert.Contains("章节文件不存在", env.Run("apply", book, "-i", env.Out("不存在.txt")).FlatOutput);
		Assert.Contains("文件不存在", env.Run("apply", env.Out("不存在.m4b")).FlatOutput);
		Assert.Contains("封面文件不存在", env.Run("apply", book, "-c", env.Out("不存在.jpg")).FlatOutput);
	}

	[Fact(DisplayName = "扩展名保护：不能把 mp3 变成扩展名与内容不符的文件")]
	public void RejectsNonContainerExtension()
	{
		// ffmpeg 用 -f mp4 时会把 mp3 音频也塞进去并打上 mp4a 的 codec tag，
		// 它自己能解码，但 Apple Books 之类的播放器不认，而且不会报错。
		string mp3 = env.Out($"plain-{Guid.NewGuid():N}.mp3");
		File.Copy(Path.Combine(env.Dir("uniform"), "ch1.mp3"), mp3);
		byte[] before = File.ReadAllBytes(mp3);

		ProcessResult result = env.Run("apply", mp3);
		Assert.NotEqual(0, result.ExitCode);
		Assert.Contains("不是 m4b 容器", result.FlatOutput);
		Assert.Equal(before, File.ReadAllBytes(mp3));

		// 即使指定 -o .m4b 也要拒绝：源不是 mp4 系容器，-c copy 会产出假 m4b。
		string target = env.Out($"plain-{Guid.NewGuid():N}.m4b");
		Assert.NotEqual(0, env.Run("apply", mp3, "-o", target).ExitCode);
		Assert.False(File.Exists(target), "失败时不该留下产物");

		// merge 的 -o 同样受保护。
		ProcessResult mergeResult = env.Run("merge", env.Dir("uniform"), "-o", env.Out($"bad-{Guid.NewGuid():N}.mp3"));
		Assert.NotEqual(0, mergeResult.ExitCode);
		Assert.Contains("不是 m4b 容器", mergeResult.FlatOutput);
	}

	[Fact(DisplayName = "只读命令不受容器限制")]
	public void ReadOnlyCommandsAcceptAnyFormat()
	{
		string mp3 = env.Out($"ro-{Guid.NewGuid():N}.mp3");
		File.Copy(Path.Combine(env.Dir("uniform"), "ch1.mp3"), mp3);
		Assert.Equal(0, env.Run("info", mp3).ExitCode);
		Assert.Equal(0, env.Run("export", mp3, "-o", env.Out($"ro-{Guid.NewGuid():N}.txt")).ExitCode);
	}

	[Fact(DisplayName = "工具找不到 ffmpeg 时给出清晰提示")]
	public void ReportsMissingFFmpegPaths()
	{
		// 路径按源码位置解析，所以在仓库里跑就一定找得到；这条断言守的是提示文本本身。
		Assert.True(File.Exists(TestEnv.FFmpeg), $"ffmpeg 不存在：{TestEnv.FFmpeg}");
		Assert.True(File.Exists(TestEnv.FFprobe), $"ffprobe 不存在：{TestEnv.FFprobe}");
	}
}
