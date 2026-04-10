#load "config-holder.csx"
#r "nuget: Spectre.Console, 0.54.0"

#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Spectre.Console;

static class CalibreBookFields
{
	/// <summary>
	/// 书籍的 ID。
	/// </summary>
	public static string Id = "id";
	/// <summary>
	/// 书籍的作者。
	/// </summary>
	public static string Authors = "authors";
	/// <summary>
	/// 书籍的作者排序。
	/// </summary>
	public static string AuthorSort = "author_sort";
	/// <summary>
	/// 书籍的备注。
	/// </summary>
	public static string Comments = "comments";
	/// <summary>
	/// 书籍是否包含封面。
	/// </summary>
	public static string Cover = "cover";
	/// <summary>
	/// 书籍包含的格式。
	/// </summary>
	public static string Formats = "formats";
	/// <summary>
	/// 书籍的标识符。
	/// </summary>
	public static string Identifiers = "identifiers";
	/// <summary>
	/// 书籍的 ISBN。
	/// </summary>
	public static string Isbn = "isbn";
	/// <summary>
	/// 书籍的语言。
	/// </summary>
	public static string Languages = "languages";
	/// <summary>
	/// 书籍的修改时间。
	/// </summary>
	public static string LastModified = "last_modified";
	/// <summary>
	/// 书籍的出版时间。
	/// </summary>
	public static string Pubdate = "pubdate";
	/// <summary>
	/// 书籍的出版社。
	/// </summary>
	public static string Publisher = "publisher";
	/// <summary>
	/// 书籍的评分。
	/// </summary>
	public static string Rating = "rating";
	/// <summary>
	/// 书籍的系列。
	/// </summary>
	public static string Series = "series";
	/// <summary>SBN
	/// 书籍的系列编号。
	/// </summary>
	public static string SeriesIndex = "series_index";
	/// <summary>
	/// 书籍的尺寸。
	/// </summary>
	public static string Size = "size";
	/// <summary>
	/// 书籍的标签。
	/// </summary>
	public static string Tags = "tags";
	/// <summary>
	/// 书籍的创建时间。
	/// </summary>
	public static string Timestamp = "timestamp";
	/// <summary>
	/// 书籍的标题。
	/// </summary>
	public static string Title = "title";
	/// <summary>
	/// 书籍的唯一 ID。
	/// </summary>
	public static string Uuid = "uuid";
}

class CalibreBookInfo
{
	/// <summary>
	/// 书籍的 ID。
	/// </summary>
	[JsonPropertyName("id")]
	public int Id { get; set; }

	/// <summary>
	/// 书籍的作者。
	/// </summary>
	[JsonPropertyName("authors")]
	public string? Authors { get; set; }

	/// <summary>
	/// 书籍的作者排序。
	/// </summary>
	[JsonPropertyName("author_sort")]
	public string? AuthorSort { get; set; }

	/// <summary>
	/// 书籍的备注。
	/// </summary>
	[JsonPropertyName("comments")]
	public string? Comments { get; set; }

	/// <summary>
	/// 书籍是否包含封面。
	/// </summary>
	[JsonPropertyName("cover")]
	public bool? Cover { get; set; }

	/// <summary>
	/// 书籍包含的格式。
	/// </summary>
	[JsonPropertyName("formats")]
	public string[]? Formats { get; set; }

	/// <summary>
	/// 书籍的标识符。
	/// </summary>
	[JsonPropertyName("identifiers")]
	public Dictionary<string, string>? Identifiers { get; set; }

	/// <summary>
	/// 书籍的 ISBN。
	/// </summary>
	[JsonPropertyName("isbn")]
	public string? Isbn { get; set; }

	/// <summary>
	/// 书籍的语言。
	/// </summary>
	[JsonPropertyName("languages")]
	public string[]? Languages { get; set; }

	/// <summary>
	/// 书籍的修改时间。
	/// </summary>
	[JsonPropertyName("last_modified")]
	public DateTime? LastModified { get; set; }

	/// <summary>
	/// 书籍的出版时间。
	/// </summary>
	[JsonPropertyName("pubdate")]
	public DateTime? Pubdate { get; set; }

	/// <summary>
	/// 书籍的出版社。
	/// </summary>
	[JsonPropertyName("publisher")]
	public string? Publisher { get; set; }

	/// <summary>
	/// 书籍的评分。
	/// </summary>
	[JsonPropertyName("rating")]
	public int? Rating { get; set; }

