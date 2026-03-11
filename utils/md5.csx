using System.Security.Cryptography;
using System.Text;

/// <summary>
/// 计算数据的 MD5。
/// </summary>
static string CalculateMD5(ReadOnlySpan<byte> data)
{
	byte[] hash = MD5.HashData(data);
	StringBuilder builder = new();
	for (int i = 0; i < hash.Length; i++)
	{
		builder.Append(hash[i].ToString("x2"));
	}
	return builder.ToString();
}
