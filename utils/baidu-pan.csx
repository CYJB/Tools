#load "baidu-pan/access-token.csx"
#load "baidu-pan/defines.csx"
#load "baidu-pan/error.csx"
#load "baidu-pan/file-block.csx"
#load "baidu-pan/list-query.csx"
#load "http.csx"
#r "nuget: Spectre.Console, 0.48.0"
#nullable enable

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

/// <summary>
/// 百度网盘接入。
/// </summary>
public class BaiduPan
{
	private const string FileAPI = "https://pan.baidu.com/rest/2.0/xpan/file";
	private const string PcsFileAPI = "https://d.pcs.baidu.com/rest/2.0/pcs/file";
	private const string SuperFile2Path = "/rest/2.0/pcs/superfile2";
	/// <summary>
	/// 上传分片大小。
	/// </summary>
	private const int BlockSize = 4 * 1024 * 1024;
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
	/// <param name="label">上传提示。</param>
	public async Task Upload(string uploadPath, string filePath, string? hint = null)
	{
		string accessToken = await GetAccessTokenAsync();
		hint ??= $"{filePath} 到 {uploadPath}";
		await AnsiConsole.Status().StartAsync($"正在上传 {hint}", async ctx =>
		{
			var fileInfo = new FileInfo(filePath);
			var fileSize = fileInfo.Length;
			FileBlock[] blocks = await GetFileBlocks(filePath, BlockSize);
			var blockList = JsonSerializer.Serialize(blocks.Select(block => block.MD5))!;
			// 1. 预上传
			var requestBody = new FormUrlEncodedContent([
				new("path", uploadPath),
				new("size", fileSize.ToString()),
				new("isdir", "0"),
				new("block_list", blockList),
				new("autoinit", "1"),
				new("rtype", "1"),
				new("local_ctime", new DateTimeOffset(fileInfo.CreationTime).ToUnixTimeMilliseconds().ToString()),
			]);
			var response = await httpClient.PostAsync(FileAPI + BuildUriQuery(new() {
				{ "method", "precreate" },
				{ "access_token", accessToken },
			}), requestBody);
			var json = await response.Content.ReadAsStringAsync();
			var preCreateResult = JsonSerializer.Deserialize<PreCreateResult>(json)!;
			if (preCreateResult.Errno != 0)
			{
				throw GetErrnoException(preCreateResult.Errno, uploadPath);
			}
			var uploadId = preCreateResult.UploadId;

			// 2. 分片上传
			UploadResult uploadResult;
			using (FileStream fs = new(filePath, FileMode.Open, FileAccess.Read))
			{
				byte[] buffer = new byte[BlockSize];
				int uploadedSize = 0;
				for (int i = 0; i < blocks.Length; i++)
				{
					// 2.a 获取上传域名。
					json = await httpClient.GetStringAsync(PcsFileAPI + BuildUriQuery(new()
					{
						{ "method", "locateupload" },
						{ "appid", "250528" },
						{ "access_token", accessToken },
						{ "path", uploadPath },
						{ "uploadid", uploadId },
						{ "upload_version", "2.0" },
					}));
					var locateResult = JsonSerializer.Deserialize<LocateUploadResult>(json)!;
					var server = locateResult.Servers[0].Server;

					// 2.b 上传分片。
					var content = new MultipartFormDataContent();
					var block = blocks[i];
					fs.Seek(block.Offset, SeekOrigin.Begin);
					var size = await fs.ReadAsync(buffer);
					var fileContent = new ByteArrayContent(buffer, 0, size);
					fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
					content.Add(fileContent, "file", "tmpfile");
					response = await httpClient.PostAsync(server + SuperFile2Path + BuildUriQuery(new()
					{
						{ "method", "upload" },
						{ "access_token", accessToken },
						{ "type", "tmpfile" },
						{ "path", uploadPath },
						{ "uploadid", uploadId },
						{ "partseq", i.ToString() },
					}), content);
					var stream = response.Content.ReadAsStream();
					using (var reader = new StreamReader(stream))
					{
						uploadResult = JsonSerializer.Deserialize<UploadResult>(await reader.ReadToEndAsync())!;
						if (uploadResult.Errno != null)
						{
							throw GetErrnoException(uploadResult.Errno.Value, $"{uploadPath}[{i}");
						}
						else if (uploadResult.MD5 != blocks[i].MD5)
						{
							throw new Exception($"上传 {uploadPath}[{i}] 异常：MD5 {uploadResult.MD5} 与预期 {blocks[i].MD5} 不符");
						}
					}

					uploadedSize += size;
					ctx.Status($"正在上传 {hint} {uploadedSize * 100 / fileSize}%");
				}
			}

			await Task.Delay(1000);

			// 3. 合并文件
			requestBody = new FormUrlEncodedContent([
				new("path", uploadPath),
				new("size", fileSize.ToString()),
				new("isdir", "0"),
				new("block_list", blockList),
				new("uploadid", uploadId),
				new("rtype", "1"),
			]);
			response = await httpClient.PostAsync(FileAPI + BuildUriQuery(new() {
				{ "method", "create" },
				{ "access_token", accessToken },
			}), requestBody);
			json = await response.Content.ReadAsStringAsync();
			uploadResult = JsonSerializer.Deserialize<UploadResult>(json)!;
			if (uploadResult.Errno != null && uploadResult.Errno.Value != 0)
			{
				throw GetErrnoException(uploadResult.Errno.Value, uploadPath);
			}
		});
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
		var requestBody = new FormUrlEncodedContent([
			new("async", "2"),
			new("filelist", JsonSerializer.Serialize(path)),
		]);
		var response = await httpClient.PostAsync(FileAPI + BuildUriQuery(new() {
			{ "method", "filemanager" },
			{ "access_token", accessToken },
			{ "opera", "delete" },
		}), requestBody);
		var json = await response.Content.ReadAsStringAsync();
		var info = JsonSerializer.Deserialize<FileManagerResult>(json)!;
		if (info.Errno != 0)
		{
			throw GetErrnoException(info.Errno, path[0]);
		}
	}
}

/// <summary>
/// 预上传结果。
/// </summary>
private class PreCreateResult
{
	/// <summary>
	/// 错误码。
	/// </summary>
	[JsonPropertyName("errno")]
	public int Errno { get; set; }
	/// <summary>
	/// 上传的唯一标识。
	/// </summary>
	[JsonPropertyName("uploadid")]
	public required string UploadId { get; set; }
}

private class LocateUploadServerResult
{
	/// <summary>
	/// 上传的服务端信息。
	/// </summary>
	[JsonPropertyName("server")]
	public required string Server { get; set; }
}

/// <summary>
/// 上传域名结果。
/// </summary>
private class LocateUploadResult
{
	/// <summary>
	/// 上传的服务端信息。
	/// </summary>
	[JsonPropertyName("servers")]
	public required List<LocateUploadServerResult> Servers { get; set; }
}

/// <summary>
/// 上传结果。
/// </summary>
private class UploadResult
{
	/// <summary>
	/// 错误码。
	/// </summary>
	[JsonPropertyName("errno")]
	public int? Errno { get; set; } = null;
	/// <summary>
	/// 分片的 MD5。
	/// </summary>
	[JsonPropertyName("md5")]
	public required string MD5 { get; set; }
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
