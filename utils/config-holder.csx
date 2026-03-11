#nullable enable

using System.Runtime.CompilerServices;
using System.Text.Json;

/// <summary>
/// 提供配置读写能力。
/// </summary>
public class ConfigHolder<T>
{
	/// <summary>
	/// 配置的序列化配置。
	/// </summary>
	private static readonly JsonSerializerOptions Options = new()
	{
		PropertyNameCaseInsensitive = true,
		WriteIndented = true,
	};

	/// <summary>
	/// 配置目录。
	/// </summary>
	private readonly string path;
	/// <summary>
	/// 配置内容。
	/// </summary>
	private T? config;

	public ConfigHolder([CallerFilePath] string? path = null)
	{
		this.path = path!.Replace(".csx", ".config.json");
		if (File.Exists(this.path))
		{
			string json = File.ReadAllText(this.path);
			config = JsonSerializer.Deserialize<T>(json, Options);
		}
	}

	/// <summary>
	/// 获取配置内容。
	/// </summary>
	public T? Config { get { return config; } }

	/// <summary>
	/// 写入配置内容。
	/// </summary>
	public void Write(T config)
	{
		this.config = config;
		File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
	}
}
