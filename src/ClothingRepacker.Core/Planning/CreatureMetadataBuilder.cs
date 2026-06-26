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
            _seenComponentExpressions);

        AddExpressions(
            metadata.PropExpressions,
            propMappings,
            expression => expression.SlotId,
            mapping => mapping.AnchorId,
            mapping => mapping.OldPropIndex,
            mapping => mapping.NewPropIndex,
            "pedPropVarIndex",
            _propExpressions,
            _seenPropExpressions);
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
        HashSet<string> seen)
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
                AddUnique(target, seen, new XElement(expression.Element));
                continue;
            }

            foreach (var mapping in slotMappings.Where(mapping => oldIndex(mapping) == expression.VariationIndex))
            {
                var clone = new XElement(expression.Element);
                SetValueAttr(RequiredElement(clone, varIndexElementName), newIndex(mapping));
                AddUnique(target, seen, clone);
            }
        }
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
