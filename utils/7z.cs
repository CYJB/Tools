#!/usr/bin/env -S dotnet --
#:include code-pages.cs
#:include process.cs
#nullable enable

using System.Text;
using System.Text.RegularExpressions;

class SevenZ
{
	/// <summary>
	/// 7z 压缩进度的正则表达式。
	/// </summary>
	private static readonly Regex SevenZProgressRegex = new(@"^(\d+%)");

	/// <summary>
	/// 使用指定的参数执行 7z 指令。
	/// </summary>
	public static async Task Compress(SevenZConfig config, Action<string>? progressCallback = null)
	{
		CodePages.RegisterGBK();
		var args = new List<string>
		{
			"a", // 添加文件到压缩包
			"-bsp1", // 输出压缩进度
		};
		// 分卷压缩。
		if (config.Volumes != null)
		{
			args.Add($"-v{config.Volumes}");
		}
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
		try
		{
			await ProcessRun.RunAsync("7z", args, line =>
			{
				if (progressCallback == null)
				{
					return;
				}
				var match = SevenZProgressRegex.Match(line.Trim());
				if (match.Success)
				{
					progressCallback(match.Value);
				}
			}, workingDirectory: config.WorkingDirectory, encoding: Encoding.GetEncoding("GBK"));
		}
		catch (Exception e) when (e is not OperationCanceledException)
		{
			// 7z 把真正的错误写到标准输出，ProcessRun 抛出的消息大多是「7z 退出码 N」，
			// 加上操作名，调用方才知道是哪一步压缩失败的。
			throw new Exception($"压缩失败: {e.Message}");
		}
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
	/// 分卷压缩。
	/// </summary>
	public string? Volumes { get; set; }
	/// <summary>
	/// 是否加密文件名。
	/// </summary>
	public bool EncrypteFileName { get; set; } = false;
}
