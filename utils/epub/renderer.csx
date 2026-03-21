#r "nuget: Cyjb.Markdown, 1.0.3"
#load "./xml.csx"

#nullable enable

using System.IO;
using System.IO.Compression;
using System.Xml;
using Cyjb.Markdown;
using Cyjb.Markdown.Renderer;
using Cyjb.Markdown.Syntax;

public interface IEPubExporter
{
	/// <summary>
	/// 添加一幅图片。
	/// </summary>
	/// <param name="filePath">图片的文件路径。</param>
	/// <param name="id">图片的标识符。</param>
	/// <param name="title">图片的标题。</param>
	/// <param name="isCover">图片是否是封面。</param>
	/// <returns>图片的路径。</returns>
	string AddImage(string url, string? id, string? title = null, bool isCover = false);
}

/// <summary>
/// ePub 的渲染器。
/// </summary>
public class EPubRenderer : BaseRenderer
{
	private readonly IEPubExporter exporter;
	/// <summary>
	/// XML 文档。
	/// </summary>
	private readonly XmlDocument document = new();
	/// <summary>
	/// 节点堆栈。
	/// </summary>
	private readonly Stack<XmlNode> nodeStack = new();
	/// <summary>
	/// 引用节点的嵌套深度。
	/// </summary>
	private int blockquoteDepth = 0;
	/// <summary>
	/// 是否只包含一个图片。
	/// </summary>
	private bool isSingleImage = false;

	/// <summary>
	/// 创建 <see cref="EPubRenderer"/> 的新实例。
	/// </summary>
	public EPubRenderer(IEPubExporter exporter)
	{
		this.exporter = exporter;
		nodeStack.Push(document);
	}

	/// <summary>
	/// 渲染指定的章节。
	/// </summary>
	/// <param name="doc">页面的 Markdown 文档。</param>
	/// <param name="title">页面标题。</param>
	public void Render(Document doc, string? title)
	{
		Clear();
		EPubXml.AddXmlDeclaration(document);

		// 检查 doc 是否只包含一个图片，可以优化图片的标签避免出现空白页。
		isSingleImage = IsSingleImage(doc);

		// 生成 HTML 的基础结构。
		var element = AddElement("html");
		element.SetAttribute("xml:lang", "zh-CN");
		nodeStack.Push(element);
		{
			nodeStack.Push(AddElement("head"));
			element = AddElement("link");
			element.SetAttribute("rel", "stylesheet");
			element.SetAttribute("type", "text/css");
			element.SetAttribute("href", "css/main.css");
			if (title != null)
			{
				AddElement("title").InnerText = title;
			}
			nodeStack.Pop(); // pop head
		}
		{
			nodeStack.Push(AddElement("body"));
			doc.Accept(this);
			nodeStack.Pop();
		}
		nodeStack.Pop(); // pop html
	}

	/// <summary>
	/// 写入当前渲染结果。
	/// </summary>
	/// <param name="write">要写入到的 XML。</param>
	public void WriteTo(XmlWriter writer)
	{
		document.WriteTo(writer);
	}

	/// <summary>
	/// 清除已生成的 XML 文本。
	/// </summary>
	public override void Clear()
	{
		base.Clear();
		document.RemoveAll();
	}

	/// <summary>
	/// 检查指定文档中是否只包含一张图片。
	/// </summary>
	private static bool IsSingleImage(Document doc)
	{
		foreach (var child in doc.Children)
		{
			switch (child.Kind)
			{
				case MarkdownKind.ThematicBreak:
				case MarkdownKind.Heading:
				case MarkdownKind.LinkDefinition:
				case MarkdownKind.Footnote:
					// 允许在图片之外显示的内容
					break;
				case MarkdownKind.Paragraph:
					foreach (var subChild in ((Paragraph)child).Children)
					{
						switch (subChild.Kind)
						{
							case MarkdownKind.HtmlComment:
							case MarkdownKind.HtmlProcessing:
							case MarkdownKind.HtmlDeclaration:
							case MarkdownKind.HtmlCData:
							case MarkdownKind.HardBreak:
							case MarkdownKind.SoftBreak:
							case MarkdownKind.Image:
								// 图片以及允许在图片之外显示的内容
								break;
							default:
								return false;
						}
					}
					break;
				default:
					return false;
			}
		}
		return true;
	}

	#region 块节点

