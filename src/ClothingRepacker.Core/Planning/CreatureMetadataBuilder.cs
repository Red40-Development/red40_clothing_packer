using System.Xml.Linq;
using ClothingRepacker.Core.Models;
using ClothingRepacker.Core.Xml;
using static ClothingRepacker.Core.Xml.XmlHelpers;

namespace ClothingRepacker.Core.Planning;

public sealed class CreatureMetadataBuilder
{
    private readonly List<XElement> _shaderVariableComponents = [];
    private readonly List<XElement> _componentExpressions = [];
    private readonly List<XElement> _propExpressions = [];
    private readonly HashSet<string> _seenShaderVariableComponents = [];
    private readonly HashSet<string> _seenComponentExpressions = [];
    private readonly HashSet<string> _seenPropExpressions = [];
    private readonly HashSet<string> _seenComponentExpressionSlots = [];
    private readonly HashSet<string> _seenPropExpressionSlots = [];

    public void Add(SourceCreatureMetadata metadata, IReadOnlyList<DrawableMapping> drawableMappings, IReadOnlyList<PropMapping> propMappings)
    {
        foreach (var shaderVariableComponent in metadata.ShaderVariableComponents)
        {
            if (drawableMappings.Any(mapping => mapping.ComponentId == shaderVariableComponent.ComponentId))
            {
                AddUnique(_shaderVariableComponents, _seenShaderVariableComponents, new XElement(shaderVariableComponent.Element));
            }
        }

        AddExpressions(
            metadata.ComponentExpressions,
            drawableMappings,
            expression => expression.SlotId,
            mapping => mapping.ComponentId,
            mapping => mapping.OldDrawableIndex,
            mapping => mapping.NewDrawableIndex,
            "pedCompVarIndex",
            _componentExpressions,
            _seenComponentExpressions,
            _seenComponentExpressionSlots);

        AddExpressions(
            metadata.PropExpressions,
            propMappings,
            expression => expression.SlotId,
            mapping => mapping.AnchorId,
            mapping => mapping.OldPropIndex,
            mapping => mapping.NewPropIndex,
            "pedPropVarIndex",
            _propExpressions,
            _seenPropExpressions,
            _seenPropExpressionSlots);
    }

    public void AddRepairHints(SourceYmt source, IReadOnlyList<DrawableMapping> drawableMappings, IReadOnlyList<PropMapping> propMappings)
    {
        foreach (var hint in source.CreatureComponentRepairHints)
        {
            foreach (var mapping in drawableMappings.Where(mapping => mapping.ComponentId == hint.ComponentId && mapping.OldDrawableIndex == hint.DrawableIndex))
            {
                AddUniqueExpression(
                    _componentExpressions,
                    _seenComponentExpressions,
                    _seenComponentExpressionSlots,
                    hint.ComponentId,
                    mapping.NewDrawableIndex,
                    BuildHighHeelExpression(mapping.NewDrawableIndex));
            }
        }

        var propRepairMappings = source.CreaturePropRepairHints
            .SelectMany(hint => propMappings.Where(mapping => mapping.AnchorId == hint.AnchorId && mapping.OldPropIndex == hint.PropIndex)
                .Select(mapping => new { Hint = hint, Mapping = mapping }))
            .ToList();
        if (propRepairMappings.Count > 0)
        {
            AddUniqueExpression(
                _propExpressions,
                _seenPropExpressions,
                _seenPropExpressionSlots,
                0,
                -1,
                BuildBaselinePropExpression());
        }

        foreach (var repairMapping in propRepairMappings)
        {
            AddUniqueExpression(
                _propExpressions,
                _seenPropExpressions,
                _seenPropExpressionSlots,
                repairMapping.Hint.AnchorId,
                repairMapping.Mapping.NewPropIndex,
                BuildHairScalePropExpression(repairMapping.Mapping.NewPropIndex));
        }
    }

    public XDocument BuildXml()
        => new(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("CCreatureMetaData",
                new XElement("shaderVariableComponents", new XAttribute("itemType", "CShaderVariableComponent"), _shaderVariableComponents),
                new XElement("pedPropExpressions", new XAttribute("itemType", "CPedPropExpressionData"), _propExpressions),
                new XElement("pedCompExpressions", new XAttribute("itemType", "CPedCompExpressionData"), _componentExpressions)));

    private static void AddExpressions<TMapping>(
        IReadOnlyList<CreatureExpression> expressions,
        IReadOnlyList<TMapping> mappings,
        Func<CreatureExpression, int> expressionSlot,
        Func<TMapping, int> mappingSlot,
        Func<TMapping, int> oldIndex,
        Func<TMapping, int> newIndex,
        string varIndexElementName,
        List<XElement> target,
        HashSet<string> seen,
        HashSet<string> seenSlots)
    {
        var mappingsBySlot = mappings
            .GroupBy(mappingSlot)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var expression in expressions)
        {
            if (!mappingsBySlot.TryGetValue(expressionSlot(expression), out var slotMappings))
            {
                continue;
            }

            if (expression.VariationIndex < 0)
            {
                AddUniqueExpression(target, seen, seenSlots, expressionSlot(expression), expression.VariationIndex, new XElement(expression.Element));
                continue;
            }

            foreach (var mapping in slotMappings.Where(mapping => oldIndex(mapping) == expression.VariationIndex))
            {
                var clone = new XElement(expression.Element);
                var remappedIndex = newIndex(mapping);
                SetValueAttr(RequiredElement(clone, varIndexElementName), remappedIndex);
                AddUniqueExpression(target, seen, seenSlots, expressionSlot(expression), remappedIndex, clone);
            }
        }
    }

    private static void AddUniqueExpression(
        List<XElement> target,
        HashSet<string> seen,
        HashSet<string> seenSlots,
        int slotId,
        int variationIndex,
        XElement element)
    {
        var slotKey = $"{slotId}:{variationIndex}";
        if (!seenSlots.Add(slotKey))
        {
            return;
        }

        AddUnique(target, seen, element);
    }

    private static void AddUnique(List<XElement> target, HashSet<string> seen, XElement element)
    {
        var key = element.ToString(SaveOptions.DisableFormatting);
        if (seen.Add(key))
        {
            target.Add(element);
        }
    }

    private static XElement BuildHighHeelExpression(int drawableIndex)
        => new("Item",
            new XElement("pedCompID", new XAttribute("value", 6)),
            new XElement("pedCompVarIndex", new XAttribute("value", drawableIndex)),
            new XElement("pedCompExpressionIndex", new XAttribute("value", 4)),
            new XElement("tracks", new XAttribute("content", "char_array"), 33),
            new XElement("ids", new XAttribute("content", "short_array"), 28462),
            new XElement("types", new XAttribute("content", "char_array"), 2),
            new XElement("components", new XAttribute("content", "char_array"), 1));

    private static XElement BuildBaselinePropExpression()
        => BuildHairScalePropExpression(-1, uint.MaxValue);

    private static XElement BuildHairScalePropExpression(int propIndex, uint expressionIndex = 0)
        => new("Item",
            new XElement("pedPropID", new XAttribute("value", 0)),
            new XElement("pedPropVarIndex", new XAttribute("value", propIndex)),
            new XElement("pedPropExpressionIndex", new XAttribute("value", expressionIndex)),
            new XElement("tracks", new XAttribute("content", "char_array"), 33),
            new XElement("ids", new XAttribute("content", "short_array"), 13201),
            new XElement("types", new XAttribute("content", "char_array"), 2),
            new XElement("components", new XAttribute("content", "char_array"), 1));
}
