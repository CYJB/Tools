#!/usr/bin/env -S dotnet --
#:include file.cs
#nullable enable

using Xabe.FFmpeg;

/// <summary>
/// ffmpeg 与视频相关的工具。
/// </summary>
class VideoUtils
{
	/// <summary>
	/// 视频的扩展名。
	/// </summary>
	public static readonly HashSet<string> Ext = [
		".mp4", ".avi", ".mkv", ".mov",
	];

	/// <summary>
	/// utils/ffmpeg 目录。
	/// </summary>
	public static readonly string FFmpegFolder = Path.Combine(FileUtils.GetScriptFolder(), "ffmpeg");

	/// <summary>
	/// ffmpeg 的可执行文件名，非 Windows 平台没有 .exe 后缀。
	/// </summary>
	private static readonly string FFmpegName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

	/// <summary>
	/// ffprobe 的可执行文件名，非 Windows 平台没有 .exe 后缀。
	/// </summary>
	private static readonly string FFprobeName = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

	/// <summary>
	/// utils/ffmpeg 下自带的 ffmpeg。
	/// </summary>
	public static readonly string FFmpegPath = Path.Combine(FFmpegFolder, FFmpegName);

	/// <summary>
	/// utils/ffmpeg 下自带的 ffprobe。
	/// </summary>
	public static readonly string FFprobePath = Path.Combine(FFmpegFolder, FFprobeName);

	/// <summary>
	/// 是否已配置过 ffmpeg 路径。
	/// </summary>
	private static bool hasSetupFFmpeg = false;

	/// <summary>
	/// 配置 ffmpeg 路径。
	/// </summary>
	public static void SetupFFmpeg()
	{
		if (hasSetupFFmpeg)
		{
			return;
		}
		hasSetupFFmpeg = true;
		FFmpeg.SetExecutablesPath(FFmpegFolder, FFmpegName);
	}
}
