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
		".mp4", ".avi", ".mvk", ".mov",
	];

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
		FFmpeg.SetExecutablesPath(Path.Combine(FileUtils.GetScriptFolder(), "ffmpeg"), "ffmpeg.exe");
	}
}
