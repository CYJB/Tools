using System.Text.Json;
using System.Text.Json.Serialization;

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
					string stringValue = reader.GetString();
					if (stringValue.Length == 0)
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
