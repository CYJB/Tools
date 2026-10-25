#!/usr/bin/env -S dotnet --
#nullable enable

using System.Net.Http;
using System.Text;
using System.Text.Json;

/// <summary>
/// HTTP 请求相关的工具。
/// </summary>
class HttpUtils
{
	/// <summary>
	/// 拼接 URI 的参数。
	/// </summary>
	public static string BuildUriQuery(Dictionary<string, string> queries, bool addSeperator = true)
	{
		if (queries == null || queries.Count == 0)
		{
			return "";
		}
		StringBuilder builder = new();
		if (addSeperator)
		{
			builder.Append('?');
		}
		bool isFirst = true;
		foreach (var query in queries)
		{
			if (isFirst)
			{
				isFirst = false;
			}
			else
			{
				builder.Append('&');
			}
			builder.Append(query.Key);
			builder.Append('=');
			builder.Append(Uri.EscapeDataString(query.Value));
		}
		return builder.ToString();
	}

	/// <summary>
	/// 发送指定的 Get 请求，并将返回结果解析为 JSON。
	/// </summary>
	public static async Task<T> GetJsonAsync<T>(HttpClient httpClient, string url, int retryCount = 1)
	{
		string text = "";
		for (; retryCount >= 0; retryCount--)
		{
			try
			{
				text = await httpClient.GetStringAsync(url);
				return JsonSerializer.Deserialize<T>(text)!;
			}
			catch (HttpRequestException)
			{
				if (retryCount <= 0)
				{
					throw;
				}
			}
			catch (JsonException ex)
			{
				if (retryCount <= 0)
				{
					throw new Exception($"请求失败，返回结果 \"{text}\" 反序列化异常：{ex.Message}");
				}
			}
		}
		throw new Exception("Not reachable");
	}

	/// <summary>
	/// 发送指定的 Post 请求，并将返回结果解析为 JSON。
	/// </summary>
	public static async Task<T> PostJsonAsync<T>(HttpClient httpClient, string url, HttpContent content, int retryCount = 1)
	{
		string text = "";
		for (; retryCount >= 0; retryCount--)
		{
			try
			{
				var response = await httpClient.PostAsync(url, content);
				var stream = response.Content.ReadAsStream();
				using (var reader = new StreamReader(stream))
				{
					text = await reader.ReadToEndAsync();
				}
				return JsonSerializer.Deserialize<T>(text)!;
			}
			catch (HttpRequestException)
			{
				if (retryCount <= 0)
				{
					throw;
				}
			}
			catch (JsonException ex)
			{
				if (retryCount <= 0)
				{
					throw new Exception($"请求失败，返回结果 \"{text}\" 反序列化异常：{ex.Message}");
				}
			}
		}
		throw new Exception("Not reachable");
	}
}
