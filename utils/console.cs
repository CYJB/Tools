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
		return app.RunAsync(args, CreateCancellationSource().Token);
	}

	/// <summary>
	/// 运行带有多个子命令的应用，并把 Ctrl+C 转换成取消。
	/// </summary>
	public static Task<int> RunAsyncWithCancellation(Action<IConfigurator> configure, string[] args)
	{
		var app = new CommandApp();
		app.Configure(configure);
		return app.RunAsync(args, CreateCancellationSource().Token);
	}

	/// <summary>
	/// 创建把 Ctrl+C 转换成取消的通知源。第二次 Ctrl+C 不再拦截，让进程能被强制结束。
	/// </summary>
	private static CancellationTokenSource CreateCancellationSource()
	{
		var cancellationTokenSource = new CancellationTokenSource();
		Console.CancelKeyPress += (_, e) =>
		{
			if (!cancellationTokenSource.IsCancellationRequested)
			{
				e.Cancel = true;
				cancellationTokenSource.Cancel();
			}
		};
		return cancellationTokenSource;
	}
}
