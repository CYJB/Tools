#load "file.csx"
#r "nuget: Xabe.FFmpeg, 5.2.6"

#nullable enable

using Xabe.FFmpeg;

/// <summary>
/// 视频的扩展名。
/// </summary>
public static readonly HashSet<string> VideoExt = [
	".mp4", ".avi", ".mvk", ".mov",
];

private static bool hasSetupFFmpeg = false;

/// <summary>
/// 配置 ffmpeg 路径。
/// </summary>
static void SetupFFmpeg()
{
	if (hasSetupFFmpeg)
	{
		return;
	}
	hasSetupFFmpeg = true;
	FFmpeg.SetExecutablesPath(Path.Combine(GetScriptFolder(), "ffmpeg"), "ffmpeg.exe");
}
