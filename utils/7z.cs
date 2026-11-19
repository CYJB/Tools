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
		// 传绝对路径时 7z 只保留文件名，会把子目录结构丢掉，这里统一换成相对工作目录的路径。
		args.AddRange(config.Files.Select(file => ToRelativePath(config.WorkingDirectory, file)));
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

	/// <summary>
	/// 返回相对于工作目录的路径；文件不在工作目录下时原样返回。
	/// </summary>
	/// <remarks>
	/// 7z 只使用文件列表里的名字，传绝对路径会把目录结构丢掉（子目录里的文件全被压到根上），
	/// 所以调用方给绝对路径时，这里换成相对工作目录的路径。
	/// </remarks>
	private static string ToRelativePath(string workingDirectory, string file)
	{
		if (string.IsNullOrEmpty(workingDirectory) || !Path.IsPathRooted(file))
		{
			return file;
		}
		string relative = Path.GetRelativePath(workingDirectory, file);
		// 不在工作目录下（不同盘符、大小写不一致等）时退回原路径，宁可压平也不能写错位置。
		return relative.StartsWith("..", StringComparison.Ordinal) ? file : relative;
	}

	/// <summary>
	/// 使用指定的参数解压文件。
	/// </summary>
	public static async Task Extract(SevenZExtractConfig config, Action<string>? progressCallback = null)
	{
		CodePages.RegisterGBK();
		var args = new List<string>
		{
			"x", // 解压文件
			"-bsp1", // 输出解压进度
			"-y", // 全部确认，避免覆盖已有文件时等待输入
		};
		// 设置密码。
		if (config.Password != null)
		{
			args.Add($"-p{config.Password}");
		}
		// 输出目录要直接跟在 -o 后面，中间不能有空格。
		args.Add($"-o{config.OutputDirectory}");
		args.Add(config.FileName);
		if (config.Files != null)
		{
			args.AddRange(config.Files);
		}
		// 7z 的普通错误写在标准输出里，致命错误（例如密码错误）写在标准错误里，两边都留一点。
		List<string> messages = [];
		List<string> errors = [];
		try
		{
			await ProcessRun.RunAsync("7z", args, line =>
			{
				var text = line.Trim();
				if (text.Length > 0 && !IsSevenZSummary(text))
				{
					messages.Add(text);
					if (messages.Count > 10)
					{
						messages.RemoveAt(0);
					}
					if (text.Contains("error", StringComparison.OrdinalIgnoreCase) || text.Contains("cannot", StringComparison.OrdinalIgnoreCase))
					{
						errors.Add(text);
						if (errors.Count > 3)
						{
							errors.RemoveAt(0);
						}
					}
				}
				if (progressCallback == null)
				{
					return;
				}
				var match = SevenZProgressRegex.Match(text);
				if (match.Success)
				{
					progressCallback(match.Value);
				}
			}, workingDirectory: config.WorkingDirectory, encoding: Encoding.GetEncoding("GBK"));
		}
		catch (Exception e) when (e is not OperationCanceledException)
		{
			// 致命错误（例如密码错误）7z 写在标准错误里，ProcessRun 会把它放进消息。
			string message = Regex.Replace(e.Message, @"\s+", " ").Trim();
			// 标准错误为空时 ProcessRun 只会给出「7z 退出码 N」，这时要靠标准输出里的错误信息。
			if (message.Length == 0 || message.Contains("退出码", StringComparison.Ordinal))
			{
				IEnumerable<string> detail = errors.Count > 0 ? errors : messages.TakeLast(3);
				string output = string.Join(" ", detail);
				if (output.Length > 0)
				{
					message = $"{message} {output}".Trim();
				}
			}
			throw new Exception(message.Length > 0 ? $"解压失败: {message}" : "解压失败");
		}
	}

	/// <summary>
	/// 判断是否为 7z 结尾的统计信息，这些行对定位错误没有帮助。
	/// </summary>
	private static bool IsSevenZSummary(string line)
	{
		return line.StartsWith("Files:", StringComparison.Ordinal)
			|| line.StartsWith("Size:", StringComparison.Ordinal)
			|| line.StartsWith("Compressed:", StringComparison.Ordinal)
			|| line.StartsWith("Path = ", StringComparison.Ordinal)
			|| line.StartsWith("Type = ", StringComparison.Ordinal);
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

public class SevenZExtractConfig
{
	/// <summary>
	/// 要解压的压缩包路径。
	/// </summary>
	public required string FileName { get; set; }
	/// <summary>
	/// 解压到的目录。
	/// </summary>
	public required string OutputDirectory { get; set; }
	/// <summary>
	/// 解压密码。
	/// </summary>
	public string? Password { get; set; }
	/// <summary>
	/// 只解压的文件列表，为空时解压全部。
	/// </summary>
	public ICollection<string>? Files { get; set; }
	/// <summary>
	/// 解压的工作目录。
	/// </summary>
	public string? WorkingDirectory { get; set; }
}
