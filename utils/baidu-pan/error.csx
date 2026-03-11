static Exception GetErrnoException(int errno, string path)
{
	if (errno == -7)
	{
		return new Exception($"文件或目录 \"{path}\" 无权限访问");
	}
	else if (errno == -8)
	{
		return new Exception($"文件或目录 \"{path}\" 不存在");
	}
	else if (errno == -10)
	{
		return new Exception($"百度网盘容量不足");
	}
	else
	{
		return new Exception($"文件或目录 \"{path}\" 操作失败：{errno}");
	}
}
