#!/usr/bin/env -S dotnet --
#:package System.Text.Encoding.CodePages

using System.Text;

class CodePages
{
	private static bool isGBKRegistered = false;
	/// <summary>
	/// 注册 GBK 编码支持。
	/// </summary>
	public static void RegisterGBK()
	{
		if (!isGBKRegistered)
		{
			isGBKRegistered = true;
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		}
	}
}

