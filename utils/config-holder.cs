#!/usr/bin/env -S dotnet --
#nullable enable

using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

/// <summary>
/// 提供配置读写能力。
/// </summary>
public class ConfigHolder<T>
	where T : new()
{
	/// <summary>
	/// 配置的序列化配置。
	/// </summary>
	private static readonly JsonSerializerOptions Options = new()
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
		Encoder = JavaScriptEncoder.Create(UnicodeRanges.All, UnicodeRanges.Cyrillic),
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
	private readonly T config;

	public ConfigHolder([CallerFilePath] string? path = null)
	{
		this.path = GetConfigPath(path);
		if (File.Exists(this.path))
		{
			string json = File.ReadAllText(this.path);
			config = JsonSerializer.Deserialize<T>(json, Options)!;
		}
		else
		{
			config = new T();
		}
	}

	/// <summary>
	/// 返回配置文件路径：传进来的是脚本路径时换成同名的 .config.json，传进来的是配置文件路径时直接使用。
	/// </summary>
	private static string GetConfigPath(string? path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return "";
		}
		return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path : Path.ChangeExtension(path, ".config.json")!;
	}

	/// <summary>
	/// 获取配置内容。
	/// </summary>
	public T Config { get { return config; } }

	/// <summary>
	/// 保存配置内容。
	/// </summary>
	public void Save()
	{
		File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
	}
}
