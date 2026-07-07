using System.Globalization;
using System.Xml.Linq;

namespace ClothingRepacker.Core.Xml;

public static class XmlHelpers
{
    public static int ValueAttr(XElement element)
        => ParseIntValue(RequiredAttribute(element, "value").Value);

    public static void SetValueAttr(XElement element, int value)
        => element.SetAttributeValue("value", value.ToString(CultureInfo.InvariantCulture));

    public static List<XElement> Items(XElement? container)
        => container?.Elements("Item").ToList() ?? [];

    public static int[] ParseIntList(string text)
        => text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseIntValue)
            .ToArray();

    public static int ParseIntValue(string text)
    {
        if (TryParseIntValue(text, out var value))
        {
            return value;
        }

        throw new FormatException($"Could not parse integer value '{text}'.");
    }

    public static bool TryParseIntValue(string text, out int value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexValue))
            {
                value = unchecked((int)hexValue);
                return true;
            }

            value = 0;
            return false;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsignedValue))
        {
            value = unchecked((int)unsignedValue);
            return true;
        }

        return false;
    }

    public static XElement RequiredElement(XElement parent, string name)
        => parent.Element(name) ?? throw new InvalidDataException($"Missing element '{name}'.");

    public static XAttribute RequiredAttribute(XElement element, string name)
        => element.Attribute(name) ?? throw new InvalidDataException($"Missing attribute '{name}' on '{element.Name}'.");
}
