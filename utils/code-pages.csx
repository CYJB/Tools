#r "nuget: System.Text.Encoding.CodePages, 10.0.5"

using System.Text;

// 注册 GBK 编码支持。
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
