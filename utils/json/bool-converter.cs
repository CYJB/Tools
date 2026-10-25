#!/usr/bin/env -S dotnet --
#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 把各种形式的值（null、字符串、数字）都当作 bool 读取的转换器。
/// </summary>
public class BooleanJsonConverter : JsonConverter<bool>
{
	public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		switch (reader.TokenType)
		{
			case JsonTokenType.Null:
				return false;
			case JsonTokenType.True:
			case JsonTokenType.False:
				return reader.GetBoolean();
			case JsonTokenType.Number:
				return reader.GetInt32() != 0;
			case JsonTokenType.String:
				{
					string? stringValue = reader.GetString();
					if (string.IsNullOrEmpty(stringValue))
					{
						return false;
					}
					else if (bool.TryParse(stringValue, out bool boolResult))
					{
						return boolResult;
					}
					else if (int.TryParse(stringValue, out int intResult))
					{
						return intResult != 0;
					}
					return true;
				}
			default:
				return false;
		}
	}

	public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
	{
		writer.WriteBooleanValue(value);
	}
}
