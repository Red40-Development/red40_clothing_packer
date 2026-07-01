using System.Globalization;
using System.Xml.Linq;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Xml;

namespace ClothingRepacker.Core.Planning;

public sealed class AlternateMetadataBuilder
{
    private static readonly IReadOnlyDictionary<string, int> ComponentIdsByPrefix = ClothingConstants.ComponentPrefixes
        .ToDictionary(item => item.Value, item => item.Key, StringComparer.OrdinalIgnoreCase);

    public XDocument BuildAlternateVariationsXml(IEnumerable<XDocument> sourceXmls, IReadOnlyList<DrawableMapping> drawableMappings)
    {
        var mappings = BuildMappingLookup(drawableMappings, useFullCollection: false);
        var switchesByPed = new Dictionary<string, List<XElement>>(StringComparer.OrdinalIgnoreCase);
        var seenByPed = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceXml in sourceXmls)
        {
            if (sourceXml.Root?.Name.LocalName != "CAlternateVariations")
            {
                continue;
            }

            foreach (var ped in XmlHelpers.Items(sourceXml.Root.Element("peds")))
            {
                var pedName = ped.Element("name")?.Value.Trim();
                if (string.IsNullOrWhiteSpace(pedName))
                {
                    continue;
                }

                foreach (var item in XmlHelpers.Items(ped.Element("switches")))
                {
                    if (!TryRemapAlternateVariationSwitch(item, mappings, out var remapped))
                    {
                        continue;
                    }

                    var switches = GetOrCreate(switchesByPed, pedName);
                    var seen = GetOrCreate(seenByPed, pedName);
                    AddUnique(switches, seen, remapped);
                }
            }
        }

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("CAlternateVariations",
                new XElement("peds",
                    switchesByPed
                        .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(item => new XElement("Item",
                            new XElement("name", item.Key),
                            new XElement("switches", item.Value))))));
    }

    public XDocument BuildFirstPersonAlternatesXml(IEnumerable<XDocument> sourceXmls, IReadOnlyList<DrawableMapping> drawableMappings)
    {
        var mappings = BuildMappingLookup(drawableMappings, useFullCollection: true);
        var alternates = new List<XElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sourceXml in sourceXmls)
        {
            if (sourceXml.Root?.Name.LocalName != "FirstPersonAlternateData")
            {
                continue;
            }

            foreach (var item in XmlHelpers.Items(sourceXml.Root.Element("alternates")))
            {
                var assetName = item.Element("assetName")?.Value.Trim();
                if (assetName is null || !TryRemapFirstPersonAssetName(assetName, mappings, out var remappedAssetName))
                {
                    continue;
                }

                var clone = new XElement(item);
                clone.Element("assetName")!.Value = remappedAssetName;
                AddUnique(alternates, seen, clone);
            }
        }

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("FirstPersonAlternateData",
                new XElement("alternates", alternates)));
    }

    private static bool TryRemapAlternateVariationSwitch(
        XElement source,
        IReadOnlyDictionary<string, DrawableMapping> mappings,
        out XElement remapped)
    {
        remapped = new XElement(source);
        var remappedSwitch = TryRemapAlternateVariationAsset(remapped, mappings);
        var sourceAssets = remapped.Element("sourceAssets");
        var hadSourceAssets = sourceAssets?.Elements("Item").Any() == true;
        var remappedSourceAssets = new List<XElement>();

        if (sourceAssets is not null)
        {
            foreach (var asset in sourceAssets.Elements("Item"))
            {
                var remappedAsset = new XElement(asset);
                if (TryRemapAlternateVariationAsset(remappedAsset, mappings))
                {
                    remappedSourceAssets.Add(remappedAsset);
                }
            }

            sourceAssets.ReplaceNodes(remappedSourceAssets);
        }

        return remappedSwitch || (hadSourceAssets && remappedSourceAssets.Count > 0);
    }

    private static bool TryRemapAlternateVariationAsset(XElement item, IReadOnlyDictionary<string, DrawableMapping> mappings)
    {
        var dlcNameHash = item.Element("dlcNameHash")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(dlcNameHash)
            || !TryGetValue(item, "component", out var component)
            || !TryGetValue(item, "index", out var index))
        {
            return false;
        }

        if (!mappings.TryGetValue(BuildKey(dlcNameHash, component, index), out var mapping))
        {
            return false;
        }

        item.Element("dlcNameHash")!.Value = mapping.TargetCollection;
        XmlHelpers.SetValueAttr(XmlHelpers.RequiredElement(item, "index"), mapping.NewDrawableIndex);
        return true;
    }

    private static bool TryRemapFirstPersonAssetName(
        string assetName,
        IReadOnlyDictionary<string, DrawableMapping> mappings,
        out string remappedAssetName)
    {
        remappedAssetName = string.Empty;
        var slashIndex = assetName.IndexOf('/');
        if (slashIndex <= 0 || slashIndex == assetName.Length - 1)
        {
            return false;
        }

        var collection = assetName[..slashIndex];
        var asset = assetName[(slashIndex + 1)..];
        foreach (var (prefix, componentId) in ComponentIdsByPrefix.OrderByDescending(item => item.Key.Length))
        {
            var prefixWithSeparator = $"{prefix}_";
            if (!asset.StartsWith(prefixWithSeparator, StringComparison.OrdinalIgnoreCase)
                || asset.Length < prefixWithSeparator.Length + 3)
            {
                continue;
            }

            var indexText = asset.Substring(prefixWithSeparator.Length, 3);
            if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var drawableIndex))
            {
                continue;
            }

            if (!mappings.TryGetValue(BuildKey(collection, componentId, drawableIndex), out var mapping))
            {
                return false;
            }

            var suffix = asset[(prefixWithSeparator.Length + 3)..];
            remappedAssetName = $"{mapping.TargetFullCollection}/{prefix}_{mapping.NewDrawableIndex:000}{suffix}";
            return true;
        }

        return false;
    }

    private static IReadOnlyDictionary<string, DrawableMapping> BuildMappingLookup(
        IReadOnlyList<DrawableMapping> drawableMappings,
        bool useFullCollection)
    {
        var result = new Dictionary<string, DrawableMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in drawableMappings)
        {
            var collection = useFullCollection ? mapping.SourceFullCollection : mapping.SourceCollection;
            result[BuildKey(collection, mapping.ComponentId, mapping.OldDrawableIndex)] = mapping;
        }

        return result;
    }

    private static bool TryGetValue(XElement parent, string name, out int value)
    {
        value = 0;
        var attribute = parent.Element(name)?.Attribute("value");
        return attribute is not null && XmlHelpers.TryParseIntValue(attribute.Value, out value);
    }

    private static string BuildKey(string collection, int component, int index)
        => $"{collection}|{component}|{index}";

    private static List<XElement> GetOrCreate(Dictionary<string, List<XElement>> dictionary, string key)
    {
        if (!dictionary.TryGetValue(key, out var value))
        {
            value = [];
            dictionary[key] = value;
        }

        return value;
    }

    private static HashSet<string> GetOrCreate(Dictionary<string, HashSet<string>> dictionary, string key)
    {
        if (!dictionary.TryGetValue(key, out var value))
        {
            value = new HashSet<string>(StringComparer.Ordinal);
            dictionary[key] = value;
        }

        return value;
    }

    private static void AddUnique(List<XElement> target, HashSet<string> seen, XElement element)
    {
        var key = element.ToString(SaveOptions.DisableFormatting);
        if (seen.Add(key))
        {
            target.Add(element);
        }
    }
}
