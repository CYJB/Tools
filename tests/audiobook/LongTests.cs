using System.Text;

namespace Audiobook.Tests;

/// <summary>
/// 超过 24 小时的有声书：时间戳不能回绕。
/// </summary>
/// <remarks>
/// 回绕的原因：<c>TimeSpan</c> 的自定义格式 <c>"hh"</c> 是「不含天数的小时部分」，
/// 25 小时会被格式化成 01:00:00。40 小时的长篇全集很常见，所以这是实际会踩到的路径。
/// <para>
/// 这个测试很慢也很占空间（要造一个 25 小时、约 85MB 的文件），默认跳过。
/// 设置环境变量 <c>AUDIOBOOK_TEST_LONG=1</c> 后运行：<c>dotnet test --filter "Category=Long"</c>。
/// </para>
/// </remarks>
[Collection(AudiobookCollection.Name)]
[Trait("Category", "Long")]
public sealed class LongTests(TestEnv env)
{
	private const double Hours25 = 90000;
	private const int Segments = 18000;   // 18000 × 5s = 25 小时

	private static bool Enabled =>
		Environment.GetEnvironmentVariable("AUDIOBOOK_TEST_LONG") is { Length: > 0 } value && value != "0";

	/// <summary>
	/// 造一个约 25 小时的 m4b：同一个低码率片段用 concat -c copy 重复 18000 次，几秒钟就能造好。
	/// </summary>
	private string BuildLongBook()
	{
		string segment = env.Out($"seg-{Guid.NewGuid():N}.m4a");
		env.Ffmpeg(["-f", "lavfi", "-i", "sine=frequency=440:duration=5",
			"-c:a", "aac", "-b:a", "8k", "-ac", "1", "-ar", "8000", segment]);
		StringBuilder list = new("ffconcat version 1.0\n");
		for (int i = 0; i < Segments; i++)
		{
			list.Append("file '").Append(segment.Replace("'", "'\\''")).Append("'\n");
		}
		string listFile = env.Out($"list-{Guid.NewGuid():N}.txt");
		File.WriteAllText(listFile, list.ToString(), new UTF8Encoding(false));
		string book = env.Out($"long-{Guid.NewGuid():N}.m4b");
		env.Ffmpeg(["-f", "concat", "-safe", "0", "-i", listFile, "-c", "copy",
			"-brand", "M4B ", "-metadata", "media_type=2", "-f", "mp4", book]);
		File.Delete(segment);
		File.Delete(listFile);
		return book;
	}

	[Fact(DisplayName = "25 小时的书：info / export / apply 的时间戳都不回绕")]
	public void TimestampsBeyond24Hours()
	{
		if (!Enabled)
		{
			// xUnit 的 Skip 只能是编译期常量，所以运行时条件下只能提前返回。
			return;
		}
		string book = BuildLongBook();
		try
		{
			Assert.Equal(Hours25, Probe.Of(book).AudioDuration, 0);

			string info = Text.Flat(env.RunOk("info", book));
			Assert.Contains("25:0", info);
			Assert.DoesNotContain(" 1:00:00", info);

			// 写入 24 小时之后的章节。
			string chapters = env.Out(Path.GetFileNameWithoutExtension(book) + ".txt");
			File.WriteAllText(chapters,
				"title = 长篇有声书\n[00:00:00] 第一章\n[10:00:00] 第二章\n[24:30:00.500] 第三章\n",
				new UTF8Encoding(false));
			env.RunOk("apply", book);
			Probe applied = Probe.Of(book);
			Assert.Equal(3, applied.ChapterCount);
			Assert.Equal([0, 36000, 88200.5], applied.Starts);

			// 导出的时间戳不能回绕成 00:30:00。
			File.Delete(chapters);
			env.RunOk("export", book);
			string exported = File.ReadAllText(chapters, Encoding.UTF8);
			Assert.Contains("[10:00:00] 第二章", exported);
			Assert.Contains("[24:30:00.500] 第三章", exported);
			Assert.DoesNotContain("[00:30:00]", exported);

			// export 出来的文件直接 apply 应当被接受且幂等。
			double[] before = Probe.Of(book).Starts;
			env.RunOk("apply", book);
			Assert.Equal(before, Probe.Of(book).Starts);
			Assert.Equal(3, Probe.Of(book).ChapterCount);

			// info 的章节表也要显示 24 小时之后。
			string table = Text.Flat(env.RunOk("info", book));
			Assert.Contains("10:00:00", table);
			Assert.Contains("24:30:00", table);
		}
		finally
		{
			File.Delete(book);
			File.Delete(book + ".backup");
			File.Delete(Path.ChangeExtension(book, ".txt"));
		}
	}
}