	/// <summary>
	/// 书籍的系列。
	/// </summary>
	[JsonPropertyName("series")]
	public string? Series { get; set; }

	/// <summary>SBN
	/// 书籍的系列编号。
	/// </summary>
	[JsonPropertyName("series_index")]
	public float? SeriesIndex { get; set; }

	/// <summary>
	/// 书籍的尺寸。
	/// </summary>
	[JsonPropertyName("size")]
	public long? Size { get; set; }

	/// <summary>
	/// 书籍的标签。
	/// </summary>
	[JsonPropertyName("tags")]
	public string[]? Tags { get; set; }

	/// <summary>
	/// 书籍的创建时间。
	/// </summary>
	[JsonPropertyName("timestamp")]
	public DateTime? Timestamp { get; set; }

	/// <summary>
	/// 书籍的标题。
	/// </summary>
	[JsonPropertyName("title")]
	public string? Title { get; set; }

	/// <summary>
	/// 书籍的唯一 ID。
	/// </summary>
	[JsonPropertyName("uuid")]
	public Guid? Uuid { get; set; }
}

class CalibreBookOptions
{
	/// <summary>
	/// 书籍的作者。
	/// </summary>
	public string? Authors { get; set; }

	/// <summary>
	/// 如果找到具有类似书名和作者的书籍，自动将输入格式(文件)合并到现有书籍记录中。
	/// </summary>
	public string? Automerge { get; set; }

	/// <summary>
	/// 书籍的封面路径。
	/// </summary>
	public string? Cover { get; set; }

	/// <summary>
	/// 即使已经存在，也添加书籍到数据库中。
	/// </summary>
	public string? Duplicates { get; set; }

	/// <summary>
	/// 书籍标识符。
	/// </summary>
	public string? Identifier { get; set; }

	/// <summary>
	/// 逗号分割的语言列表（最好使用 ISO639 语言代码，尽管也能识别某些语言名称）。
	/// </summary>
	public string? Languages { get; set; }

	/// <summary>
	/// 书籍的丛书
	/// </summary>
	public string? Series { get; set; }

	/// <summary>
	/// 书籍的丛书编号。
	/// </summary>
	public double? SeriesIndex { get; set; }

	/// <summary>
	/// 书籍的标签。
	/// </summary>
	public string? Tags { get; set; }

	/// <summary>
	/// 书籍的书名。
	/// </summary>
	public string? Title { get; set; }
}

class Calibre
{
	/// <summary>
	/// calibredb.exe 的名称。
	/// </summary>
	private const string CalibreDBFile = "calibredb.exe";
	/// <summary>
	/// 添加结果的正则表达式。
	/// </summary>
	private static readonly Regex CalibreAddResultRegex = new(@"id:\s*(\d+)");
	/// <summary>
	/// 配置。
	/// </summary>
	private static readonly ConfigHolder<Config> configHolder = new();

	/// <summary>
	/// Calibre 的 bin 目录。
	/// </summary>
	private readonly string binDir = GetCalibreBinDir();
	/// <summary>
	/// Calibre 数据库的参数。
	/// </summary>
	private readonly List<string> libraryArgs = [];

	public Calibre(string library)
	{
		if (library.StartsWith("http://") || library.StartsWith("https://"))
		{
			// 是内容服务器。
			var uri = new UriBuilder(library);
			if (uri.UserName.Length > 0)
			{
				libraryArgs.Add($"--username={uri.UserName}");
				uri.UserName = "";
			}
			if (uri.Password.Length > 0)
			{
				libraryArgs.Add($"--password={uri.Password}");
				uri.Password = "";
			}
			libraryArgs.Add($"--with-library={Uri.UnescapeDataString(uri.ToString())}");
		}
		else
		{
			// 是本地磁盘目录。
			libraryArgs.Add($"--with-library={library}");
		}
	}

