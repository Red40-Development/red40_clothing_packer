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
        var pedFile = new PedFile();
        pedFile.Load(data, entry);

        var xml = pedFile.Meta is not null
            ? MetaXml.GetXml(pedFile.Meta)
            : pedFile.Pso is not null
                ? PsoXml.GetXml(pedFile.Pso)
                : pedFile.Rbf is not null
                    ? RbfXml.GetXml(pedFile.Rbf)
                    : null;

        if (string.IsNullOrWhiteSpace(xml))
        {
            throw new InvalidDataException($"CodeWalker could not decode '{ymtPath}' as a clothing YMT.");
        }

        return Task.FromResult(XDocument.Parse(xml, LoadOptions.PreserveWhitespace));
    }

    public Task EncodeFromXmlAsync(XDocument xml, string outputYmtPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var doc = new XmlDocument();
        using (var reader = xml.CreateReader())
        {
            doc.Load(reader);
        }

        var data = XmlMeta.GetData(doc, MetaFormat.RSC, string.Empty);
        if (data is null || data.Length == 0)
        {
            throw new InvalidDataException($"CodeWalker could not encode XML to binary YMT for '{outputYmtPath}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputYmtPath)!);
        File.WriteAllBytes(outputYmtPath, data);
        return Task.CompletedTask;
    }

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
