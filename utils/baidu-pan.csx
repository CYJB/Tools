#load "baidu-pan/access-token.csx"
#load "baidu-pan/defines.csx"
#load "baidu-pan/error.csx"
#load "baidu-pan/upload-context.csx"
#load "baidu-pan/list-query.csx"
#load "http.csx"
#r "nuget: Spectre.Console, 0.54.0"
#nullable enable

using System;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

/// <summary>
/// 百度网盘接入。
/// </summary>
public class BaiduPan
{
	private const string FileAPI = "https://pan.baidu.com/rest/2.0/xpan/file";
	private static readonly HttpClient httpClient = new();

	/// <summary>
	/// 列出所有文件列表。
	/// </summary>
	/// <param name="dir">需要列出的目录，以 `/` 开头的绝对路径。</param>
	/// <param name="options">列出的选项。</param>
	public async Task<List<BaiduPanFileInfo>> List(string dir, BaiduPanListOptions? options = null)
	{
		string accessToken = await GetAccessTokenAsync();
		Dictionary<string, string> queries = GetListQuery(dir, accessToken, options);
		int start = 0;
		List<BaiduPanFileInfo> result = [];
		while (true)
		{
			queries["start"] = start.ToString();
			var json = await httpClient.GetStringAsync(FileAPI + BuildUriQuery(queries));
			var root = JsonDocument.Parse(json).RootElement;
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
				throw GetErrnoException(root.GetProperty("errno").GetInt32(), dir);
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
		string accessToken = await GetAccessTokenAsync();
		UploadContext context = new(accessToken, uploadPath, filePath);
		await context.Upload(progressCallback);
	}

	/// <summary>
	/// 重命名指定文件。
	/// </summary>
	public async Task Rename(string oldPath, string newName)
	{
		if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newName))
		{
			return;
		}
		string accessToken = await GetAccessTokenAsync();
		var url = FileAPI + BuildUriQuery(new() {
			{ "method", "filemanager" },
			{ "access_token", accessToken },
			{ "opera", "rename" },
		});
		RenameParams[] fileList = [new RenameParams(oldPath, newName)];
		var requestBody = new FormUrlEncodedContent([
			new("async", "0"),
			new("filelist", JsonSerializer.Serialize(fileList)),
			new("ondup", "overwrite"),
		]);
		FileManagerResult result = await PostJsonAsync<FileManagerResult>(httpClient, url, requestBody);
		if (result.Errno != 0)
		{
			throw GetErrnoException(result.Errno, oldPath);
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
		string accessToken = await GetAccessTokenAsync();
		var url = FileAPI + BuildUriQuery(new() {
			{ "method", "filemanager" },
			{ "access_token", accessToken },
			{ "opera", "delete" },
		});
		var requestBody = new FormUrlEncodedContent([
			new("async", "0"),
			new("filelist", JsonSerializer.Serialize(path)),
		]);
		FileManagerResult result = await PostJsonAsync<FileManagerResult>(httpClient, url, requestBody);
		if (result.Errno != 0)
		{
			throw GetErrnoException(result.Errno, path[0]);
		}
	}
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
	/// 重命名后的新路径。
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
}
