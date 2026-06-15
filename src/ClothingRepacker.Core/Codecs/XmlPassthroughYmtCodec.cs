using System.Xml.Linq;

namespace ClothingRepacker.Core.Codecs;

public sealed class XmlPassthroughYmtCodec : IYmtCodec
{
    public Task<XDocument> DecodeToXmlAsync(string ymtPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(XDocument.Load(ymtPath, LoadOptions.PreserveWhitespace));
    }

    public Task EncodeFromXmlAsync(XDocument xml, string outputYmtPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(outputYmtPath)!);
        xml.Save(outputYmtPath);
        return Task.CompletedTask;
    }
}
