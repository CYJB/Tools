#!/usr/bin/env -S dotnet --
#:include baidu-pan/access-token.cs
#:include baidu-pan/defines.cs
#:include baidu-pan/error.cs
#:include baidu-pan/list-query.cs
#:include baidu-pan/upload-context.cs
#:include http.cs
#:package Spectre.Console
#nullable enable

using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 百度网盘接入。
/// </summary>
public class BaiduPan
{
	private const string FileAPI = "https://pan.baidu.com/rest/2.0/xpan/file";
	private const string MultimediaAPI = "https://pan.baidu.com/rest/2.0/xpan/multimedia";
	/// <summary>
	/// 请求过于频繁时返回的错误码。
	/// </summary>
	private const int FrequencyLimitErrno = 31034;
	/// <summary>
	/// 文件管理接口单次请求的文件数上限。
	/// </summary>
	/// <remarks>
	/// 实测超过 50 个时整批都不生效（外层返回 errno 12，逐项返回 -9），50 及以下正常。
	/// </remarks>
	private const int FileManagerBatchSize = 50;
	/// <summary>
	/// 递归列目录时每页的最大条数。
	/// </summary>
	private const int ListAllPageSize = 1000;

	private static readonly HttpClient httpClient = new();

	/// <summary>
	/// 列出所有文件列表。
	/// </summary>
	/// <param name="dir">需要列出的目录，以 `/` 开头的绝对路径。</param>
	/// <param name="options">列出的选项。</param>
	public async Task<List<BaiduPanFileInfo>> List(string dir, BaiduPanListOptions? options = null)
	{
		string accessToken = await BaiduPanAuth.GetAccessTokenAsync();
		Dictionary<string, string> queries = BaiduPanListQuery.GetListQuery(dir, accessToken, options);
		int start = 0;
		List<BaiduPanFileInfo> result = [];
		while (true)
		{
			queries["start"] = start.ToString();
			var root = await GetJsonAsync(FileAPI + HttpUtils.BuildUriQuery(queries));
			if (root.TryGetProperty("list", out var list))
			{
				foreach (var item in list.EnumerateArray())
				{
					var info = JsonSerializer.Deserialize<BaiduPanFileInfo>(item)!;
					result.Add(info);
				}
				if (list.GetArrayLength() < 1000)
				{
					// 当前页已请求完毕。
					break;
				}
				start += 1000;
			}
			else
			{
				var errno = root.GetProperty("errno").GetInt32();
				if (errno == -9)
				{
					// 目录不存在，返回空路径。
					return result;
				}
				throw BaiduPanError.GetErrnoException(errno, dir);
			}
		}
		return result;
	}

	/// <summary>
	/// 上传指定文件。
	/// </summary>
	/// <param name="uploadPath">要上传到的路径，以 / 开头。</param>
	/// <param name="filePath">要上传的文件路径。</param>
	/// <param name="progressCallback">上传进度回调。</param>
	public async Task Upload(string uploadPath, string filePath, Action<float>? progressCallback = null)
	{
		string accessToken = await BaiduPanAuth.GetAccessTokenAsync();
		UploadContext context = new(accessToken, uploadPath, filePath);
		await context.Upload(progressCallback);
	}

	/// <summary>
	/// 递归列出指定目录下的所有文件和子目录。
	/// </summary>
	/// <param name="dir">要列出的目录，以 / 开头的绝对路径。</param>
	/// <remarks>
	/// 使用 <c>multimedia?method=listall</c> 接口：目录参数名是 <c>path</c>（不是 <c>parent_path</c>），
	/// 分页使用 <c>start</c>/<c>limit</c>，<c>has_more</c> 为 0 表示没有更多数据，返回项包含 <c>isdir</c>。
	/// </remarks>
	public async Task<List<BaiduPanFileInfo>> ListAll(string dir)
	{
		string accessToken = await BaiduPanAuth.GetAccessTokenAsync();
		dir = TrimEndSlash(dir);
		int start = 0;
		List<BaiduPanFileInfo> result = [];
		while (true)
		{
			var url = MultimediaAPI + HttpUtils.BuildUriQuery(new() {
				{ "method", "listall" },
				{ "access_token", accessToken },
				{ "path", dir },
				{ "recursion", "1" },
				{ "start", start.ToString() },
				{ "limit", ListAllPageSize.ToString() },
			});
			var root = await GetJsonAsync(url);
			if (!root.TryGetProperty("list", out var list))
			{
				throw BaiduPanError.GetErrnoException(root.TryGetProperty("errno", out var errno) ? errno.GetInt32() : -1, dir);
			}
			foreach (var item in list.EnumerateArray())
			{
				result.Add(JsonSerializer.Deserialize<BaiduPanFileInfo>(item)!);
			}
			// has_more 为 0 表示已经取完；接口没返回该字段时按「还有更多」处理，靠空页结束。
			bool hasMore = !root.TryGetProperty("has_more", out var hasMoreElement) || hasMoreElement.GetInt32() != 0;
			if (!hasMore || list.GetArrayLength() == 0)
			{
				break;
			}
			start += list.GetArrayLength();
			// 连续翻页容易触发频率限制，这里主动放慢一点。
			await Task.Delay(TimeSpan.FromMilliseconds(200));
		}
		return result;
	}

