using System.Globalization;
using System.Runtime.CompilerServices;

// 这些测试会读写共享的工作区目录（合并结果就放在素材目录旁边），并行跑会互相踩，所以关掉并行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Audiobook.Tests;

/// <summary>
/// 所有测试共享的环境：构建 audiobook.cs、解析 ffmpeg、生成测试素材。
/// </summary>
/// <remarks>
/// 测试跑的是 `dotnet build audiobook.cs` 产出的真实可执行文件，不是把源码编进测试程序集——
/// audiobook.cs 有顶层语句，一个程序集里只能有一份，而且这样测的才是真正会发布出去的产物。
/// </remarks>
public sealed class TestEnv : IDisposable
{
	/// <summary>仓库根目录，按源码位置解析，不依赖当前工作目录。</summary>
	public static readonly string RepoRoot = Directory.GetParent(SourceFolder())!.Parent!.FullName;

	/// <summary>造素材和探测用的 ffmpeg。</summary>
	public static readonly string FFmpeg = ResolveBinary("ffmpeg");

	/// <summary>探测用的 ffprobe。</summary>
	public static readonly string FFprobe = ResolveBinary("ffprobe");

	/// <summary>audiobook 工具的可执行文件。</summary>
	public string Tool { get; }

	/// <summary>本次测试的工作区，素材和产物都在里面。</summary>
	public string Ws { get; }

	/// <summary>素材目录里实际存在的音频格式数，用于异构格式的用例。</summary>
	public int FormatCount { get; }

	private readonly string root;

