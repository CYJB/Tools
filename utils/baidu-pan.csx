#load "baidu-pan/access-token.csx"
#load "baidu-pan/defines.csx"
#load "baidu-pan/error.csx"
#load "baidu-pan/file-block.csx"
#load "baidu-pan/list-query.csx"
#load "http.csx"
#r "nuget: Spectre.Console, 0.54.0"
#nullable enable

using System.Net.Http;
using System.Net.Http.Headers;
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
			new("async", "2"),
			new("filelist", JsonSerializer.Serialize(path)),
		]);
		FileManagerResult result = await PostJsonAsync<FileManagerResult>(httpClient, url, requestBody);
		if (result.Errno != 0)
		{
			throw GetErrnoException(result.Errno, path[0]);
		}
	}

	/// <summary>
	/// 上传上下文。
	/// </summary>
	private class UploadContext(string accessToken, string uploadPath, string filePath)
	{
		/// <summary>
		/// 上传分片大小。
		/// </summary>
		private const int BlockSize = 4 * 1024 * 1024;

		private readonly string accessToken = accessToken;
		private readonly string uploadPath = uploadPath;
		private readonly string filePath = filePath;
		private readonly HttpClient httpClient = new()
		{
			// 将超时设置为 5 分钟。
			Timeout = TimeSpan.FromMinutes(5)
		};
		private readonly byte[] buffer = new byte[BlockSize];
		private long fileSize;
		private string uploadId = "";
		private string server = "";

		/// <summary>
		/// 上传当前文件。
		/// </summary>
		/// <returns></returns>
		public async Task Upload(Action<float>? progressCallback)
		{
			FileBlock[] blocks = await GetFileBlocks(filePath, BlockSize);
			var blockList = JsonSerializer.Serialize(blocks.Select(block => block.MD5))!;
			// 1. 预上传
			uploadId = await Precreate(blockList);
			// 2. 获取上传域名。
			server = await GetServer();
			// 3. 分片上传
			using (FileStream fs = new(filePath, FileMode.Open, FileAccess.Read))
			{
				float uploadedSize = 0;
				for (int i = 0; i < blocks.Length; i++)
				{
					uploadedSize += await UploadBlock(i, fs, blocks[i]);
					progressCallback?.Invoke(uploadedSize * 100 / fileSize);
				}
			}

			await Task.Delay(500);

			// 4. 合并文件
			var url = FileAPI + BuildUriQuery(new() {
				{ "method", "create" },
				{ "access_token", accessToken },
			});
			var requestBody = new FormUrlEncodedContent([
				new("path", uploadPath),
				new("size", fileSize.ToString()),
				new("isdir", "0"),
				new("block_list", blockList),
				new("uploadid", uploadId),
				new("rtype", "1"),
			]);
			UploadResult result = await PostJsonAsync<UploadResult>(httpClient, url, requestBody);
			if (result.Errno != null && result.Errno.Value != 0)
			{
				throw GetErrnoException(result.Errno.Value, uploadPath);
			}
		}

		/// <summary>
		/// 预上传。
		/// </summary>
		private async Task<string> Precreate(string blockList)
		{
			var fileInfo = new FileInfo(filePath);
			fileSize = fileInfo.Length;
			var requestUrl = FileAPI + BuildUriQuery(new() {
				{ "method", "precreate" },
				{ "access_token", accessToken },
			});
			var requestBody = new FormUrlEncodedContent([
				new("path", uploadPath),
				new("size", fileSize.ToString()),
				new("isdir", "0"),
				new("block_list", blockList),
				new("autoinit", "1"),
				new("rtype", "1"),
				new("local_ctime", new DateTimeOffset(fileInfo.CreationTime).ToUnixTimeMilliseconds().ToString()),
			]);
			PreCreateResult result = await PostJsonAsync<PreCreateResult>(httpClient, requestUrl, requestBody);
			if (result.Errno != 0)
			{
				throw GetErrnoException(result.Errno, uploadPath);
			}
			return result.UploadId;
		}

		/// <summary>
		/// 获取上传域名。
		/// </summary>
		private async Task<string> GetServer()
		{
			string url = PcsFileAPI + BuildUriQuery(new() {
				{ "method", "locateupload" },
				{ "appid", "250528" },
				{ "access_token", accessToken },
				{ "path", uploadPath },
				{ "uploadid", uploadId },
				{ "upload_version", "2.0" },
			});
			LocateUploadResult result = await GetJsonAsync<LocateUploadResult>(httpClient, url);
			return result.Servers[0].Server;
		}

		/// <summary>
		/// 上传分片。
		/// </summary>
		private async Task<int> UploadBlock(int partSeq, FileStream fs, FileBlock block)
		{
			var url = server + SuperFile2Path + BuildUriQuery(new() {
				{ "method", "upload" },
				{ "access_token", accessToken },
				{ "type", "tmpfile" },
				{ "path", uploadPath },
				{ "uploadid", uploadId },
				{ "partseq", partSeq.ToString() },
			});
			var content = new MultipartFormDataContent();
			fs.Seek(block.Offset, SeekOrigin.Begin);
			var size = await fs.ReadAsync(buffer);
			var fileContent = new ByteArrayContent(buffer, 0, size);
			fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
			content.Add(fileContent, "file", "tmpfile");
			UploadResult result = await PostJsonAsync<UploadResult>(httpClient, url, content);
			if (result.Errno != null)
			{
				throw GetErrnoException(result.Errno.Value, $"{uploadPath}[{partSeq}");
			}
			else if (result.MD5 != block.MD5)
			{
				throw new Exception($"上传 {uploadPath}[{partSeq}] 异常：MD5 {result.MD5} 与预期 {block.MD5} 不符");
			}
			return size;
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
