using System.Xml.Linq;
using ClothingRepacker.Core.Hashing;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Xml;
using static ClothingRepacker.Core.Xml.XmlHelpers;

namespace ClothingRepacker.Core.Planning;

public sealed class OutputCollectionBuilder
{
    private readonly Dictionary<int, List<XElement>> _componentDrawables = [];
    private readonly Dictionary<int, List<XElement>> _componentInfos = [];
    private readonly Dictionary<int, List<XElement>> _props = [];

    public OutputCollectionBuilder(string collectionName, string fullCollectionName, string pedBaseName, PedGender gender)
    {
        CollectionName = collectionName;
        FullCollectionName = fullCollectionName;
        PedBaseName = pedBaseName;
        Gender = gender;
    }

    public string CollectionName { get; }
    public string FullCollectionName { get; }
    public string PedBaseName { get; }
    public PedGender Gender { get; }

    public IReadOnlyList<DrawableMapping> AddComponents(SourceYmt source)
        => AddComponents(
            source,
            source.Components.ToDictionary(
                component => component.ComponentId,
                component => new SourceIndexRange(source.YmtPath, component.ComponentId, 0, component.Drawables.Count)));

    public IReadOnlyList<DrawableMapping> AddComponents(SourceYmt source, IReadOnlyDictionary<int, SourceIndexRange> componentRanges)
    {
        var mappings = new List<DrawableMapping>();
        foreach (var component in source.Components.OrderBy(c => c.ComponentId))
        {
            if (!componentRanges.TryGetValue(component.ComponentId, out var range) || range.Count <= 0)
            {
                continue;
            }

            var targetDrawables = GetOrCreate(_componentDrawables, component.ComponentId);
            var targetInfos = GetOrCreate(_componentInfos, component.ComponentId);
            var targetOffset = targetDrawables.Count;
            var rangeEnd = Math.Min(range.StartIndex + range.Count, component.Drawables.Count);

            for (var index = range.StartIndex; index < rangeEnd; index++)
            {
                targetDrawables.Add(new XElement(component.Drawables[index]));
                mappings.Add(new DrawableMapping(
                    source.ResourceName,
                    source.YmtPath,
                    source.CollectionName,
                    source.FullCollectionName,
                    CollectionName,
                    FullCollectionName,
                    PedBaseName,
                    component.ComponentId,
                    index,
                    targetOffset + index - range.StartIndex));
            }

            foreach (var compInfo in component.CompInfos)
            {
                var clone = new XElement(compInfo);
                if (clone.Element("pedXml_drawblIdx") is { } drawblIdx)
                {
                    var sourceDrawableIndex = ValueAttr(drawblIdx);
                    if (sourceDrawableIndex < range.StartIndex || sourceDrawableIndex >= rangeEnd)
                    {
                        continue;
                    }

                    SetValueAttr(drawblIdx, targetOffset + sourceDrawableIndex - range.StartIndex);
                }

                targetInfos.Add(clone);
            }
        }

        return mappings;
    }

    public IReadOnlyList<PropMapping> AddProps(SourceYmt source)
        => AddProps(
            source,
            source.Props.ToDictionary(
                prop => prop.AnchorId,
                prop => new SourceIndexRange(source.YmtPath, prop.AnchorId, 0, prop.Props.Count)));

