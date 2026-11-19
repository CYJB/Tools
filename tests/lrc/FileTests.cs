using System.Text;

namespace LrcTests;

/// <summary>
/// <see cref="Lrc.ParseFile"/> / <see cref="Lrc.FindSidecarFiles"/> / <see cref="Lrc.IsLyricFile"/>：
/// 同名配对和编码猜测。
/// </summary>
public sealed class FileTests : IDisposable
{
	private readonly string dir = Path.Combine(Path.GetTempPath(), $"lrc-tests-{Guid.NewGuid():N}");

	public FileTests()
	{
		Directory.CreateDirectory(dir);
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(dir, true);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
		}
	}

	private string Write(string name, params byte[] bytes)
	{
		string path = Path.Combine(dir, name);
		File.WriteAllBytes(path, bytes);
		return path;
	}

	private string WriteText(string name, string content, Encoding encoding)
	{
		return Write(name, [.. encoding.GetPreamble(), .. encoding.GetBytes(content)]);
	}

	[Fact(DisplayName = "UTF-8：带不带 BOM 都能读")]
	public void ReadsUtf8()
	{
		UTF8Encoding noBom = new(false);
		List<LrcLine> plain = Lrc.ParseFile(WriteText("plain.lrc", "[00:01.00]第一句\n", noBom), out bool plainGbk);
		Assert.False(plainGbk);
		Assert.Equal("第一句", Assert.Single(plain).Text);

		List<LrcLine> bom = Lrc.ParseFile(WriteText("bom.lrc", "[00:01.00]第二句\n", new UTF8Encoding(true)), out bool bomGbk);
		Assert.False(bomGbk);
		Assert.Equal("第二句", Assert.Single(bom).Text);
	}

	[Fact(DisplayName = "GBK：老工具导出的歌词不能变成乱码")]
	public void FallsBackToGbk()
	{
		// 千千静听这类老工具默认写 GBK；按 UTF-8 硬读会静默变成乱码，再一路写进成品。
		CodePages.RegisterGBK();
		string path = Write("gbk.lrc", [.. Encoding.GetEncoding("GBK").GetBytes("[00:01.00]中文歌词\n")]);
		List<LrcLine> lines = Lrc.ParseFile(path, out bool gbk);
		Assert.True(gbk);
		Assert.Equal("中文歌词", Assert.Single(lines).Text);
	}

	[Fact(DisplayName = "带 BOM 的 UTF-16（大小端）也能读")]
	public void ReadsUtf16WithBom()
	{
		Assert.Equal("小端", Assert.Single(Lrc.ParseFile(WriteText("le.lrc", "[00:01.00]小端\r\n", Encoding.Unicode))).Text);
		Assert.Equal("大端", Assert.Single(Lrc.ParseFile(WriteText("be.lrc", "[00:01.00]大端\r\n", Encoding.BigEndianUnicode))).Text);
	}

	[Fact(DisplayName = "同名配对：按去掉扩展名的文件名匹配，.lrc 不区分大小写")]
	public void FindsSidecarFiles()
	{
		Write("01.mp3");
		Write("01.lrc");
		Write("02.mp3");
		Write("02.LRC");
		Write("03.lrc");        // 没有对应音频也照样列出来，用不用由调用方决定
		Write("04.txt");
		Dictionary<string, string> files = Lrc.FindSidecarFiles(dir);
		Assert.Equal(["01", "02", "03"], files.Keys.OrderBy(key => key, StringComparer.Ordinal));
		Assert.EndsWith("01.lrc", files["01"]);
		Assert.EndsWith("02.LRC", files["02"]);
	}

	[Fact(DisplayName = "目录不存在时返回空表")]
	public void HandlesMissingDirectory()
	{
		Assert.Empty(Lrc.FindSidecarFiles(Path.Combine(dir, "不存在")));
	}

	[Theory(DisplayName = "IsLyricFile 只认 .lrc 扩展名（不区分大小写）")]
	[InlineData("01.lrc", true)]
	[InlineData("01.LRC", true)]
	[InlineData("01.Lrc", true)]
	[InlineData("01.mp3", false)]
	[InlineData("01.lrc.txt", false)]
	public void RecognizesLyricFiles(string name, bool expected)
	{
		Assert.Equal(expected, Lrc.IsLyricFile(name));
	}
}
