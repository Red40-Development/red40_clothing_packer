using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using ClothingRepacker.Core.Codecs;
using CodeWalker.GameFiles;

namespace ClothingRepacker.CodeWalker;

public sealed class CodeWalkerYmtCodec : IYmtCodec
{
    public Task<XDocument> DecodeToXmlAsync(string ymtPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var data = File.ReadAllBytes(ymtPath);
        var entry = CreateFileEntry(Path.GetFileName(ymtPath), ymtPath, ref data);
        var ymtFile = new YmtFile();
        ymtFile.Load(data, entry);

        var xml = ymtFile.Meta is not null
            ? MetaXml.GetXml(ymtFile.Meta)
            : ymtFile.Pso is not null
                ? PsoXml.GetXml(ymtFile.Pso)
                : ymtFile.Rbf is not null
                    ? RbfXml.GetXml(ymtFile.Rbf)
                    : null;

        if (string.IsNullOrWhiteSpace(xml))
        {
            throw new InvalidDataException($"CodeWalker could not decode '{ymtPath}' as a YMT.");
        }

        return Task.FromResult(XDocument.Parse(xml, LoadOptions.PreserveWhitespace));
    }

    public Task EncodeFromXmlAsync(XDocument xml, string outputYmtPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var metaFormat = GetMetaFormat(xml);
        var encoderXml = metaFormat == MetaFormat.RBF
            ? PrepareXmlForCodeWalkerRbf(xml)
            : PrepareXmlForCodeWalkerMeta(xml);
        var doc = new XmlDocument();
        using (var reader = encoderXml.CreateReader())
        {
            doc.Load(reader);
        }

        var data = XmlMeta.GetData(doc, metaFormat, string.Empty);
        if (data is null || data.Length == 0)
        {
            throw new InvalidDataException($"CodeWalker could not encode XML to binary YMT for '{outputYmtPath}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputYmtPath)!);
        File.WriteAllBytes(outputYmtPath, data);
        return Task.CompletedTask;
    }

    private static MetaFormat GetMetaFormat(XDocument xml)
        => xml.Root?.Name.LocalName == "CCreatureMetaData"
            ? MetaFormat.RBF
            : MetaFormat.RSC;

    private static XDocument PrepareXmlForCodeWalkerMeta(XDocument xml)
    {
        var clone = new XDocument(xml);
        foreach (var attribute in clone.Descendants().Attributes("value"))
        {
            var text = attribute.Value.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                attribute.Value = IsSignedIndexAttribute(attribute) && value > int.MaxValue
                    ? unchecked((int)value).ToString(CultureInfo.InvariantCulture)
                    : value.ToString(CultureInfo.InvariantCulture);
            }
        }

        return clone;
    }

    private static XDocument PrepareXmlForCodeWalkerRbf(XDocument xml)
    {
        var clone = new XDocument(xml);
        foreach (var attribute in clone.Descendants().Attributes("value"))
        {
            var text = attribute.Value.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexValue))
            {
                attribute.Value = FormatHexValue(hexValue);
            }
            else if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signedValue))
            {
                attribute.Value = FormatHexValue(unchecked((uint)signedValue));
            }
            else if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsignedValue))
            {
                attribute.Value = FormatHexValue(unsignedValue);
            }
        }

        return clone;
    }

    private static string FormatHexValue(uint value)
        => $"0x{value:X}";

    private static bool IsSignedIndexAttribute(XAttribute attribute)
        => attribute.Parent?.Name.LocalName.EndsWith("VarIndex", StringComparison.OrdinalIgnoreCase) == true;

    private static RpfFileEntry CreateFileEntry(string name, string path, ref byte[] data)
    {
        RpfFileEntry entry;
        var rsc7 = data.Length > 4 ? BitConverter.ToUInt32(data, 0) : 0u;
        if (rsc7 == 0x37435352)
        {
            entry = RpfFile.CreateResourceFileEntry(ref data, 0);
            data = ResourceBuilder.Decompress(data);
        }
        else
        {
            var binaryEntry = new RpfBinaryFileEntry
            {
                FileSize = (uint)data.Length,
                FileUncompressedSize = (uint)data.Length,
            };
            entry = binaryEntry;
        }

        entry.Name = name;
        entry.NameLower = name.ToLowerInvariant();
        entry.NameHash = JenkHash.GenHash(entry.NameLower);
        entry.ShortNameHash = JenkHash.GenHash(Path.GetFileNameWithoutExtension(entry.NameLower));
        entry.Path = path;
        return entry;
    }
}
