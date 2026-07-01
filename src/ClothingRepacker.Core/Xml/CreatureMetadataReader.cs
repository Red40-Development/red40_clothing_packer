using ClothingRepacker.Core.Models;
using System.Xml.Linq;
using static ClothingRepacker.Core.Xml.XmlHelpers;

namespace ClothingRepacker.Core.Xml;

public sealed class CreatureMetadataReader
{
    public SourceCreatureMetadata Read(XDocument xml, string path, string resourceName, string resourceRoot)
    {
        var root = xml.Root ?? throw new InvalidDataException("Missing XML root.");
        if (root.Name.LocalName != "CCreatureMetaData")
        {
            throw new InvalidDataException($"Unexpected root '{root.Name}'.");
        }

        return new SourceCreatureMetadata(
            path,
            resourceName,
            resourceRoot,
            xml,
            Items(root.Element("shaderVariableComponents"))
                .Where(item => TryGetValue(item, "pedcompID", out _))
                .Select(item => new CreatureShaderVariableComponent(GetValue(item, "pedcompID"), new XElement(item)))
                .ToList(),
            Items(root.Element("pedCompExpressions"))
                .Where(item => TryGetValue(item, "pedCompID", out _) && TryGetValue(item, "pedCompVarIndex", out _))
                .Select(item => new CreatureExpression(GetValue(item, "pedCompID"), GetValue(item, "pedCompVarIndex"), new XElement(item)))
                .ToList(),
            Items(root.Element("pedPropExpressions"))
                .Where(item => TryGetValue(item, "pedPropID", out _) && TryGetValue(item, "pedPropVarIndex", out _))
                .Select(item => new CreatureExpression(GetValue(item, "pedPropID"), GetValue(item, "pedPropVarIndex"), new XElement(item)))
                .ToList());
    }

    private static int GetValue(XElement item, string name)
        => ValueAttr(RequiredElement(item, name));

    private static bool TryGetValue(XElement item, string name, out int value)
    {
        value = 0;
        var attribute = item.Element(name)?.Attribute("value");
        return attribute is not null && XmlHelpers.TryParseIntValue(attribute.Value, out value);
    }
}
