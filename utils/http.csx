using System.Text;

/**
 * 拼接 URI 的参数。
 */
static string BuildUriQuery(Dictionary<string, string> queries, bool addSeperator = true)
{
	if (queries == null || queries.Count == 0)
	{
		return "";
	}
	StringBuilder builder = new();
	if (addSeperator)
	{
		builder.Append('?');
	}
	bool isFirst = true;
	foreach (var query in queries)
	{
		if (isFirst)
		{
			isFirst = false;
		}
		else
		{
			builder.Append('&');
		}
		builder.Append(query.Key);
		builder.Append('=');
		builder.Append(Uri.EscapeDataString(query.Value));
	}
	return builder.ToString();
}
