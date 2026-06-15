using System.Xml.Linq;

namespace ClothingRepacker.Core.Codecs;

public interface IYmtCodec
{
    Task<XDocument> DecodeToXmlAsync(string ymtPath, CancellationToken cancellationToken = default);
    Task EncodeFromXmlAsync(XDocument xml, string outputYmtPath, CancellationToken cancellationToken = default);
}
