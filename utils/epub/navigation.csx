#load "./xml.csx"

#nullable enable

using System.Xml;

/// <summary>
/// ePub 的导航。
/// </summary>
class EPubNavigation
{
	/// <summary>
	/// 导航 XML 文档。
	/// </summary>
	private readonly XmlDocument document = new();
	/// <summary>
	/// 导航 XML 节点。
	/// </summary>
	private readonly XmlElement navMapNode;
	/// <summary>
	/// 导航的索引。
	/// </summary>
	private int navIndex = 1;
	/// <summary>
	/// 当前章节标题。
	/// </summary>
	private readonly List<string> currentChapter = new();
	/// <summary>
	/// 当前章节的导航 XML 节点。
	/// </summary>
	private List<XmlElement> currentChapterNode = new();

	public EPubNavigation(string uuid, string title, string author)
	{
		EPubXml.AddXmlDeclaration(document);

		var ncx = EPubXml.CreateNCXElement(document, "ncx");
		ncx.SetAttribute("version", "2005-1");
		ncx.SetAttribute("xml:lang", "zh");
		document.AppendChild(ncx);

		var head = EPubXml.CreateNCXElement(document, "head");
		ncx.AppendChild(head);

		var meta = EPubXml.CreateNCXElement(document, "meta");
		head.AppendChild(meta);
		meta.SetAttribute("name", "dtb:uid");
		meta.SetAttribute("content", uuid);

		meta = EPubXml.CreateNCXElement(document, "meta");
		head.AppendChild(meta);
		meta.SetAttribute("name", "dtb:depth");
		meta.SetAttribute("content", "1");

		meta = EPubXml.CreateNCXElement(document, "meta");
		head.AppendChild(meta);
		meta.SetAttribute("name", "dtb:totalPageCount");
		meta.SetAttribute("content", "0");

		meta = EPubXml.CreateNCXElement(document, "meta");
		head.AppendChild(meta);
		meta.SetAttribute("name", "dtb:maxPageNumber");
		meta.SetAttribute("content", "0");

		var docTitle = EPubXml.CreateNCXElement(document, "docTitle");
		ncx.AppendChild(docTitle);
		var text = EPubXml.CreateNCXElement(document, "text");
		docTitle.AppendChild(text);
		text.InnerText = title;

		var docAuthor = EPubXml.CreateNCXElement(document, "docAuthor");
		ncx.AppendChild(docAuthor);
		text = EPubXml.CreateNCXElement(document, "text");
		docAuthor.AppendChild(text);
		text.InnerText = author;

		navMapNode = EPubXml.CreateNCXElement(document, "navMap");
		ncx.AppendChild(navMapNode);
	}

	/// <summary>
	/// 添加新的导航。
	/// </summary>
	public void AddNav(string pageName, string title, string[]? chapter = null)
	{
		XmlElement navNode = navMapNode;
		if (chapter == null || chapter.Length == 0)
		{
			currentChapter.Clear();
			currentChapterNode.Clear();
		}
		else
		{
			for (int i = 0; i < chapter.Length; i++)
			{
				var chapterTitle = chapter[i];
				if (currentChapter.Count > i && currentChapter[i] == chapterTitle)
				{
					// 标题匹配，使用现有导航项。
					navNode = currentChapterNode[i];
				}
				else
				{
					// 标题不匹配，创建新的导航项。
					XmlElement newNavNode = CreateNavPoint(pageName, chapterTitle);
					navNode.AppendChild(newNavNode);
					navNode = newNavNode;
					currentChapter.RemoveRange(i, currentChapter.Count - i);
					currentChapter.Add(chapterTitle);
					currentChapterNode.RemoveRange(i, currentChapterNode.Count - i);
					currentChapterNode.Add(navNode);
				}
			}
		}
		navNode.AppendChild(CreateNavPoint(pageName, title));
	}

	/// <summary>
	/// 将导航写入指定的写入器。
	/// </summary>
	/// <param name="writer">XML 写入器。</param>
	public void WriteTo(XmlWriter writer)
	{
		document.WriteTo(writer);
	}

	/// <summary>
	/// 创建 NavPoint 节点。
	/// </summary>
	private XmlElement CreateNavPoint(string pageName, string title)
	{
		string idx = (navIndex++).ToString();
		var navPoint = EPubXml.CreateNCXElement(document, "navPoint");
		navPoint.SetAttribute("id", "nav_" + idx);
		navPoint.SetAttribute("playOrder", idx);
		var navLabel = EPubXml.CreateNCXElement(document, "navLabel");
		var text = EPubXml.CreateNCXElement(document, "text");
		navLabel.AppendChild(text);
		text.InnerText = title;
		navPoint.AppendChild(navLabel);
		var content = EPubXml.CreateNCXElement(document, "content");
		navPoint.AppendChild(content);
		content.SetAttribute("src", pageName);
		return navPoint;
	}
}
