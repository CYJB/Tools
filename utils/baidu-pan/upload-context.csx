#load "../md5.csx"

#nullable enable

using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 上传上下文。
/// </summary>
class UploadContext
{
	private const string FileAPI = "https://pan.baidu.com/rest/2.0/xpan/file";
	private const string PcsFileAPI = "https://d.pcs.baidu.com/rest/2.0/pcs/file";
	private const string SuperFile2Path = "/rest/2.0/pcs/superfile2";

	private readonly string accessToken;
	/// <summary>
	/// 上传路径。
	/// </summary>
	private readonly string uploadPath;
	/// <summary>
	/// 文件路径。
	/// </summary>
	private readonly string filePath;
	/// <summary>
	/// 文件大小。
	/// </summary>
	private readonly long fileSize;
	/// <summary>
	/// 分片大小。
	/// </summary>
	private readonly int blockSize;
	/// <summary>
	/// 分片缓存。
	/// </summary>
	private readonly byte[] buffer;
	private readonly HttpClient httpClient = new()
	{
		// 将超时设置为 5 分钟。
		Timeout = TimeSpan.FromMinutes(5)
	};
	/// <summary>
	/// 文件的 MD5。
	/// </summary>
	private string fileMD5 = "";
	/// <summary>
	/// 文件前 256KB 的 MD5。
	/// </summary>
	private string sliceMD5 = "";
	/// <summary>
	/// 文件的分片列表。
	/// </summary>
	private FileBlock[] blocks = Array.Empty<FileBlock>();
	/// <summary>
	/// 上传 ID。
	/// </summary>
	private string uploadId = "";
	/// <summary>
	/// 上传服务器地址。
	/// </summary>
	private string server = "";

	public UploadContext(string accessToken, string uploadPath, string filePath)
	{
		this.accessToken = accessToken;
		this.uploadPath = uploadPath;
		this.filePath = filePath;
		fileSize = new FileInfo(filePath).Length;
		if (fileSize < 4L * 1024 * 1024 * 1024)
		{
			// 文件大小 < 4GB，使用默认的 4M 分片。
			blockSize = 4 * 1024 * 1024;
		}
		else if (fileSize < 10L * 1024 * 1024 * 1024)
		{
			// 文件大小 < 10GB，需要普通会员，使用 16M 分片。
			blockSize = 16 * 1024 * 1024;
		}
		else
		{
			// 更大的文件需要超级会员，使用 32M 分片。
			blockSize = 32 * 1024 * 1024;
		}
		buffer = new byte[blockSize];
	}

	/// <summary>
	/// 上传当前文件。
	/// </summary>
	/// <returns></returns>
	public async Task Upload(Action<float>? progressCallback)
	{
		await GetFileBlocks();
		var blockList = JsonSerializer.Serialize(blocks.Select(block => block.MD5))!;
		// 1. 预上传
		var preCreateResult = await Precreate(blockList);
		uploadId = preCreateResult.UploadId;
		// 2. 获取上传域名。
		server = await GetServer();
		// 3. 分片上传
		using (FileStream fs = new(filePath, FileMode.Open, FileAccess.Read))
		{
			float uploadedSize = 0;
			foreach (var blockId in preCreateResult.BlockList)
			{
				uploadedSize += await UploadBlock(blockId, fs, blocks[blockId]);
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
	/// 返回文件的分片信息。
	/// </summary>
	private async Task GetFileBlocks()
	{
		int blockCount = (int)Math.Ceiling((double)fileSize / blockSize);
		var fileMD5Hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
		blocks = new FileBlock[blockCount];
		// 计算文件分片。
		using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read);
		long offset = 0;
		for (int i = 0; i < blockCount; i++, offset += blockSize)
		{
			int size = await fs.ReadAsync(buffer);
			fileMD5Hash.AppendData(buffer, 0, size);
			string md5 = CalculateMD5(buffer.AsSpan(0, size));
			blocks[i] = new FileBlock(offset, size, md5);
			if (sliceMD5.Length == 0)
			{
				sliceMD5 = CalculateMD5(buffer.AsSpan(0, Math.Min(size, 256 * 1024)));
			}
		}
		fileMD5 = GetMD5Hex(fileMD5Hash.GetCurrentHash());
	}

	/// <summary>
	/// 预上传。
	/// </summary>
	private async Task<PreCreateResult> Precreate(string blockList)
	{
		var fileInfo = new FileInfo(filePath);
		var requestUrl = FileAPI + BuildUriQuery(new() {
			{ "method", "precreate" },
			{ "access_token", accessToken },
		});
		var requestBody = new FormUrlEncodedContent([
			new("path", uploadPath),
			new("size", fileSize.ToString()),
			new("isdir", "0"),
			new("block_list", blockList),
			new("content-md5", fileMD5),
			new("slice-md5", sliceMD5),
			new("autoinit", "1"),
			new("rtype", "1"),
			new("local_ctime", new DateTimeOffset(fileInfo.CreationTime).ToUnixTimeMilliseconds().ToString()),
		]);
		PreCreateResult result = await PostJsonAsync<PreCreateResult>(httpClient, requestUrl, requestBody);
		if (result.Errno != 0)
		{
			throw GetErrnoException(result.Errno, uploadPath);
		}
		return result;
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
		UploadResult result;
		try
		{
			result = await PostJsonAsync<UploadResult>(httpClient, url, content, 3);
		}
		catch (Exception ex)
		{
			throw new Exception($"上传 {uploadPath}[{partSeq}] 异常：{ex.Message}");
		}
		if (result.Errno != null)
		{
			throw GetErrnoException(result.Errno.Value, $"{uploadPath}[{partSeq}]");
		}
		else if (result.MD5 != block.MD5)
		{
			throw new Exception($"上传 {uploadPath}[{partSeq}] 异常：MD5 {result.MD5} 与预期 {block.MD5} 不符");
		}
		return size;
	}

	/// <summary>
	/// 文件的分片。
	/// </summary>
	private record class FileBlock(long Offset, long Size, string MD5) { }

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
		/// <summary>
		/// 需要上传的分片序号列表。
		/// </summary>
		[JsonPropertyName("block_list")]
		public required int[] BlockList { get; set; }
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
}

