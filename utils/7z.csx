#r "nuget: System.Text.Encoding.CodePages, 10.0.5"

#nullable enable

using System.Text;
using System.Text.RegularExpressions;

// 注册 GBK 编码支持。
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

/// <summary>
/// 7z 压缩进度的正则表达式。
/// </summary>
private static readonly Regex SevenZProgressRegex = new(@"^(\d+%)");

/// <summary>
/// 使用指定的参数执行 7z 指令。
/// </summary>
static async Task Compress(SevenZConfig config, Action<string>? progressCallback = null)
{
	using var process = new Process();
	var args = new List<string>
	{
		"a", // 添加文件到压缩包
		"-bsp1", // 输出压缩进度
	};
	// 设置密码。
	if (config.Password != null)
	{
		args.Add($"-p{config.Password}");
		// 加密文件名
		if (config.EncrypteFileName)
		{
			args.Add("-mhe");
		}
	}
	args.Add(config.FileName);
	args.AddRange(config.Files);
	process.StartInfo = new ProcessStartInfo("7z", args)
	{
		WorkingDirectory = config.WorkingDirectory,
		UseShellExecute = false,
		RedirectStandardOutput = true,
		RedirectStandardError = true,
		// 避免乱码
		StandardOutputEncoding = Encoding.GetEncoding("GBK"),
		StandardErrorEncoding = Encoding.GetEncoding("GBK"),
	};

	var errorBuilder = new StringBuilder();
	process.OutputDataReceived += (sender, e) =>
	{
		if (e.Data != null && progressCallback != null)
		{
			var match = SevenZProgressRegex.Match(e.Data.Trim());
			if (match.Success)
			{
				progressCallback(match.Value);
			}
		}
	};
	process.ErrorDataReceived += (sender, e) =>
	{
		if (e.Data != null)
		{
			errorBuilder.AppendLine(e.Data);
		}
	};
	process.Start();
	process.BeginOutputReadLine();
	process.BeginErrorReadLine();
	await process.WaitForExitAsync();
	if (process.ExitCode != 0)
	{
		throw new Exception($"压缩失败: {process.ExitCode}, {errorBuilder}");
	}
}

public class SevenZConfig
{
	/// <summary>
	/// 压缩的目标文件名。
	/// </summary>
	public required string FileName { get; set; }
	/// <summary>
	/// 压缩的工作目录。
	/// </summary>
	public required string WorkingDirectory { get; set; }
	/// <summary>
	/// 压缩文件列表。
	/// </summary>
	public required ICollection<string> Files { get; set; }
	/// <summary>
	/// 加密密码。
	/// </summary>
	public string? Password { get; set; }
	/// <summary>
	/// 是否加密文件名。
	/// </summary>
	public bool EncrypteFileName { get; set; } = false;
}
