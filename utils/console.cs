#!/usr/bin/env -S dotnet --
#nullable enable

using System.Threading;
using Spectre.Console.Cli;

/// <summary>
/// 控制台的辅助方法。
/// </summary>
class ConsoleApp
{
	/// <summary>
	/// 运行命令，并把 Ctrl+C 转换成取消。
	/// </summary>
	public static Task<int> RunAsyncWithCancellation<TDefaultCommand>(string[] args)
		where TDefaultCommand : class, ICommand
	{
		var app = new CommandApp<TDefaultCommand>();
		var cancellationTokenSource = new CancellationTokenSource();
		Console.CancelKeyPress += (_, e) =>
		{
			e.Cancel = true;
			cancellationTokenSource.Cancel();
		};
		return app.RunAsync(args, cancellationTokenSource.Token);
	}
}
