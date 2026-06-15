using ClothingRepacker.Core.Models;
using System.Xml.Linq;
using static ClothingRepacker.Core.Xml.XmlHelpers;

namespace ClothingRepacker.Core.Xml;

public sealed class PedVariationReader
{
    public SourceYmt Read(XDocument xml, string ymtPath, string resourceName, string resourceRoot)
    {
        var messages = new List<ValidationMessage>();
        var root = xml.Root ?? throw new InvalidDataException("Missing XML root.");
        if (root.Name.LocalName != "CPedVariationInfo")
        {
            throw new InvalidDataException($"Unexpected root '{root.Name}'.");
        }

        var collectionName = RequiredAttribute(root, "name").Value;
        var dlcName = root.Element("dlcName")?.Value.Trim() ?? string.Empty;
        var (pedBaseName, gender, fullCollectionName) = InferIdentity(ymtPath, collectionName);

        var availCompValues = ParseIntList(RequiredElement(root, "availComp").Value);
        if (availCompValues.Length != ClothingConstants.ComponentSlotCount)
        {
            messages.Add(new(ValidationSeverity.Error, "availComp-length", $"availComp must have {ClothingConstants.ComponentSlotCount} entries."));
        }

        var componentData = Items(RequiredElement(root, "aComponentData3"));
        var compInfos = Items(RequiredElement(root, "compInfos"));
        var components = new List<ComponentBlock>();

        for (var componentId = 0; componentId < Math.Min(availCompValues.Length, ClothingConstants.ComponentSlotCount); componentId++)
        {
            var componentDataIndex = availCompValues[componentId];
            if (componentDataIndex == ClothingConstants.MissingComponent)
            {
                continue;
            }

            if (componentDataIndex < 0 || componentDataIndex >= componentData.Count)
            {
                messages.Add(new(ValidationSeverity.Error, "availComp-range", $"Component {componentId} points outside aComponentData3."));
                continue;
            }

            var componentItem = componentData[componentDataIndex];
            var drawables = Items(RequiredElement(componentItem, "aDrawblData3"))
                .Select(item => new XElement(item))
                .ToList();

            var componentInfos = compInfos
                .Where(item => TryGetValue(item, "pedXml_compIdx", out var value) && value == componentId)
                .Select(item => new XElement(item))
                .ToList();

            var duplicatePairs = componentInfos
                .GroupBy(item => TryGetValue(item, "pedXml_drawblIdx", out var drawbl) ? drawbl : -1)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();

            foreach (var duplicatePair in duplicatePairs)
            {
                messages.Add(new(ValidationSeverity.Warning, "duplicate-compInfo", $"Component {componentId} has duplicate compInfo for drawable {duplicatePair}."));
            }

            foreach (var compInfo in componentInfos)
            {
                if (!TryGetValue(compInfo, "pedXml_drawblIdx", out var drawblIdx))
                {
                    continue;
                }

                if (drawblIdx < 0 || drawblIdx >= drawables.Count)
                {
                    messages.Add(new(ValidationSeverity.Error, "compInfo-range", $"Component {componentId} compInfo drawable index {drawblIdx} is out of range."));
                }
            }

            components.Add(new ComponentBlock(componentId, drawables, componentInfos));
        }

        var props = ReadProps(root, messages);
        return new SourceYmt(
            ymtPath,
            resourceName,
            resourceRoot,
            pedBaseName,
            gender,
            collectionName,
            fullCollectionName,
            dlcName,
            xml,
            components,
            props,
            messages);
    }

    private static IReadOnlyList<PropBlock> ReadProps(XElement root, List<ValidationMessage> messages)
    {
        var propInfo = root.Element("propInfo");
        if (propInfo is null)
        {
            return [];
        }

        var metadata = Items(propInfo.Element("aPropMetaData"));
        var grouped = metadata
            .Select((item, index) => new
            {
                Item = new XElement(item),
                Index = index,
                AnchorId = TryGetValue(item, "anchorId", out var anchorId) ? anchorId : -1,
                PropId = TryGetValue(item, "propId", out var propId) ? propId : -1,
            })
            .GroupBy(item => item.AnchorId)
            .OrderBy(group => group.Key);

        var results = new List<PropBlock>();
        foreach (var group in grouped)
        {
            var duplicates = group.GroupBy(item => item.PropId).Where(item => item.Key >= 0 && item.Count() > 1);
            foreach (var duplicate in duplicates)
            {
                messages.Add(new(ValidationSeverity.Warning, "duplicate-propId", $"Anchor {group.Key} has duplicate propId {duplicate.Key}."));
            }

            var ordered = group.OrderBy(item => item.PropId).ThenBy(item => item.Index).Select(item => item.Item).ToList();
            results.Add(new PropBlock(group.Key, ordered));
        }

        return results;
    }

    private static (string PedBaseName, PedGender Gender, string FullCollectionName) InferIdentity(string ymtPath, string collectionName)
    {
        var filename = Path.GetFileName(ymtPath);
        if (filename.EndsWith(".ymt.xml", StringComparison.OrdinalIgnoreCase))
        {
            filename = filename[..^8];
        }
        else if (filename.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            filename = filename[..^4];
        }
        else if (filename.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase))
        {
            filename = filename[..^4];
        }

        var pedBaseName = string.Empty;
        var fullCollectionName = filename;
        if (filename.EndsWith($"_{collectionName}", StringComparison.OrdinalIgnoreCase))
        {
            pedBaseName = filename[..^(collectionName.Length + 1)];
        }

        var gender = pedBaseName.Contains("mp_f_freemode_01", StringComparison.OrdinalIgnoreCase)
            ? PedGender.Female
            : pedBaseName.Contains("mp_m_freemode_01", StringComparison.OrdinalIgnoreCase)
                ? PedGender.Male
                : PedGender.Unknown;

        if (string.IsNullOrWhiteSpace(pedBaseName))
        {
            pedBaseName = gender switch
            {
                PedGender.Female => "mp_f_freemode_01",
                PedGender.Male => "mp_m_freemode_01",
                _ => "unknown_ped",
            };

            fullCollectionName = $"{pedBaseName}_{collectionName}";
        }

        return (pedBaseName, gender, fullCollectionName);
    }

    private static bool TryGetValue(XElement parent, string name, out int value)
    {
        value = 0;
        var element = parent.Element(name);
        if (element?.Attribute("value") is not { } attribute)
        {
            return false;
        }

        return int.TryParse(attribute.Value, out value);
    }
}
