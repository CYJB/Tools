#!/usr/bin/env -S dotnet --
#:include file.cs
#nullable enable

using Xabe.FFmpeg;

class VideoUtils
{
	/// <summary>
	/// 视频的扩展名。
	/// </summary>
	public static readonly HashSet<string> Ext = [
		".mp4", ".avi", ".mvk", ".mov",
	];

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
