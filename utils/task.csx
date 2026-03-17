#r "nuget: Spectre.Console, 0.54.0"

#nullable enable

using System.Collections.Concurrent;
using System.Threading;
using Spectre.Console;

/// <summary>
/// 执行指定的任务。
/// </summary>
static Task RunTaskAsync(List<Func<TaskContext, Task>> tasks, CancellationToken? cancellationToken = null)
{
	CancellationToken token = cancellationToken == null ? CancellationToken.None : cancellationToken.Value;
	return AnsiConsole.Live(new Rows())
		.StartAsync(ctx => new TaskRunContext(tasks, ctx, token).Run());
}

/// <summary>
/// 任务的运行上下文。
/// </summary>
private class TaskRunContext
{
	/// <summary>
	/// 任务总数。
	/// </summary>
	private readonly int taskCount;
	/// <summary>
	/// 任务队列。
	/// </summary>
	private readonly ConcurrentQueue<Func<TaskContext, Task>> taskQueue;
	/// <summary>
	/// 动态显示的上下文。
	/// </summary>
	private readonly LiveDisplayContext ctx;
	/// <summary>
	/// 取消 Token。
	/// </summary>
	private readonly CancellationToken cancellationToken;
	/// <summary>
	/// 使用处理器个数个线程并处处理任务。
	/// </summary>
	private readonly int threadCount = Environment.ProcessorCount;
	/// <summary>
	/// 成功任务数。
	/// </summary>
	private int successCount = 0;
	/// <summary>
	/// 失败任务数。
	/// </summary>
	private int failureCount = 0;
	/// <summary>
	/// 动态显示的内容。
	/// </summary>
	private readonly Markup?[] widgets;
	/// <summary>
	/// 串行输出控制台结果的锁。
	/// </summary>
	private readonly Mutex consoleMutex = new();
	/// <summary>
	/// 任务的锁。
	/// </summary>
	private readonly Mutex taskMutex = new();
	/// <summary>
	/// 当前的阻塞型任务。
	/// </summary>
	private Task? blockTask = null;

	public TaskRunContext(List<Func<TaskContext, Task>> tasks, LiveDisplayContext ctx, CancellationToken cancellationToken)
	{
		taskCount = tasks.Count;
		taskQueue = new ConcurrentQueue<Func<TaskContext, Task>>(tasks);
		this.ctx = ctx;
		this.cancellationToken = cancellationToken;
		// 首个 widget 总是用于记录进度。
		widgets = new Markup?[threadCount + 1];
		widgets[0] = new Markup($"执行任务 [green]0[/]/{taskCount}");
	}

	/// <summary>
	/// 运行当前任务列表。
	/// </summary>
	public Task Run()
	{
		Task[] tasks = new Task[threadCount];
		for (int i = 0; i < threadCount; i++)
		{
			var context = new TaskContext(i, UpdateStatus, MarkupLine, cancellationToken);
			int index = i;
			tasks[i] = RunTask(context);
		}
		return Task.WhenAll(tasks);
	}

	/// <summary>
	/// 使用指定的上下文执行任务。
	/// </summary>
	private async Task RunTask(TaskContext context)
	{
		// 确保任务是异步的。
		await Task.Delay(1);
		while (taskQueue.TryDequeue(out Func<TaskContext, Task>? task))
		{
			try
			{
				// 清除状态信息。
				context.Status(null);
				await WaitBlockTask();
				context.IsBlock = false;
				var curTask = task(context);
				Task? curBlockTask = null;
				if (context.IsBlock)
				{
					// 需要阻塞其它任务执行。
					curBlockTask = new Task(() => { });
					taskMutex.WaitOne();
					Task? oldBlockTask = blockTask;
					blockTask = curBlockTask;
					taskMutex.ReleaseMutex();
					if (oldBlockTask != null)
					{
						// 等待之前的任务执行完毕。
						await oldBlockTask;
					}
				}
				await curTask;
				TaskFinished(true);
				// 执行其它被阻塞的任务。
				if (curBlockTask != null)
				{
					taskMutex.WaitOne();
					if (blockTask == curBlockTask)
					{
						blockTask = null;
					}
					taskMutex.ReleaseMutex();
					curBlockTask.Start();
				}
			}
			catch (Exception e)
			{
				// 忽略任务被取消的错误。
				if (e is not OperationCanceledException)
				{
					consoleMutex.WaitOne();
					AnsiConsole.MarkupLine("[red]Error: [/]" + context.StatusMarkup);
					AnsiConsole.WriteException(e, ExceptionFormats.ShortenPaths);
					consoleMutex.ReleaseMutex();
				}
				TaskFinished(false);
			}
		}
		context.Status(null);
	}

	/// <summary>
	/// 更新指定任务的状态。
	/// </summary>
	private void UpdateStatus(int index, string? markup)
	{
		consoleMutex.WaitOne();
		// 首个 widget 总是用于记录进度。
		if (markup != null || widgets[index + 1] != null)
		{
			widgets[index + 1] = markup == null ? null : new Markup(markup);
			var rows = new Rows(widgets.Where(widget => widget != null).Cast<Markup>());
			ctx.UpdateTarget(rows);
		}
		consoleMutex.ReleaseMutex();
	}

	/// <summary>
	/// 输出指定的控制台信息。
	/// </summary>
	private void MarkupLine(string markup)
	{
		consoleMutex.WaitOne();
		AnsiConsole.MarkupLine(markup);
		consoleMutex.ReleaseMutex();
	}

	/// <summary>
	/// 指示任务执行完毕。
	/// </summary>
	private void TaskFinished(bool isSuccess)
	{
		if (isSuccess)
		{
			Interlocked.Increment(ref successCount);
		}
		else
		{
			Interlocked.Increment(ref failureCount);
		}
		var message = $"执行任务 [green]{successCount}[/]/{taskCount}";
		if (failureCount > 0)
		{
			message += $" 失败 [red]{failureCount}[/]";
		}
		UpdateStatus(-1, message);
	}

	/// <summary>
	/// 等待阻塞型任务执行完毕。
	/// </summary>
	private async Task WaitBlockTask()
	{
		while (true)
		{
			Task? oldBlockTask = null;
			taskMutex.WaitOne();
			if (blockTask != null)
			{
				oldBlockTask = blockTask;
			}
			taskMutex.ReleaseMutex();
			if (oldBlockTask == null)
			{
				return;
			}
			else
			{
				await oldBlockTask;
			}
		}
	}
}

/// <summary>
/// 任务上下文。
/// </summary>
public class TaskContext(int index, Action<int, string?> updateStatus, Action<string> markupLine, CancellationToken cancellationToken)
{
	private readonly int index = index;
	private readonly Action<int, string?> updateStatus = updateStatus;
	private readonly Action<string> markupLine = markupLine;

	/// <summary>
	/// 获取或设置是否是阻塞型任务，需要阻塞其它任务的执行。
	/// </summary>
	public bool IsBlock { get; set; }
	/// <summary>
	/// 取消通知。
	/// </summary>
	public CancellationToken CancellationToken { get; init; } = cancellationToken;

	/// <summary>
	/// 获取状态文本。
	/// </summary>
	public string? StatusMarkup { get; private set; }

	/// <summary>
	/// 设置任务的状态。
	/// </summary>
	public void Status(string? markup)
	{
		StatusMarkup = markup;
		updateStatus(index, markup);
	}
	/// <summary>
	/// 输出额外的控制台文本，禁止使用 AnsiConsole 以避免出现多线程问题。
	/// </summary>
	public void MarkupLine(string markup)
	{
		markupLine(markup);
	}
}