	/// <summary>
	/// 发送 Get 请求并返回 JSON，请求过于频繁时等待后重试。
	/// </summary>
	private static async Task<JsonElement> GetJsonAsync(string url)
	{
		for (int retryCount = 0; ; retryCount++)
		{
			using var response = await httpClient.GetAsync(url);
			string json = await response.Content.ReadAsStringAsync();
			var root = JsonDocument.Parse(json).RootElement;
			var errno = root.TryGetProperty("errno", out var errnoElement) ? errnoElement.GetInt32() : 0;
			if (errno != FrequencyLimitErrno)
			{
				return root;
			}
			if (retryCount >= 3)
			{
				// 重试次数用尽，交由调用方按错误码处理。
				return root;
			}
			// 请求过于频繁，按 3、6、9 秒递增等待。
			await Task.Delay(TimeSpan.FromSeconds(3 * (retryCount + 1)));
		}
	}

	/// <summary>
	/// 创建指定目录。
	/// </summary>
	/// <param name="path">要创建的目录，以 / 开头的绝对路径。</param>
	/// <remarks>
	/// 目录已存在时百度网盘不会报错，而是自动改名后创建（例如 <c>dir_20261027_232719</c>），
	/// 所以这里要比较返回的路径，把多建出来的目录删掉，按「目录已存在」处理。
	/// </remarks>
	public async Task CreateDir(string path)
	{
		// 目录路径不能以 / 结尾，否则末尾的 / 会被当成名称的一部分。
		path = TrimEndSlash(path);
		string accessToken = await BaiduPanAuth.GetAccessTokenAsync();
		var url = FileAPI + HttpUtils.BuildUriQuery(new() {
			{ "method", "create" },
			{ "access_token", accessToken },
		});
		var requestBody = new FormUrlEncodedContent([
			new("path", path),
			new("isdir", "1"),
		]);
		FileManagerResult result = await HttpUtils.PostJsonAsync<FileManagerResult>(httpClient, url, requestBody);
		if (result.Errno != 0)
		{
			throw BaiduPanError.GetErrnoException(result.Errno, path);
		}
		// 返回的路径被改名了，说明目录本来就存在，把多建的那个删掉。
		if (result.Path != null && !string.Equals(result.Path, path, StringComparison.Ordinal))
		{
			await Delete(result.Path);
		}
	}

	/// <summary>
	/// 重命名指定文件。
	/// </summary>
	/// <param name="files">要重命名的文件，包含原路径和新文件名。</param>
	public async Task Rename(params (string Path, string NewName)[] files)
	{
		if (files.Length == 0)
		{
			return;
		}
		string accessToken = await BaiduPanAuth.GetAccessTokenAsync();
		var url = FileAPI + HttpUtils.BuildUriQuery(new() {
			{ "method", "filemanager" },
			{ "access_token", accessToken },
			{ "opera", "rename" },
		});
		// 单次请求的文件数有上限，分批处理。
		foreach (var chunk in files.Chunk(FileManagerBatchSize))
		{
			RenameParams[] fileList = chunk.Select(file => new RenameParams(file.Path, file.NewName)).ToArray();
			var requestBody = new FormUrlEncodedContent([
				new("async", "0"),
				new("filelist", JsonSerializer.Serialize(fileList)),
				new("ondup", "overwrite"),
			]);
			FileManagerResult result = await HttpUtils.PostJsonAsync<FileManagerResult>(httpClient, url, requestBody);
			if (result.Errno != 0)
			{
				throw GetOperateException(result, chunk[0].Path);
			}
		}
	}

	/// <summary>
	/// 删除指定文件。
	/// </summary>
	public async Task Delete(params string[] path)
	{
		if (path.Length == 0)
		{
			return;
		}
		string accessToken = await BaiduPanAuth.GetAccessTokenAsync();
		var url = FileAPI + HttpUtils.BuildUriQuery(new() {
			{ "method", "filemanager" },
			{ "access_token", accessToken },
			{ "opera", "delete" },
		});
		// 单次请求的文件数有上限，分批处理。
		foreach (var chunk in path.Chunk(FileManagerBatchSize))
		{
			var requestBody = new FormUrlEncodedContent([
				new("async", "0"),
				new("filelist", JsonSerializer.Serialize(chunk)),
			]);
			FileManagerResult result = await HttpUtils.PostJsonAsync<FileManagerResult>(httpClient, url, requestBody);
			if (result.Errno != 0)
			{
				throw GetOperateException(result, chunk[0]);
			}
		}
	}

