#r "nuget: Cyjb.Markdown, 1.0.3"
#r "nuget: MimeMapping, 4.0.0"
#load "../file.csx"
#load "../image.csx"
#load "./metadata.csx"
#load "./navigation.csx"
#load "./renderer.csx"

#nullable enable

using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Cyjb.Markdown;

/// <summary>
/// ePub 电子书的导出器。
/// </summary>
public class EPubExporter : IEPubExporter
{
	/// <summary>
	/// 元数据的文件路径。
	/// </summary>
	private const string MetadataPath = "metadata.opf";
	/// <summary>
	/// 导航的文件路径。
	/// </summary>
	private const string NavigationPath = "toc.ncx";
	/// <summary>
	/// 主样式的文件路径。
	/// </summary>
	private const string MainStylePath = "css/main.css";

	/// <summary>
	/// 当前文档的唯一标识。
	/// </summary>
	private readonly string uuid = Guid.NewGuid().ToString();
	/// <summary>
	/// 电子书的标题。
	/// </summary>
	private readonly string title;
	/// <summary>
	/// 电子书的作者。
	/// </summary>
	private readonly string author;
	/// <summary>
	/// 文件流。
	/// </summary>
	private readonly FileStream fileStream;
	/// <summary>
	/// ZIP 文档。
	/// </summary>
	private readonly ZipArchive archive;
	/// <summary>
	/// 元数据。
	/// </summary>
	private readonly EPubMetadata metadata;
	/// <summary>
	/// 导航。
	/// </summary>
	private readonly EPubNavigation navigation;
	/// <summary>
	/// 渲染器。
	/// </summary>
	private readonly EPubRenderer renderer;
	/// <summary>
	/// XML 序列化设置。
	/// </summary>
	private readonly XmlWriterSettings xmlWriterSettings = new()
	{
		CloseOutput = true,
		Indent = true,
		IndentChars = "  ",
		NewLineChars = "\n",
		Encoding = Encoding.UTF8,
		NamespaceHandling = NamespaceHandling.OmitDuplicates,
	};

	public EPubExporter(string filePath, string title, string author)
	{
		this.title = title;
		this.author = author;
		fileStream = new FileStream(filePath, FileMode.Create);
		archive = new ZipArchive(fileStream, ZipArchiveMode.Create);
		metadata = new EPubMetadata(uuid, title, author);
		navigation = new EPubNavigation(uuid, title, author);
		renderer = new EPubRenderer(this);

		// 写入基础文件。
		using (StreamWriter writer = new(archive.CreateEntry("mimetype").Open()))
		{
			writer.WriteLine("application/epub+zip");
		}
		using (StreamWriter writer = new(archive.CreateEntry("META-INF/container.xml").Open()))
		{
			writer.Write(File.ReadAllText(Path.Combine(GetScriptFolder(), "container.xml")));
		}
		using (StreamWriter writer = new(archive.CreateEntry(MainStylePath).Open()))
		{
			writer.Write(File.ReadAllText(Path.Combine(GetScriptFolder(), "main.css")));
		}
		metadata.AddManifest("css", MainStylePath, "text/css");
		metadata.AddManifest("ncx", NavigationPath, "application/x-dtbncx+xml");
	}

	/// <summary>
	/// 添加指定的页面。
	/// </summary>
	public void AddPage(string id, string? title, Document document)
	{
		// 需要先渲染再写入。
		renderer.Render(document, title);
		var pageId = $"page_{id}";
		var pagePath = pageId + ".xhtml";
		{
			using Stream stream = archive.CreateEntry(pagePath).Open();
			using XmlWriter writer = XmlWriter.Create(stream, xmlWriterSettings);
			renderer.WriteTo(writer);
		}
		AddPageMeta(pageId, pagePath, title);
	}

	// /// <summary>
	// /// 添加指定的封面章节。
	// /// </summary>
	// /// <param name="chapter">要添加的封面章节。</param>
	// public void AddCoverChapter(Chapter chapter)
	// {
	// 	// 从当前封面复制封面图片。
	// 	Document coverDoc = (Document)chapter.Document.Clone();
	// 	// 添加标题和作者。
	// 	coverDoc.Children.Add(new Heading(1)
	// 	{
	// 		Children = {
	// 			new Literal(title),
	// 		}
	// 	});
	// 	coverDoc.Children.Add(new Paragraph()
	// 	{
	// 		Children = {
	// 			new Literal(author),
	// 		}
	// 	});

	// 	var pageId = "cover";
	// 	var pagePath = pageId + ".xhtml";
	// 	renderer.Render(pagePath, title, true, coverDoc);
	// 	AddPageMeta(pageId, pagePath, chapter);
	// }

	/// <summary>
	/// 添加指定页面的元数据。
	/// </summary>
	/// <param name="id">页面的 id。</param>
	/// <param name="pageName">页面的名称。</param>
	/// <param name="title">页面的标题。</param>
	private void AddPageMeta(string id, string pageName, string? title = null)
	{
		metadata.AddManifest(id, pageName, "application/xhtml+xml", true);
		if (title != null)
		{
			navigation.AddNav(pageName, title);
		}
	}

	/// <summary>
	/// 添加一幅图片。
	/// </summary>
	/// <param name="filePath">图片的文件路径。</param>
	/// <param name="id">图片的标识符。</param>
	/// <param name="title">图片的标题。</param>
	/// <param name="isCover">图片是否是封面。</param>
	/// <returns>图片的路径。</returns>
	public string AddImage(string filePath, string? id, string? title = null, bool isCover = false)
	{
		string imgId = $"image_{id}";
		string extension = Path.GetExtension(filePath).ToLowerInvariant();
		string imgPath = $"images/{id}{extension}";
		if (isCover)
		{
			metadata.AddCover(imgId);
		}
		// 写入元数据。
		metadata.AddManifest(imgId, imgPath, MimeMapping.MimeUtility.GetMimeMapping(filePath));
		// 将图片写入 ZIP 文档。
		using Stream outputStream = archive.CreateEntry(imgPath).Open();
		using FileStream inputStream = new(filePath, FileMode.Open, FileAccess.Read);
		inputStream.CopyTo(outputStream);
		return imgPath;
	}

	/// <summary>
	/// 完成章节添加。
	/// </summary>
	public void Finish()
	{
		using (var writer = XmlWriter.Create(archive.CreateEntry(MetadataPath).Open(), xmlWriterSettings))
		{
			metadata.WriteTo(writer);
		}
		using (var writer = XmlWriter.Create(archive.CreateEntry(NavigationPath).Open(), xmlWriterSettings))
		{
			navigation.WriteTo(writer);
		}
		archive.Dispose();
		fileStream.Dispose();
	}
}
