using System.Globalization;
using System.Xml.Linq;

namespace ClothingRepacker.Core.Xml;

public static class XmlHelpers
{
    public static int ValueAttr(XElement element)
        => int.Parse(RequiredAttribute(element, "value").Value, CultureInfo.InvariantCulture);

    public static void SetValueAttr(XElement element, int value)
        => element.SetAttributeValue("value", value.ToString(CultureInfo.InvariantCulture));

    public static string ValueText(XElement element)
        => element.Value.Trim();

    public static List<XElement> Items(XElement? container)
        => container?.Elements("Item").ToList() ?? [];

    public static int[] ParseIntList(string text)
        => text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(v => int.Parse(v, CultureInfo.InvariantCulture))
            .ToArray();

    public static XElement RequiredElement(XElement parent, string name)
        => parent.Element(name) ?? throw new InvalidDataException($"Missing element '{name}'.");

    public static XAttribute RequiredAttribute(XElement element, string name)
        => element.Attribute(name) ?? throw new InvalidDataException($"Missing attribute '{name}' on '{element.Name}'.");
}
