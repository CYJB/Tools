#load "../json/bool-converter.csx"
#nullable enable

using System.Text.Json.Serialization;

/// <summary>
/// 百度网盘列表顺序。
/// </summary>
public enum BaiduPanListOrder { Name, Time, Size }

/// <summary>
/// 百度网盘列表选项。
/// </summary>
public class BaiduPanListOptions
{
	/// <summary>
	/// 排序字段。
	/// </summary>
	public BaiduPanListOrder? Order { get; set; } = null;
	/// <summary>
	/// 是否降序排列。
	/// </summary>
	public bool? Desc { get; set; } = null;
	/// <summary>
	/// 是否返回缩略图数据。
	/// </summary>
	public bool? Thumbs = null;
	/// <summary>
	/// 是否只返回文件夹。
	/// </summary>
	public bool? IsFolderOnly = null;
}

public enum BaiduPanFileType
{
	/// <summary>
	/// 视频。
	/// </summary>
	Video = 1,
	/// <summary>
	/// 音频。
	/// </summary>
	Autio = 2,
	/// <summary>
	/// 图片。
	/// </summary>
	Image = 3,
	/// <summary>
	/// 文档。
	/// </summary>
	Document = 4,
	/// <summary>
	/// 应用。
	/// </summary>
	Application = 5,
	/// <summary>
	/// 其它。
	/// </summary>
	Other = 6,
	/// <summary>
	/// 种子。
	/// </summary>
	Torrent = 7,
}

/// <summary>
/// 百度网盘文件信息。
/// </summary>
public class BaiduPanFileInfo
{
	/// <summary>
	/// 文件名。
	/// </summary>
	[JsonPropertyName("server_filename")]
	public required string Name { get; init; }
	/// <summary>
	/// 文件的绝对路径。
	/// </summary>
	[JsonPropertyName("path")]
	public required string Path { get; init; }
	/// <summary>
	/// 文件大小，单位 B。
	/// </summary>
	[JsonPropertyName("size")]
	public required long Size { get; init; }
	/// <summary>
	/// 文件在服务器修改时间，单位：秒。
	/// </summary>
	[JsonPropertyName("server_mtime")]
	public required long ServerModifyTime { get; init; }
	/// <summary>
	/// 文件在服务器创建时间，单位：秒。
	/// </summary>
	[JsonPropertyName("server_ctime")]
	public required long ServerCreatetime { get; init; }
	/// <summary>
	/// 文件在客户端修改时间，单位：秒。
	/// </summary>
	[JsonPropertyName("local_mtime")]
	public required long LocalModifytime { get; init; }
	/// <summary>
	/// 文件在客户端创建时间，单位：秒。
	/// </summary>
	[JsonPropertyName("local_ctime")]
	public required long LocalCreateTime { get; init; }
	/// <summary>
	/// 是否为目录。
	/// </summary>
	[JsonPropertyName("isdir")]
	[JsonConverter(typeof(BooleanJsonConverter))]
	public bool IsDir { get; init; }
	/// <summary>
	/// 文件类型。
	/// </summary>
	[JsonPropertyName("category")]
	public BaiduPanFileType Category { get; init; }
	/// <summary>
	/// 云端哈希（非文件真实 MD5），仅限文件类型。
	/// </summary>
	[JsonPropertyName("md5")]
	public string? ServerHash { get; init; } = null;
	/// <summary>
	/// 该目录是否存在子目录，只有请求参数web=1且该条目为目录时，该字段才存在， 0为存在， 1为不存在
	/// </summary>
	[JsonPropertyName("dir_empty")]
	[JsonConverter(typeof(BooleanJsonConverter))]
	public bool? IsDirEmpty { get; set; } = null;
	/// <summary>
	/// 只有设置 <see cref="BaiduPanListOptions.Thumbs"/> 参数且该条目分类为图片时，
	/// 该字段才存在，包含三个尺寸的缩略图 URL；
	/// </summary>
	[JsonPropertyName("thumbs")]
	public Dictionary<string, string>? Thumbs { get; set; } = null;
}
