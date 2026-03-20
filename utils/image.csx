// https://github.com/dlemstra/Magick.NET
#r "nuget: Magick.NET.Core, 14.11.0"
#r "nuget: Magick.NET-Q8-x64, 14.11.0"

#nullable enable

using ImageMagick;

/// <summary>
/// 图片的扩展名。
/// </summary>
public static readonly HashSet<string> ImageExt = [
	".jpg", ".jpeg", ".png", ".bmp", ".heic", ".webp", ".gif",
];

/// <summary>
/// 图片的扩展名到 MagickFormat 的映射。
/// </summary>
private static readonly Dictionary<string, MagickFormat> ImageFormat = new()
{
	{ ".jpg", MagickFormat.Jpeg },
	{ ".jpeg", MagickFormat.Jpeg },
	{ ".png", MagickFormat.Png },
	{ ".bmp", MagickFormat.Bmp },
	{ ".heic", MagickFormat.Heic },
	{ ".webp", MagickFormat.WebP },
	{ ".gif", MagickFormat.Gif },
};

/// <summary>
/// 从图片的扩展名返回 MagickFormat。
/// </summary>
static MagickFormat GetMagickFormat(string fileName)
{
	if (ImageFormat.TryGetValue(Path.GetExtension(fileName).ToLowerInvariant(), out var format))
	{
		return format;
	}
	return MagickFormat.Unknown;
}
