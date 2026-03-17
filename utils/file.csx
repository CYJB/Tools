using System.Runtime.CompilerServices;

/// <summary>
/// 图片的扩展名。
/// </summary>
public static readonly HashSet<string> ImageExt = [
	".jpg", ".jpeg", ".png", ".bmp", ".heic",
];

/// <summary>
/// 视频的扩展名。
/// </summary>
public static readonly HashSet<string> VideoExt = [
	".mp4", ".avi", ".mvk", ".mov",
];

/**
 * 返回当前脚本所在的文件夹。
 */
static string GetScriptFolder([CallerFilePath] string path = null)
{
	return Path.GetDirectoryName(path);
}

/// <summary>
/// 安全删除指定文件
/// </summary>
static async Task SafeDeleteFile(string file)
{
	try
	{
		File.Delete(file);
	}
	catch (IOException)
	{
		// 可能是文件在被其它进程使用，等待 1s 后重试
		await Task.Delay(1000);
		File.Delete(file);
	}
	catch (UnauthorizedAccessException)
	{
		// 可能是文件被设置为只读，修改属性
		FileAttributes attributes = File.GetAttributes(file);
		attributes &= ~FileAttributes.ReadOnly;
		File.SetAttributes(file, attributes);
		// 重试删除
		File.Delete(file);
	}
}