	/// <summary>
	/// 列出可用书籍。
	/// </summary>
	public async Task<CalibreBookInfo[]> List(ICollection<string>? fields = null, string? search = null)
	{
		using var process = new Process();
		List<string> args = new() { "list", "--for-machine" };
		if (fields != null && fields.Count > 0)
		{
			args.Add("-f");
			args.Add(string.Join(',', fields));
		}
		if (search != null)
		{
			args.Add("-s");
			args.Add(search);
		}
		args.AddRange(libraryArgs);
		process.StartInfo = new ProcessStartInfo(Path.Join(binDir, CalibreDBFile), args)
		{
			WorkingDirectory = binDir,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		var resultBuilder = new StringBuilder();
		var errorBuilder = new StringBuilder();
		process.OutputDataReceived += (sender, e) =>
		{
			if (!string.IsNullOrEmpty(e.Data))
			{
				resultBuilder.Append(e.Data);
			}
		};
		process.ErrorDataReceived += (sender, e) =>
		{
			if (!string.IsNullOrEmpty(e.Data))
			{
				errorBuilder.Append(e.Data);
			}
		};
		process.Start();

		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		await process.WaitForExitAsync();
		if (process.ExitCode != 0)
		{
			throw new Exception($"读取失败: {process.ExitCode}, {errorBuilder}");
		}
		else if (errorBuilder.Length > 0)
		{
			throw new Exception($"读取失败: {errorBuilder}");
		}
		return JsonSerializer.Deserialize<CalibreBookInfo[]>(resultBuilder.ToString())!;
	}

	/// <summary>
	/// 将指定的文件当作书籍添加到 Calibre 数据库。
	/// </summary>
	public async Task<string> Add(ICollection<string> files, CalibreBookOptions? options = null)
	{
		using var process = new Process();
		List<string> args = new() { "add" };
		if (options != null)
		{
			if (!string.IsNullOrWhiteSpace(options.Authors))
			{
				args.Add("-a");
				args.Add(options.Authors);
			}
			if (!string.IsNullOrWhiteSpace(options.Title))
			{
				args.Add("-t");
				args.Add(options.Title);
			}
			if (options.Cover != null)
			{
				args.Add("-c");
				args.Add(options.Cover);
			}
			if (options.Automerge != null)
			{
				args.Add("-m");
				args.Add(options.Automerge);
			}
			if (options.Duplicates != null)
			{
				args.Add("-d");
				args.Add(options.Duplicates);
			}
			if (options.Identifier != null)
			{
				args.Add("-I");
				args.Add(options.Identifier);
			}
			if (options.Languages != null)
			{
				args.Add("-l");
				args.Add(options.Languages);
			}
			if (options.Series != null)
			{
				args.Add("-s");
				args.Add(options.Series);
			}
			if (options.SeriesIndex != null)
			{
				args.Add("-S");
				args.Add(options.SeriesIndex.Value.ToString());
			}
			if (options.Tags != null)
			{
				args.Add("-T");
				args.Add(options.Tags);
			}
		}
		args.AddRange(libraryArgs);
		args.AddRange(files);
		process.StartInfo = new ProcessStartInfo(Path.Join(binDir, CalibreDBFile), args)
		{
			WorkingDirectory = binDir,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		string? id = null;
		var errorBuilder = new StringBuilder();
		process.OutputDataReceived += (sender, e) =>
		{
			if (!string.IsNullOrEmpty(e.Data))
			{
				var match = CalibreAddResultRegex.Match(e.Data.Trim());
				if (match.Success)
				{
					id = match.Value;
				}
			}
		};
		process.ErrorDataReceived += (sender, e) =>
		{
			if (!string.IsNullOrEmpty(e.Data))
			{
				errorBuilder.Append(e.Data);
			}
		};
		process.Start();

		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		await process.WaitForExitAsync();
		if (process.ExitCode != 0)
		{
			throw new Exception($"添加失败: {process.ExitCode}, {errorBuilder}");
		}
		else if (errorBuilder.Length > 0)
		{
			throw new Exception($"添加失败: {errorBuilder}");
		}
		return id ?? "";
	}

	/// <summary>
	/// 返回 Calibre 的可执行文件目录。
	/// </summary>
	private static string GetCalibreBinDir()
	{
		var config = configHolder.Config;
		if (config.BinDir == null)
		{
			var binDir = AnsiConsole.Ask<string>("请输入 [green]Calibre 可执行文件目录[/]:");
			// 如果传入的是文件，找到相应的目录。
			if (File.Exists(binDir))
			{
				binDir = Path.GetDirectoryName(binDir);
			}
			if (Directory.Exists(binDir))
			{
				// 验证是否存在 calibredb.exe。
				if (!File.Exists(Path.Join(binDir, CalibreDBFile)))
				{
					throw new Exception($"无效的 Calibre 可执行文件目录 {binDir}");
				}
			}
			else
			{
				throw new Exception($"无效的 Calibre 可执行文件目录 {binDir}");
			}
			config.BinDir = binDir;
			configHolder.Save();
		}
		return config.BinDir;
	}

	private class Config
	{
		public string? BinDir { get; set; } = null;
	}
}
