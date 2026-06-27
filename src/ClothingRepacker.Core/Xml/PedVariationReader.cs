using ClothingRepacker.Core.Models;
using System.Globalization;
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

        var dlcName = root.Element("dlcName")?.Value.Trim() ?? string.Empty;
        var (pedBaseName, gender, collectionName, fullCollectionName) = InferIdentity(ymtPath, root.Attribute("name")?.Value);

        var availCompValues = ParseIntList(RequiredElement(root, "availComp").Value);
        if (availCompValues.Length != ClothingConstants.ComponentSlotCount)
        {
            messages.Add(new(ValidationSeverity.Error, "availComp-length", $"availComp must have {ClothingConstants.ComponentSlotCount} entries."));
        }

        var componentData = Items(RequiredElement(root, "aComponentData3"));
        var compInfos = Items(RequiredElement(root, "compInfos"));
        var components = new List<ComponentBlock>();
        var componentRepairHints = new List<CreatureComponentRepairHint>();

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
                else if (componentId == 6
                         && TryGetFloatArrayValue(compInfo, "pedXml_expressionMods", 4, out var highHeelExpression)
                         && highHeelExpression != 0)
                {
                    componentRepairHints.Add(new CreatureComponentRepairHint(componentId, drawblIdx));
                }
            }

            components.Add(new ComponentBlock(componentId, drawables, componentInfos));
        }

        var (props, propRepairHints) = ReadProps(root, messages);
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
            componentRepairHints,
            propRepairHints,
            messages);
    }

    private static (IReadOnlyList<PropBlock> Props, IReadOnlyList<CreaturePropRepairHint> RepairHints) ReadProps(XElement root, List<ValidationMessage> messages)
    {
        var propInfo = root.Element("propInfo");
        if (propInfo is null)
        {
            return ([], []);
        }

        var metadata = Items(propInfo.Element("aPropMetaData"));
        var repairHints = metadata
            .Where(item => TryGetValue(item, "anchorId", out var anchorId)
                           && anchorId == 0
                           && TryGetValue(item, "propId", out var propId)
                           && propId >= 0
                           && TryGetFloatArrayValue(item, "expressionMods", 0, out var hairScaleExpression)
                           && hairScaleExpression != 0)
            .Select(item => new CreaturePropRepairHint(GetValue(item, "anchorId"), GetValue(item, "propId")))
            .ToList();

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

        return (results, repairHints);
    }

    private static (string PedBaseName, PedGender Gender, string CollectionName, string FullCollectionName) InferIdentity(string ymtPath, string? collectionName)
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

        collectionName = collectionName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(collectionName))
        {
            return InferNamelessIdentity(filename);
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

        return (pedBaseName, gender, collectionName, fullCollectionName);
    }

    private static (string PedBaseName, PedGender Gender, string CollectionName, string FullCollectionName) InferNamelessIdentity(string filename)
    {
        if (TryInferFreemodeIdentity(filename, "mp_f_freemode_01", PedGender.Female, out var femaleIdentity))
        {
            return femaleIdentity;
        }

        if (TryInferFreemodeIdentity(filename, "mp_m_freemode_01", PedGender.Male, out var maleIdentity))
        {
            return maleIdentity;
        }

        return ("unknown_ped", PedGender.Unknown, filename, filename);
    }

    private static bool TryInferFreemodeIdentity(
        string filename,
        string pedBaseName,
        PedGender gender,
        out (string PedBaseName, PedGender Gender, string CollectionName, string FullCollectionName) identity)
    {
        if (filename.Equals(pedBaseName, StringComparison.OrdinalIgnoreCase))
        {
            identity = (pedBaseName, gender, string.Empty, filename);
            return true;
        }

        var prefix = $"{pedBaseName}_";
        if (filename.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            identity = (pedBaseName, gender, filename[prefix.Length..], filename);
            return true;
        }

        identity = (string.Empty, PedGender.Unknown, string.Empty, string.Empty);
        return false;
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

    private static int GetValue(XElement item, string name)
        => ValueAttr(RequiredElement(item, name));

    private static bool TryGetFloatArrayValue(XElement parent, string name, int index, out double value)
    {
        value = 0;
        var element = parent.Element(name);
        if (element is null)
        {
            return false;
        }

        var child = element.Element($"f{index}");
        if (child?.Attribute("value") is { } childAttribute)
        {
            return double.TryParse(childAttribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        if (element.Attribute("value") is { } valueAttribute)
        {
            return double.TryParse(valueAttribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        var values = element.Value.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return index >= 0
               && index < values.Length
               && double.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