	public TestEnv()
	{
		root = Path.Combine(Path.GetTempPath(), $"audiobook-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(root);
		Tool = BuildTool();
		Ws = Path.Combine(root, "ws");
		Directory.CreateDirectory(Ws);
		GenerateFixtures();
		FormatCount = Directory.GetFiles(Path.Combine(Ws, "fmts")).Length;
	}

	/// <summary>本文件所在目录，即 tests/audiobook。</summary>
	private static string SourceFolder([CallerFilePath] string? path = null) => Path.GetDirectoryName(path)!;

	/// <summary>
	/// 解析 ffmpeg / ffprobe：优先用仓库自带的 utils/ffmpeg/，找不到时回退到 PATH。
	/// </summary>
	private static string ResolveBinary(string name)
	{
		string exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
		string bundled = Path.Combine(RepoRoot, "utils", "ffmpeg", exe);
		if (File.Exists(bundled))
		{
			return bundled;
		}
		// PATH 上找。
		string? path = Environment.GetEnvironmentVariable("PATH");
		if (path != null)
		{
			foreach (string dir in path.Split(Path.PathSeparator))
			{
				if (dir.Length == 0) continue;
				string candidate = Path.Combine(dir, exe);
				if (File.Exists(candidate)) return candidate;
			}
		}
		throw new InvalidOperationException(
			$"找不到 {name}：仓库的 utils/ffmpeg/ 里没有当前平台的二进制，PATH 上也没有。");
	}

	/// <summary>
	/// 构建 audiobook.cs，返回可执行文件路径。
	/// </summary>
	private string BuildTool()
	{
		string bin = Path.Combine(root, "bin");
		Directory.CreateDirectory(bin);
		ProcessResult result = Proc.Run("dotnet", ["build", Path.Combine(RepoRoot, "audiobook.cs"),
			"-o", bin, "--nologo", "-v", "q"], RepoRoot);
		string message = result.Output;
		Assert.True(result.ExitCode == 0,
			message.Contains("无法识别的指令") || message.Contains("unrecognized directive")
				? $"构建 audiobook.cs 失败：`#:include` 需要 .NET SDK 10.0.300 或更高版本，当前 {SdkVersion()}。\n{message}"
				: $"构建 audiobook.cs 失败（退出码 {result.ExitCode}）：\n{message}");
		string tool = Path.Combine(bin, OperatingSystem.IsWindows() ? "audiobook.exe" : "audiobook");
		Assert.True(File.Exists(tool), $"构建产物不存在：{tool}\n{message}");
		return tool;
	}

	private static string SdkVersion()
	{
		try
		{
			return Proc.Run("dotnet", ["--version"]).StdOut.Trim();
		}
		catch (Exception)
		{
			return "(未知)";
		}
	}

	/// <summary>工作区里的目录路径。</summary>
	public string Dir(string name) => Path.Combine(Ws, name);

	/// <summary>工作区根目录下的文件路径（合并产物、章节文件等）。</summary>
	public string Out(string name) => Path.Combine(Ws, name);

	/// <summary>跑一次 audiobook 工具。</summary>
	public ProcessResult Run(params string[] args) => Proc.Run(Tool, args, Ws);

	/// <summary>跑一次 audiobook 工具并断言成功。</summary>
	public string RunOk(params string[] args)
	{
		ProcessResult result = Run(args);
		Assert.True(result.ExitCode == 0, $"audiobook {string.Join(' ', args)} 退出码 {result.ExitCode}\n{result.Output}");
		return result.Output;
	}

	/// <summary>跑一次 ffmpeg 并断言成功。输出文件的父目录会自动创建。</summary>
	public void Ffmpeg(params string[] args)
	{
		EnsureParentDir(args[^1]);
		Proc.RunChecked(FFmpeg, ["-hide_banner", "-v", "error", "-y", .. args], Ws);
	}

	/// <summary>ffmpeg 不会创建输出目录，这里替它建好。</summary>
	private void EnsureParentDir(string output)
	{
		string? dir = Path.GetDirectoryName(output);
		if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
	}

	/// <summary>生成一个指定时长的 440Hz 正弦音频。</summary>
	public void Gen(string relativeOutput, double seconds, params string[] extra)
	{
		Ffmpeg(["-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds.ToString(CultureInfo.InvariantCulture)}",
			.. extra, Path.Combine(Ws, relativeOutput)]);
	}

	/// <summary>生成一个指定时长/频率/采样率/声道数的正弦音频。</summary>
	public void GenTone(string relativeOutput, double seconds, int frequency, params string[] extra)
	{
		Ffmpeg(["-f", "lavfi", "-i", $"sine=frequency={frequency}:duration={seconds.ToString(CultureInfo.InvariantCulture)}",
			.. extra, Path.Combine(Ws, relativeOutput)]);
	}

	/// <summary>生成静音音频。</summary>
	public void GenSilence(string relativeOutput, double seconds, params string[] extra)
	{
		Ffmpeg(["-f", "lavfi", "-i", $"anullsrc=r=44100:cl=stereo:d={seconds.ToString(CultureInfo.InvariantCulture)}",
			.. extra, Path.Combine(Ws, relativeOutput)]);
	}

	/// <summary>当前 ffmpeg 是否有某个编码器。仓库自带的精简构建缺 vorbis 等。</summary>
	public bool HasEncoder(string name)
	{
		ProcessResult result = Proc.Run(FFmpeg, ["-hide_banner", "-encoders"]);
		return result.Output.Contains($" {name} ", StringComparison.Ordinal);
	}

	/// <summary>
	/// 生成全部测试素材。每个目录针对 merge 的一条分支或一个边界情况。
	/// </summary>
	private void GenerateFixtures()
	{
		// 封面
		Ffmpeg(["-f", "lavfi", "-i", "color=c=steelblue:s=300x300:d=0.1", "-frames:v", "1", Path.Combine(Ws, "cover.jpg")]);

		// uniform：3 个一致的 mp3，带 title 标签，用于测 --chapter-title tag
		for (int i = 1; i <= 3; i++)
		{
			Gen($"uniform/ch{i}.mp3", 5, "-c:a", "libmp3lame", "-b:a", "128k", "-metadata", $"title=第 {i} 章");
		}

		// mixedrate：同 codec 但采样率/声道不同
		GenTone("mixedrate/a.mp3", 3, 440, "-ar", "44100", "-ac", "2", "-c:a", "libmp3lame");
		GenTone("mixedrate/b.mp3", 3, 660, "-ar", "22050", "-ac", "1", "-c:a", "libmp3lame");

		// mixedcodec：codec 不同，必须走 filter_complex（走 demuxer 会静默丢帧）
		Gen("mixedcodec/a.mp3", 3, "-c:a", "libmp3lame");
		Gen("mixedcodec/b.m4a", 3, "-c:a", "aac");

		// covered：源 mp3 带封面，有 2 条流，会让 concat demuxer 的流匹配错位
		Ffmpeg(["-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-i", Path.Combine(Ws, "cover.jpg"),
			"-map", "0:a", "-map", "1:v", "-c:v", "copy", "-disposition:v", "attached_pic", Path.Combine(Ws, "covered/a.mp3")]);
		Gen("covered/b.mp3", 3, "-c:a", "libmp3lame");

		// order：命名陷阱，字符串排序会得到 ch1 < ch10 < ch2
		foreach (int i in new[] { 1, 2, 10 })
		{
			Gen($"order/ch{i}.mp3", 2, "-c:a", "libmp3lame");
		}

		// aacsrc：参数完全一致的 AAC，应走无损 copy
		for (int i = 1; i <= 3; i++)
		{
			Gen($"aacsrc/ch{i}.m4a", 3, "-c:a", "aac", "-b:a", "96k");
		}

		// rate：低参数的源排在前面。不固定 -ar/-ac 时整本书会被降采样成 22050/mono
		GenTone("rate/a_low.mp3", 3, 660, "-ar", "22050", "-ac", "1", "-c:a", "libmp3lame");
		GenTone("rate/b_high.mp3", 3, 440, "-ar", "44100", "-ac", "2", "-c:a", "libmp3lame");

		// rate2：混合 codec 且低参数在前，测 filter_complex 路径的归一化
		GenTone("rate2/a_low.m4a", 3, 660, "-ar", "22050", "-ac", "1", "-c:a", "aac");
		GenTone("rate2/b_high.mp3", 3, 440, "-ar", "44100", "-ac", "2", "-c:a", "libmp3lame");

		// aacmix：全是 AAC 但采样率不同。走 demuxer 会静默丢掉约一半音频
		GenTone("aacmix/a.m4a", 3, 660, "-ar", "22050", "-ac", "1", "-c:a", "aac");
		GenTone("aacmix/b.m4a", 3, 440, "-ar", "44100", "-ac", "2", "-c:a", "aac");

		// brmix：128k + 320k，码率应对齐最高的
		Gen("brmix/a.mp3", 3, "-c:a", "libmp3lame", "-b:a", "128k");
		Gen("brmix/b.mp3", 3, "-c:a", "libmp3lame", "-b:a", "320k");

		// flu / lossless：无损源，应触发有损告警和备份提醒
		for (int i = 1; i <= 3; i++)
		{
			Gen($"flu/ch{i}.flac", 3, "-c:a", "flac");
		}
		Directory.CreateDirectory(Path.Combine(Ws, "lossless"));
		foreach (string file in Directory.GetFiles(Dir("flu")))
		{
			File.Copy(file, Path.Combine(Ws, "lossless", Path.GetFileName(file)));
		}
		GenTone("lossless/w1.wav", 3, 440, "-c:a", "pcm_s16le");
		GenTone("lossless/w2.wav", 3, 660, "-c:a", "pcm_s16le");

		// fmts：异构格式混合
		Gen("fmts/01.flac", 2, "-c:a", "flac");
		GenTone("fmts/02.wav", 2, 550, "-c:a", "pcm_s16le");
		Gen("fmts/03.mp3", 2, "-c:a", "libmp3lame");
		Gen("fmts/04.m4a", 2, "-c:a", "aac");
		Gen("fmts/05.opus", 2, "-c:a", "libopus");
		// 仓库自带的精简 ffmpeg 构建可能没有 vorbis 编码器，缺了就跳过（用例按实际文件数断言）
		if (HasEncoder("vorbis") || HasEncoder("libvorbis"))
		{
			try
			{
				// ffmpeg 原生的 vorbis 是实验性的，且只支持 2 声道
				GenTone("fmts/06.ogg", 2, 990, "-ac", "2", "-c:a", "vorbis", "-strict", "-2");
			}
			catch (Exception)
			{
				try { GenTone("fmts/06.ogg", 2, 990, "-ac", "2", "-c:a", "libvorbis"); }
				catch (Exception) { /* 没有可用的 vorbis 编码器，跳过 ogg */ }
			}
		}

		// single / autocover / batch / empty
		Directory.CreateDirectory(Path.Combine(Ws, "single"));
		File.Copy(Path.Combine(Ws, "uniform/ch1.mp3"), Path.Combine(Ws, "single/ch1.mp3"));
		Directory.CreateDirectory(Path.Combine(Ws, "autocover"));
		foreach (string file in Directory.GetFiles(Dir("uniform")))
		{
			File.Copy(file, Path.Combine(Ws, "autocover", Path.GetFileName(file)));
		}
		File.Copy(Path.Combine(Ws, "cover.jpg"), Path.Combine(Ws, "autocover/cover.jpg"));
		CopyDirectory(Dir("uniform"), Path.Combine(Ws, "batch/书一"));
		CopyDirectory(Dir("order"), Path.Combine(Ws, "batch/书二"));
		Directory.CreateDirectory(Path.Combine(Ws, "empty"));

		// drift：tone/silence 交替 6 段，用于样本级验证章节边界是否累积漂移。
		// 静音段的真实起点就是对应章节的开始时间，可以解码成 PCM 后逐样本找出来当真值。
		GenerateDrift("cbr_xing", ["-c:a", "libmp3lame", "-b:a", "128k"]);                    // 有 Xing 头，容器时长准确
		GenerateDrift("cbr_noxing", ["-c:a", "libmp3lame", "-b:a", "128k", "-write_xing", "0"]); // 无 Xing 头但 CBR
		GenerateDrift("vbr_noxing", ["-c:a", "libmp3lame", "-q:a", "4", "-write_xing", "0"]);    // 无 Xing 头且 VBR：最坏情况
		GenerateDrift("vbr0_noxing", ["-c:a", "libmp3lame", "-q:a", "0", "-write_xing", "0"]);
	}

	/// <summary>生成 tone/silence 交替的 6 个文件。</summary>
	private void GenerateDrift(string name, string[] encoder)
	{
		for (int i = 0; i < 6; i++)
		{
			string output = $"{name}/{i:D2}.mp3";
			if (i % 2 == 0) GenTone(output, 2, 440, encoder);
			else GenSilence(output, 2, encoder);
		}
	}

	private static void CopyDirectory(string source, string target)
	{
		Directory.CreateDirectory(target);
		foreach (string file in Directory.GetFiles(source))
		{
			File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
		}
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
		catch (Exception)
		{
			// 临时目录清不掉就算了，不影响测试结果。
		}
	}
}

/// <summary>
/// 让所有测试类共享同一个 <see cref="TestEnv"/>（只构建一次工具、只造一次素材）。
/// </summary>
[CollectionDefinition(Name)]
public sealed class AudiobookCollection : ICollectionFixture<TestEnv>
{
	public const string Name = "audiobook";
}
