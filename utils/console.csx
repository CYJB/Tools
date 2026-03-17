using System.Threading;
using Spectre.Console.Cli;

static Task<int> RunAsyncWithCancellation<TDefaultCommand>(string[] args)
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
