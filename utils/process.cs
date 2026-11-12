#!/usr/bin/env -S dotnet --
#nullable enable

using System.Diagnostics;
using System.Text;

/// <summary>
/// 外部进程执行的辅助方法。
/// </summary>
class ProcessRun
{
	/// <summary>
	/// 子进程输出的默认编码。ffmpeg / ffprobe / calibredb 输出的都是 UTF-8。
	/// </summary>
	private static readonly UTF8Encoding Utf8NoBom = new(false);

	/// <summary>
	/// 执行指定的命令，非 0 退出码时抛出异常。
	/// </summary>
	/// <param name="fileName">要执行的可执行文件。</param>
	/// <param name="args">参数列表。使用 <c>ArgumentList</c> 逐个传递，避免拼接成字符串时丢掉参数里的首尾空格
	/// （例如 ffmpeg 的 <c>-brand "M4B "</c>）。</param>
	/// <param name="onOutputLine">标准输出的逐行回调，用于解析进度信息。</param>
	/// <param name="cancellationToken">取消通知。</param>
	/// <param name="workingDirectory">进程的工作目录，默认继承当前目录（calibredb 要在自己的目录下运行）。</param>
	/// <param name="encoding">输出的编码，默认 UTF-8。7z 输出的是控制台代码页（简体中文下是 GBK）。</param>
	public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args,
		Action<string>? onOutputLine = null, CancellationToken cancellationToken = default,
		string? workingDirectory = null, Encoding? encoding = null)
	{
		encoding ??= Utf8NoBom;
		using Process process = new();
		process.StartInfo = new ProcessStartInfo(fileName, args)
		{
			WorkingDirectory = workingDirectory ?? string.Empty,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			// Windows 上默认用控制台输出代码页（例如 chcp 936 时的 GBK）解码子进程输出，
			// 而 ffmpeg / ffprobe 输出的始终是 UTF-8，中文会变成乱码。
			StandardOutputEncoding = encoding,
			StandardErrorEncoding = encoding,
		};
		StringBuilder output = new();
		StringBuilder error = new();
		process.OutputDataReceived += (_, e) =>
		{
			if (e.Data == null)
			{
				return;
			}
			output.AppendLine(e.Data);
			onOutputLine?.Invoke(e.Data);
		};
		process.ErrorDataReceived += (_, e) =>
		{
			if (e.Data != null)
			{
				error.AppendLine(e.Data);
			}
		};
		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		// 取消时杀掉进程树，ffmpeg 会派生出子进程。
		using CancellationTokenRegistration registration = cancellationToken.Register(() =>
		{
			try
			{
				if (!process.HasExited)
				{
					process.Kill(true);
				}
			}
			catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
			{
				// 进程已经退出，忽略。
			}
		});
		await process.WaitForExitAsync(cancellationToken);
		// WaitForExitAsync 只保证进程已退出，异步输出回调可能还没处理完，需要再等一次。
		process.WaitForExit();
		if (process.ExitCode != 0)
		{
			string message = error.ToString().Trim();
			throw new Exception(message.Length > 0 ? message : $"{Path.GetFileName(fileName)} 退出码 {process.ExitCode}");
		}
		return new ProcessResult(output.ToString(), error.ToString());
	}
}

/// <summary>
/// 进程的执行结果。
/// </summary>
/// <param name="StdOut">标准输出的内容。</param>
/// <param name="StdErr">标准错误的内容。有些程序（例如 ffprobe）会把警告写到这里，即使退出码是 0。</param>
public sealed record ProcessResult(string StdOut, string StdErr);
