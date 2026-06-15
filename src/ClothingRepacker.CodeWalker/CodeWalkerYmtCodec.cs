using System.Xml.Linq;
using ClothingRepacker.Core.Codecs;

namespace ClothingRepacker.CodeWalker;

public sealed class CodeWalkerYmtCodec : IYmtCodec
{
    public Task<XDocument> DecodeToXmlAsync(string ymtPath, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Binary YMT decode is not wired yet. Use XML fixtures or the XmlPassthroughYmtCodec.");

    public Task EncodeFromXmlAsync(XDocument xml, string outputYmtPath, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Binary YMT encode is not wired yet. See CodeWalkerIntegrationReport.md.");
}
