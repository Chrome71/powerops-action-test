using System.Xml;
using System.Xml.Linq;

using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace PowerOps.Core.Readers;

/// <summary>
/// Turns the YAML layout back into the XML shape of the solution files: `'@name'` is an attribute, `'#text'` the element text,
/// a list is one element per item. Read as an event stream so anchors and aliases can be refused instead of expanded.
/// </summary>
internal static class YamlXml
{
    /// <returns>A `yaml` element whose children are the document's top-level keys.</returns>
    /// <exception cref="InvalidDataException">The YAML is malformed, uses anchors or aliases, or is nested too deep.</exception>
    public static XElement Convert(string yaml)
    {
        var root = new XElement("yaml");
        try
        {
            var parser = new Parser(new StringReader(yaml));
            Next(parser); // StreamStart
            if (Next(parser) is DocumentStart && Next(parser) is MappingStart)
            {
                Mapping(parser, root, 1);
            }
        }
        // XAttribute and XElement throw for reserved names such as `'@xmlns'`.
        catch (Exception e) when (e is YamlException or ArgumentException or XmlException)
        {
            throw new InvalidDataException(e.Message, e);
        }

        return root;
    }

    private static void Mapping(Parser parser, XElement element, int depth)
    {
        for (var ev = Next(parser); ev is not MappingEnd; ev = Next(parser))
        {
            if (ev is not Scalar key)
            {
                throw new InvalidDataException("YAML keys must be plain text.");
            }

            var value = Next(parser);
            if (key.Value.StartsWith('@') || key.Value == "#text")
            {
                if (value is not Scalar text)
                {
                    throw new InvalidDataException($"YAML key '{key.Value}' must have a plain text value.");
                }

                if (key.Value == "#text")
                {
                    element.Add(text.Value);
                }
                else if (!key.Value.Contains(':'))
                {
                    element.SetAttributeValue(Name(key.Value[1..]), text.Value);
                }
            }
            else
            {
                Node(parser, element, Name(key.Value), value, depth);
            }
        }
    }

    private static void Node(Parser parser, XElement parent, string name, ParsingEvent ev, int depth)
    {
        if (depth > SolutionParser.MaxXmlDepth)
        {
            throw new InvalidDataException($"YAML is nested deeper than {SolutionParser.MaxXmlDepth} levels.");
        }

        switch (ev)
        {
            case Scalar s:
                parent.Add(new XElement(name, s.Value));
                break;
            case MappingStart:
                var child = new XElement(name);
                parent.Add(child);
                Mapping(parser, child, depth + 1);
                break;
            case SequenceStart:
                for (var item = Next(parser); item is not SequenceEnd; item = Next(parser))
                {
                    Node(parser, parent, name, item, depth + 1);
                }

                break;
            default:
                throw new InvalidDataException("Unexpected YAML content.");
        }
    }

    private static ParsingEvent Next(Parser parser)
    {
        if (!parser.MoveNext())
        {
            throw new InvalidDataException("Unexpected end of YAML.");
        }

        var ev = parser.Current!;
        if (ev is AnchorAlias || (ev is NodeEvent node && !node.Anchor.IsEmpty))
        {
            throw new InvalidDataException("YAML anchors and aliases are not supported.");
        }

        return ev;
    }

    private static string Name(string key) =>
        key.Length == 0 ? throw new InvalidDataException("YAML keys must not be empty.") : XmlConvert.EncodeLocalName(key);
}