	/// <summary>
	/// 移动指定文件到目标目录。
	/// </summary>
	/// <param name="destDir">目标目录，以 / 开头的绝对路径。</param>
	/// <param name="files">要移动的文件，包含原路径和移动后的文件名。</param>
	public async Task Move(string destDir, params (string Path, string NewName)[] files)
	{
		if (files.Length == 0)
		{
			return;
		}
		// 目标目录不能以 / 结尾，否则末尾的 / 会被当成名称的一部分。
		destDir = TrimEndSlash(destDir);
		string accessToken = await BaiduPanAuth.GetAccessTokenAsync();
		var url = FileAPI + HttpUtils.BuildUriQuery(new() {
			{ "method", "filemanager" },
			{ "access_token", accessToken },
			{ "opera", "move" },
		});
		// 单次请求的文件数有上限，分批处理。
		foreach (var chunk in files.Chunk(FileManagerBatchSize))
		{
			MoveParams[] fileList = chunk.Select(file => new MoveParams(file.Path, destDir, file.NewName)).ToArray();
			var requestBody = new FormUrlEncodedContent([
				new("async", "0"),
				new("filelist", JsonSerializer.Serialize(fileList)),
				// 目标文件已存在时直接报错，避免覆盖掉已有的备份。
				new("ondup", "fail"),
			]);
			FileManagerResult result = await HttpUtils.PostJsonAsync<FileManagerResult>(httpClient, url, requestBody);
			if (result.Errno != 0)
			{
				throw GetOperateException(result, chunk[0].Path);
			}
		}
	}

	/// <summary>
	/// 移除目录路径末尾的 /。
	/// </summary>
	private static string TrimEndSlash(string path)
	{
		return path.Length > 1 ? path.TrimEnd('/') : path;
	}

	/// <summary>
	/// 返回文件操作失败时的异常，尽量带上每个文件的具体错误码。
	/// </summary>
	/// <remarks>
	/// 网盘的外层 errno 只是汇总码（例如 12 表示「批量操作失败」），
	/// 真正的原因在每个文件的 <c>info[].errno</c> 里，需要一并输出才好排查。
	/// </remarks>
	private static Exception GetOperateException(FileManagerResult result, string path)
	{
		var failed = result.Info?.Where(info => info.Errno != 0).Take(3).ToArray() ?? [];
		if (failed.Length == 0)
		{
			return BaiduPanError.GetErrnoException(result.Errno, path);
		}
		string detail = string.Join("，", failed.Select(info => $"{info.Path}={info.Errno}"));
		return new Exception($"\"{path}\" 操作失败：{result.Errno}（{detail}）");
	}

	/// <summary>
	/// 文件移动参数。
	/// </summary>
	private class MoveParams(string path, string dest, string newName)
	{
		/// <summary>
		/// 文件的原路径。
		/// </summary>
		[JsonPropertyName("path")]
		public string Path { get; init; } = path;
		/// <summary>
		/// 目标目录。
		/// </summary>
		[JsonPropertyName("dest")]
		public string Dest { get; init; } = dest;
		/// <summary>
		/// 移动后的新文件名。
		/// </summary>
		[JsonPropertyName("newname")]
		public string NewName { get; init; } = newName;
	}

	/// <summary>
	/// 文件重命名参数。
	/// </summary>
	private class RenameParams(string path, string newName)
	{
		/// <summary>
		/// 文件路径。
		/// </summary>
		[JsonPropertyName("path")]
		public string Path { get; init; } = path;
		/// <summary>
		/// 重命名后的新文件名。
		/// </summary>
		[JsonPropertyName("newname")]
		public string NewName { get; init; } = newName;
	}

	/// <summary>
	/// 文件操作结果。
	/// </summary>
	private class FileManagerResult
	{
		/// <summary>
		/// 错误码。
		/// </summary>
		[JsonPropertyName("errno")]
		public int Errno { get; set; }
		/// <summary>
		/// 错误信息。
		/// </summary>
		[JsonPropertyName("errmsg")]
		public string? ErrMsg { get; set; } = null;
		/// <summary>
		/// 创建出来的路径，仅创建目录时返回。
		/// </summary>
		[JsonPropertyName("path")]
		public string? Path { get; set; } = null;
		/// <summary>
		/// 每个文件的操作结果，批量操作时返回。
		/// </summary>
		[JsonPropertyName("info")]
		public List<FileManagerInfo>? Info { get; set; } = null;
	}

	/// <summary>
	/// 单个文件的操作结果。
	/// </summary>
	private class FileManagerInfo
	{
		/// <summary>
		/// 错误码。
		/// </summary>
		[JsonPropertyName("errno")]
		public int Errno { get; set; }
		/// <summary>
		/// 文件路径。
		/// </summary>
		[JsonPropertyName("path")]
		public string? Path { get; set; } = null;
	}
}
