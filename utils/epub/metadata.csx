#load "./xml.csx"

#nullable enable

using System.Xml;

/// <summary>
/// ePub 的元数据。
/// </summary>
class EPubMetadata
{
	/// <summary>
	/// 元数据 XML 文档。
	/// </summary>
	private readonly XmlDocument document = new();
	/// <summary>
	/// 元数据 XML 节点。
	/// </summary>
	private readonly XmlElement metadataNode;
	/// <summary>
	/// manifest XML 节点。
	/// </summary>
	private readonly XmlElement manifestNode;
	/// <summary>
	/// spine XML 节点。
	/// </summary>
	private readonly XmlElement spineNode;
	/// <summary>
	/// 是否已设置封面。
	/// </summary>
	private bool hasCover = false;

	public EPubMetadata(string uuid, string title, string author)
	{
		EPubXml.AddXmlDeclaration(document);
		var package = EPubXml.CreateOPFElement(document, "package");
		document.AppendChild(package);
		package.SetAttribute("unique-identifier", "uuid_id");
		package.SetAttribute("version", "2.0");

		metadataNode = EPubXml.CreateOPFElement(document, "metadata");
		package.AppendChild(metadataNode);
		metadataNode.SetAttribute("xmlns:dc", EPubXml.DCNamespaceURI);

		var titleElement = EPubXml.CreateDCElement(document, "title");
		titleElement.InnerText = title;
		metadataNode.AppendChild(titleElement);

		var creator = EPubXml.CreateDCElement(document, "creator");
		metadataNode.AppendChild(creator);
		creator.SetAttribute("role", "aut");
		creator.SetAttribute("file-as", author);
		creator.InnerText = author;

		var identifier = EPubXml.CreateDCElement(document, "identifier");
		metadataNode.AppendChild(identifier);
		identifier.SetAttribute("scheme", "uuid");
		identifier.SetAttribute("id", "uuid_id");
		identifier.InnerText = uuid;

		manifestNode = EPubXml.CreateOPFElement(document, "manifest");
		package.AppendChild(manifestNode);

		spineNode = EPubXml.CreateOPFElement(document, "spine");
		spineNode.SetAttribute("toc", "ncx");
		package.AppendChild(spineNode);
	}

	/// <summary>
	/// 添加指定的封面。
	/// </summary>
	/// <param name="id">封面 ID。</param>
	public void AddCover(string id)
	{
		if (hasCover)
		{
			return;
		}
		hasCover = true;
		var meta = EPubXml.CreateOPFElement(document, "meta");
		metadataNode.AppendChild(meta);
		meta.SetAttribute("name", "cover");
		meta.SetAttribute("content", id);
	}

	/// <summary>
	/// 添加指定的 manifest 项。
	/// </summary>
	public void AddManifest(string id, string href, string mediaType, bool addSpine = false)
	{
		var item = EPubXml.CreateOPFElement(document, "item");
		manifestNode.AppendChild(item);
		item.SetAttribute("id", id);
		item.SetAttribute("href", href);
		item.SetAttribute("media-type", mediaType);
		if (addSpine)
		{
			var itemref = EPubXml.CreateOPFElement(document, "itemref");
			spineNode.AppendChild(itemref);
			itemref.SetAttribute("idref", id);
		}
	}

	/// <summary>
	/// 将元数据写入指定的写入器。
	/// </summary>
	/// <param name="writer">XML 写入器。</param>
	public void WriteTo(XmlWriter writer)
	{
		document.WriteTo(writer);
	}
}
