#nullable enable

using System.Xml;

/// <summary>
/// 提供 epub 的 XML 辅助方法。
/// </summary>
static class EPubXml
{
	/// <summary>
	/// OPF 规范的命名空间。
	/// </summary>
	private const string OPFNamespaceURI = "http://www.idpf.org/2007/opf";

	/// <summary>
	/// DC 规范的命名空间。
	/// </summary>
	public const string DCNamespaceURI = "http://purl.org/dc/elements/1.1/";

	/// <summary>
	/// NCX 规范的命名空间。
	/// </summary>
	private const string NCXNamespaceURI = "http://www.daisy.org/z3986/2005/ncx/";

	/// <summary>
	/// 向指定 XML 文档添加 XML 声明。
	/// </summary>
	/// <param name="doc">当前 XML 文档。</param>
	/// <returns>当前 XML 文档。</returns>
	public static XmlDocument AddXmlDeclaration(XmlDocument doc)
	{
		doc.AppendChild(doc.CreateXmlDeclaration("1.0", "utf-8", null));
		return doc;
	}

	/// <summary>
	/// 创建 OPF 规范的元素。
	/// </summary>
	/// <param name="doc">当前 XML 文档。</param>
	/// <param name="qualifiedName">元素的标签。</param>
	/// <returns>OPF 规范的元素。</returns>
	public static XmlElement CreateOPFElement(XmlDocument doc, string qualifiedName)
	{
		return doc.CreateElement(qualifiedName, OPFNamespaceURI);
	}

	/// <summary>
	/// 创建 DC 规范的元素。
	/// </summary>
	/// <param name="doc">当前 XML 文档。</param>
	/// <param name="qualifiedName">元素的标签。</param>
	/// <returns>DC 规范的元素。</returns>
	public static XmlElement CreateDCElement(XmlDocument doc, string qualifiedName)
	{
		return doc.CreateElement("dc", qualifiedName, DCNamespaceURI);
	}

	/// <summary>
	/// 创建 NCX 规范的元素。
	/// </summary>
	/// <param name="doc">当前 XML 文档。</param>
	/// <param name="qualifiedName">元素的标签。</param>
	/// <returns>NCX 规范的元素。</returns>
	public static XmlElement CreateNCXElement(XmlDocument doc, string qualifiedName)
	{
		return doc.CreateElement(qualifiedName, NCXNamespaceURI);
	}
}
