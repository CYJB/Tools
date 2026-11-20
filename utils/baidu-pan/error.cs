#!/usr/bin/env -S dotnet --
#nullable enable

/// <summary>
/// 网盘上的文件或目录不存在。
/// </summary>
/// <remarks>
/// 单独一个类型，方便调用方把「已经不存在」和真正的失败区分开（例如批量删除时有一半已经删掉了）。
/// </remarks>
public sealed class BaiduPanNotExistException(string path) : Exception($"文件或目录 \"{path}\" 不存在");

/// <summary>
/// 百度网盘的错误码。
/// </summary>
class BaiduPanError
{
	/// <summary>
	/// 把百度网盘的 errno 转换成异常。
	/// </summary>
	public static Exception GetErrnoException(int errno, string path)
	{
		return errno switch
		{
			-7 => new Exception($"文件或目录 \"{path}\" 无权限访问"),
			// 实测：rename 到已存在的名字返回 -8（即使带 ondup=overwrite 也不会覆盖），源不存在返回 -9。
			-8 => new Exception($"文件或目录 \"{path}\" 已存在"),
			-9 => new BaiduPanNotExistException(path),
			-10 => new Exception($"百度网盘容量不足"),
			31034 => new Exception($"\"{path}\" 请求过于频繁，请稍后再试"),
			_ => new Exception($"文件或目录 \"{path}\" 操作失败：{errno}"),
		};
	}
}
