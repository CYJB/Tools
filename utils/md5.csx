using System.Security.Cryptography;
using System.Text;

/// <summary>
/// 计算数据的 MD5。
/// </summary>
static string CalculateMD5(ReadOnlySpan<byte> data)
{
	return GetMD5Hex(MD5.HashData(data));
}

/// <summary>
/// 计算指定文件的 MD5。
/// </summary>
static string CalculateFileMD5(string filepath)
{
	using var stream = File.OpenRead(filepath);
	return GetMD5Hex(MD5.HashData(stream));
}

/// <summary>
/// 计算 MD5 的 HEX 格式。
/// </summary>
static string GetMD5Hex(byte[] hash)
{
	StringBuilder builder = new();
	for (int i = 0; i < hash.Length; i++)
	{
		builder.Append(hash[i].ToString("x2"));
	}
	return builder.ToString();
}
