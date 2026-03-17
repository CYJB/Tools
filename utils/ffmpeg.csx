#load "file.csx"
#r "nuget: Xabe.FFmpeg, 5.2.6"

#nullable enable

using Xabe.FFmpeg;

/// <summary>
/// 配置 ffmpeg 路径。
/// </summary>
static void SetupFFmpeg()
{
	FFmpeg.SetExecutablesPath(Path.Combine(GetScriptFolder(), "ffmpeg"), "ffmpeg.exe");
}
