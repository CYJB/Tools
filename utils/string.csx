/// <summary>
/// 生成随机字符串。
/// </summary>
static string RandomString(int minLength, int maxLength, string charset)
{
	var random = Random.Shared;
	int length = random.Next(minLength, maxLength + 1);
	var result = new StringBuilder(length);
	for (int i = 0; i < length; i++)
	{
		int index = random.Next(charset.Length);
		result.Append(charset[index]);
	}
	return result.ToString();
}
