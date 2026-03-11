#load "defines.csx"
#nullable enable

/// <summary>
/// 返回 List 选项。
/// </summary>
static Dictionary<string, string> GetListQuery(string dir, string accessToken, BaiduPanListOptions? options)
{
	Dictionary<string, string> queries = new() {
		{ "method", "list" },
		{ "access_token", accessToken },
		{ "dir", dir },
		// 总是返回 dir_empty 属性。
		{ "showempty", "1" },
		// 总是返回最大 1000 条数据。
		{ "limit", "1000" },
	};
	if (options != null)
	{
		if (options.Order != null)
		{
			string order = "";
			switch (options.Order.Value)
			{
				case BaiduPanListOrder.Name:
					order = "name";
					break;
				case BaiduPanListOrder.Time:
					order = "time";
					break;
				case BaiduPanListOrder.Size:
					order = "size";
					break;
			}
			queries.Add("order", order);
		}
		if (options.Desc != null && options.Desc.Value)
		{
			queries.Add("desc", "1");
		}
		if (options.Thumbs != null && options.Thumbs.Value)
		{
			queries.Add("web", "1");
		}
		if (options.IsFolderOnly != null && options.IsFolderOnly.Value)
		{
			queries.Add("folder", "1");
		}
	}
	return queries;
}