    public IReadOnlyList<PropMapping> AddProps(SourceYmt source, IReadOnlyDictionary<int, SourceIndexRange> propRanges)
    {
        var mappings = new List<PropMapping>();
        foreach (var prop in source.Props.OrderBy(p => p.AnchorId))
        {
            if (!propRanges.TryGetValue(prop.AnchorId, out var range) || range.Count <= 0)
            {
                continue;
            }

            var targetProps = GetOrCreate(_props, prop.AnchorId);
            var targetOffset = targetProps.Count;
            var ordered = prop.Props
                .OrderBy(item => ValueAttr(RequiredElement(item, "propId")))
                .Where(item =>
                {
                    var propId = ValueAttr(RequiredElement(item, "propId"));
                    return propId >= range.StartIndex && propId < range.StartIndex + range.Count;
                })
                .ToList();

            for (var ordinal = 0; ordinal < ordered.Count; ordinal++)
            {
                var clone = new XElement(ordered[ordinal]);
                var oldPropId = ValueAttr(RequiredElement(clone, "propId"));
                var newPropId = targetOffset + ordinal;
                SetValueAttr(RequiredElement(clone, "propId"), newPropId);
                targetProps.Add(clone);

                mappings.Add(new PropMapping(
                    source.ResourceName,
                    source.YmtPath,
                    source.CollectionName,
                    source.FullCollectionName,
                    CollectionName,
                    FullCollectionName,
                    PedBaseName,
                    prop.AnchorId,
                    oldPropId,
                    newPropId));
            }
        }

        return mappings;
    }

    public XDocument BuildXml()
    {
        var availComp = Enumerable.Repeat(ClothingConstants.MissingComponent, ClothingConstants.ComponentSlotCount).ToArray();
        var componentItems = new List<XElement>();
        foreach (var component in _componentDrawables.OrderBy(item => item.Key))
        {
            availComp[component.Key] = componentItems.Count;
            var numAvailTex = component.Value.Sum(drawable => Items(drawable.Element("aTexData")).Count) % 256;
            componentItems.Add(new XElement("Item",
                new XElement("numAvailTex", new XAttribute("value", numAvailTex)),
                new XElement("aDrawblData3", new XAttribute("itemType", "CPVDrawblData"), component.Value)));
        }

        var propItems = _props.OrderBy(item => item.Key).SelectMany(item => item.Value).ToList();
        var anchorItems = _props.OrderBy(item => item.Key).Select(item =>
        {
            var counts = item.Value.Select(prop => Items(prop.Element("aTexData")).Count).ToArray();
            return new XElement("Item",
                new XElement("props", string.Join(" ", counts)),
                new XElement("anchor", ClothingConstants.AnchorNames.GetValueOrDefault(item.Key, $"ANCHOR_{item.Key}")));
        }).ToList();

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("CPedVariationInfo",
                new XAttribute("name", CollectionName),
                new XElement("bHasTexVariations", new XAttribute("value", "true")),
                new XElement("bHasDrawblVariations", new XAttribute("value", "true")),
                new XElement("bHasLowLODs", new XAttribute("value", "false")),
                new XElement("bIsSuperLOD", new XAttribute("value", "false")),
                new XElement("availComp", string.Join(" ", availComp)),
                new XElement("aComponentData3", new XAttribute("itemType", "CPVComponentData"), componentItems),
                new XElement("aSelectionSets", new XAttribute("itemType", "CPedSelectionSet")),
                new XElement("compInfos", new XAttribute("itemType", "CComponentInfo"),
                    _componentInfos.OrderBy(item => item.Key).SelectMany(item => item.Value)),
                new XElement("propInfo",
                    new XElement("numAvailProps", new XAttribute("value", propItems.Count)),
                    new XElement("aPropMetaData", new XAttribute("itemType", "CPedPropMetaData"), propItems),
                    new XElement("aAnchors", new XAttribute("itemType", "CAnchorProps"), anchorItems)),
                new XElement("dlcName", $"hash_{JenkHash.Hash(CollectionName):X8}")));
    }

    public Dictionary<int, int> GetComponentCounts()
        => _componentDrawables.ToDictionary(item => item.Key, item => item.Value.Count);

    public Dictionary<int, int> GetPropCounts()
        => _props.ToDictionary(item => item.Key, item => item.Value.Count);

    private static List<XElement> GetOrCreate(Dictionary<int, List<XElement>> dictionary, int key)
    {
        if (!dictionary.TryGetValue(key, out var value))
        {
            value = [];
            dictionary[key] = value;
        }

        return value;
    }
}