	/// <summary>
	/// 访问指定的分割线节点。
	/// </summary>
	/// <param name="node">要访问的分割线节点。</param>
	public override void VisitThematicBreak(ThematicBreak node)
	{
		AddElement("hr");
	}

	/// <summary>
	/// 访问指定的标题节点。
	/// </summary>
	/// <param name="node">要访问的标题节点。</param>
	public override void VisitHeading(Heading node)
	{
		nodeStack.Push(AddElement($"h{node.Depth}"));
		DefaultVisit(node);
		nodeStack.Pop();
	}

	/// <summary>
	/// 访问指定的段落节点。
	/// </summary>
	/// <param name="node">要访问的段落节点。</param>
	public override void VisitParagraph(Paragraph node)
	{
		if (blockquoteDepth > 0)
		{
			// 检测引用块末尾的 — XXX 形式，渲染为 footer。
			if (node.Parent!.LastChild == node &&
				node.FirstChild is Literal literal &&
				literal.Content.StartsWith("—"))
			{
				nodeStack.Push(AddElement("footer"));
				DefaultVisit(node);
				nodeStack.Pop();
				return;
			}
		}
		bool loose = node.Parent?.Parent is not List list || list.Loose;
		if (loose)
		{
			// 只包含一张图片的段落，不生成 p 标签。
			if (node.Children.Count == 1 && node.Children[0].Kind == MarkdownKind.Image)
			{
				loose = false;
			}
		}
		if (loose)
		{
			nodeStack.Push(AddElement("p"));
		}
		DefaultVisit(node);
		if (loose)
		{
			nodeStack.Pop();
		}
	}

	/// <summary>
	/// 访问指定的引用节点。
	/// </summary>
	/// <param name="node">要访问的引用节点。</param>
	public override void VisitBlockquote(Blockquote node)
	{
		nodeStack.Push(AddElement("blockquote"));
		blockquoteDepth++;
		DefaultVisit(node);
		nodeStack.Pop();
		blockquoteDepth--;
	}

	#endregion // 块节点

	#region 行内节点

	/// <summary>
	/// 访问指定的强调节点。
	/// </summary>
	/// <param name="node">要访问的强调节点。</param>
	public override void VisitEmphasis(Emphasis node)
	{
		nodeStack.Push(AddElement("em"));
		DefaultVisit(node);
		nodeStack.Pop();
	}

	/// <summary>
	/// 访问指定的加粗节点。
	/// </summary>
	/// <param name="node">要访问的加粗节点。</param>
	public override void VisitStrong(Strong node)
	{
		nodeStack.Push(AddElement("strong"));
		DefaultVisit(node);
		nodeStack.Pop();
	}

	/// <summary>
	/// 访问指定的删除节点。
	/// </summary>
	/// <param name="node">要访问的删除节点。</param>
	public override void VisitStrikethrough(Strikethrough node)
	{
		nodeStack.Push(AddElement("del"));
		DefaultVisit(node);
		nodeStack.Pop();
	}

	/// <summary>
	/// 访问指定的链接节点。
	/// </summary>
	/// <param name="node">要访问的链接节点。</param>
	public override void VisitLink(Link node)
	{
		if (node.Kind == MarkdownKind.Image)
		{
			var isCover = node.Attributes["cover"] != null;
			var src = exporter.AddImage(node.URL, node.Attributes["id"], node.Attributes["alt"], isCover);
			if (isCover || isSingleImage)
			{
				// 封面生成为 <img class="cover" />
				// 只有一张图片时也使用 cover 模式，避免页面中出现不必要的空白。
				var element = AddElement("img");
				element.SetAttribute("src", src);
				element.SetAttribute("class", "cover");
			}
			else
			{
				// 普通图片生成为 <figure><img /></figure>
				nodeStack.Push(AddElement("figure"));
				AddElement("img").SetAttribute("src", src);
				nodeStack.Pop();
			}
		}
	}

	/// <summary>
	/// 访问指定的文本节点。
	/// </summary>
	/// <param name="node">要访问的文本节点。</param>
	public override void VisitLiteral(Literal node)
	{
		nodeStack.Peek().AppendChild(document.CreateTextNode(node.Content));
	}

	#endregion // 行内节点

	/// <summary>
	/// 添加指定的 HTML 节点。
	/// </summary>
	private XmlElement AddElement(string name)
	{
		var element = document.CreateElement(name, "http://www.w3.org/1999/xhtml");
		nodeStack.Peek().AppendChild(element);
		return element;
	}
}
