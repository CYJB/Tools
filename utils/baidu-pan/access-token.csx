#load "../config-holder.csx"
#load "../http.csx"
#r "nuget: Spectre.Console, 0.54.0"
#nullable enable

using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

/// <summary>
/// DeviceCode 请求路径。
/// </summary>
private const string DeviceCodeAPI = "https://openapi.baidu.com/oauth/2.0/device/code";
/// <summary>
/// AccessToken 请求路径。
/// </summary>
private const string TokenAPI = "https://openapi.baidu.com/oauth/2.0/token";

private static readonly HttpClient httpClient = new();
/// <summary>
/// 百度网盘配置路径。
/// </summary>
private static readonly ConfigHolder<Config> configHolder = new();

/// <summary>
/// 获取百度网盘的 AccessToken
/// </summary>
static async Task<string> GetAccessTokenAsync()
{
	// 配置不存在，要求用户输入。
	var config = configHolder.Config;
	if (config.AppKey == null || config.SecretKey == null)
	{
		var appKey = AnsiConsole.Ask<string>("请输入百度网盘的 [green]AppKey[/]:");
		var secretKey = AnsiConsole.Ask<string>("请输入百度网盘的 [green]SecretKey[/]:");
		config.AppKey = appKey;
		config.SecretKey = secretKey;
		configHolder.Save();
	}
	if (config.AccessToken == null || config.RefreshToken == null)
	{
		// 没有 AccessToken，请求用户授权。
		// 或者如果没有 RefreshToken，也需要请求授权。
		await RequestAccessTokenAsync(config);
		// 保存配置。
		configHolder.Save();
	}
	else if (config.AccessTokenExpires <= DateTimeOffset.Now.ToUnixTimeMilliseconds())
	{
		// AccessToken 已过期，使用 RefreshToken 刷新。
		await RefreshAccessTokenAsync(config);
		// 保存配置。
		configHolder.Save();
	}
	return config.AccessToken!;
}

/// <summary>
/// 请求 AccessToken。
/// </summary>
static async Task RequestAccessTokenAsync(Config config)
{
	// 请求设备码。
	var json = await httpClient.GetStringAsync(DeviceCodeAPI + BuildUriQuery(new(){
		{ "response_type", "device_code" },
		{ "client_id", config.AppKey },
		{ "scope", "basic,netdisk" },
	}));
	var deviceResult = JsonSerializer.Deserialize<DeviceCodeResult>(json)!;
	AnsiConsole.WriteLine($"百度网盘需要用户授权：");
	AnsiConsole.MarkupLine($"    请在 [green]{deviceResult.ExpiresIn}[/] 秒内访问 [link]{deviceResult.VerificationUrl}[/] 并输入用户码，完成授权");
	AnsiConsole.MarkupLine($"    用户码 [green]{deviceResult.UserCode}[/]");

	// 轮询 AccessToken，等待用户授权。
	await AnsiConsole.Status().StartAsync("等待用户授权...", async ctx =>
	{
		int interval = deviceResult.Interval;
		while (true)
		{
			// 使用指定轮询间隔。
			await Task.Delay(interval * 1000);
			var response = await httpClient.GetAsync(TokenAPI + BuildUriQuery(new(){
				{"grant_type", "device_token" },
				{"code", deviceResult.DeviceCode },
				{"client_id", config.AppKey },
				{"client_secret", config.SecretKey },
			}));
			json = await response.Content.ReadAsStringAsync();
			var tokenResult = JsonSerializer.Deserialize<AccessTokenResult>(json)!;
			if (response.IsSuccessStatusCode)
			{
				// 已授权，填充相关信息。
				config.CopyFrom(tokenResult);
				AnsiConsole.MarkupLine($"[green]授权成功！[/]");
				return;
			}
			else
			{
				// 处理非成功状态码（如授权未完成）
				var error = tokenResult.Error;
				if (error == "authorization_pending")
				{
					// 用户尚未授权，继续轮询。
					continue;
				}
				else if (error == "slow_down")
				{
					// 请求过快，增加间隔
					interval += 1;
					continue;
				}
				else
				{
					var errorMsg = $"授权失败: {error}";
					AnsiConsole.MarkupLine($"[red]{errorMsg}[/]");
					throw new Exception(errorMsg);
				}
			}
		}
	});
}
/// <summary>
/// 刷新 AccessToken。
/// </summary>
static async Task RefreshAccessTokenAsync(Config config)
{
	var response = await httpClient.GetAsync(TokenAPI + BuildUriQuery(new(){
		{"grant_type", "refresh_token" },
		{"refresh_token", config.RefreshToken },
		{"client_id", config.AppKey },
		{"client_secret", config.SecretKey },
	}));
	var json = await response.Content.ReadAsStringAsync();
	var tokenResult = JsonSerializer.Deserialize<AccessTokenResult>(json)!;
	if (response.IsSuccessStatusCode)
	{
		// 已授权，填充相关信息。
		config.CopyFrom(tokenResult);
	}
	else
	{
		// 处理非成功状态码（如授权未完成）
		var error = tokenResult.Error;
		if (error == "expired_token")
		{
			// 刷新 token 过期，重新请求授权。
			await RequestAccessTokenAsync(config);
			// 保存配置。
			configHolder.Save();
		}
		else
		{
			var errorMsg = $"百度网盘授权刷新失败: {error}";
			AnsiConsole.MarkupLine($"[red]{errorMsg}[/]");
			throw new Exception(errorMsg);
		}
	}
}

