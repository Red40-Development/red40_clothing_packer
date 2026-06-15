using System.Xml.Linq;

namespace ClothingRepacker.Core.Codecs;

public sealed class CompositeYmtCodec : IYmtCodec
{
    private readonly IYmtCodec _xmlCodec;
    private readonly IYmtCodec _binaryCodec;

    public CompositeYmtCodec(IYmtCodec xmlCodec, IYmtCodec binaryCodec)
    {
        _xmlCodec = xmlCodec;
        _binaryCodec = binaryCodec;
    }

    public Task<XDocument> DecodeToXmlAsync(string ymtPath, CancellationToken cancellationToken = default)
        => IsXmlPath(ymtPath)
            ? _xmlCodec.DecodeToXmlAsync(ymtPath, cancellationToken)
            : _binaryCodec.DecodeToXmlAsync(ymtPath, cancellationToken);

    public Task EncodeFromXmlAsync(XDocument xml, string outputYmtPath, CancellationToken cancellationToken = default)
        => IsXmlPath(outputYmtPath)
            ? _xmlCodec.EncodeFromXmlAsync(xml, outputYmtPath, cancellationToken)
            : _binaryCodec.EncodeFromXmlAsync(xml, outputYmtPath, cancellationToken);

    private static bool IsXmlPath(string path)
        => path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
}