private class Config
{
	public string? AppKey { get; set; } = null;
	public string? SecretKey { get; set; } = null;
	/// <summary>
	/// 访问码，是调用网盘开放API访问用户授权资源的凭证。
	/// </summary>
	public string? AccessToken { get; set; } = null;
	/// <summary>
	/// 访问码的过期时间。
	/// </summary>
	public long AccessTokenExpires { get; set; } = 0;
	/// <summary>
	/// 用于刷新 Access Token 的更新码，有效期为 10 年。
	/// </summary>
	public string? RefreshToken { get; set; } = null;

	/// <summary>
	/// 从指定 Access Token 结果复制。
	/// </summary>
	public void CopyFrom(AccessTokenResult token)
	{
		AccessToken = token.AccessToken;
		AccessTokenExpires = DateTimeOffset.Now.ToUnixTimeMilliseconds() +
			token.ExpiresIn * 1000;
		RefreshToken = token.RefreshToken;
	}
}

private class DeviceCodeResult
{
	/// <summary>
	/// 设备码，可用于生成单次凭证 Access Token。
	/// </summary>
	[JsonPropertyName("device_code")]
	public required string DeviceCode { get; set; }
	/// <summary>
	/// 用户码。
	/// </summary>
	[JsonPropertyName("user_code")]
	public required string UserCode { get; set; }
	/// <summary>
	/// 用户输入 user code 进行授权的 url。
	/// </summary>
	[JsonPropertyName("verification_url")]
	public required string VerificationUrl { get; set; }
	/// <summary>
	/// DeviceCode 的过期时间，单位：秒。
	/// </summary>
	[JsonPropertyName("expires_in")]
	public long ExpiresIn { get; set; }
	/// <summary>
	/// device_code 换 Access Token 轮询间隔时间，单位：秒。
	/// </summary>
	[JsonPropertyName("interval")]
	public int Interval { get; set; }
}

private class AccessTokenResult
{
	/// <summary>
	/// 调用网盘开放 API 访问用户授权资源的凭证。
	/// </summary>
	[JsonPropertyName("access_token")]
	public string? AccessToken { get; set; }
	/// <summary>
	/// 用于刷新 Access Token, 有效期为 10 年。
	/// </summary>
	[JsonPropertyName("refresh_token")]
	public string? RefreshToken { get; set; }
	/// <summary>
	/// Access Token 的有效期，单位：秒。
	/// </summary>
	[JsonPropertyName("expires_in")]
	public long ExpiresIn { get; set; }
	/// <summary>
	/// 授权时的错误信息。
	/// </summary>
	[JsonPropertyName("error")]
	public string? Error { get; set; }
}
