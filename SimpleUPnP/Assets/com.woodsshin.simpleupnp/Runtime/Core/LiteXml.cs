// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WoodsShin.SimpleUPNP
{
    /// <summary>One element of a <see cref="LiteXml"/> document. Namespace prefixes are dropped from names.</summary>
    internal sealed class XmlNodeLite
    {
        private readonly StringBuilder _text = new StringBuilder();

        public XmlNodeLite(string name, XmlNodeLite parent)
        {
            Name = name;
            Parent = parent;
        }

        /// <summary>Local name of the element (no prefix).</summary>
        public string Name { get; }

        public XmlNodeLite Parent { get; }

        public List<XmlNodeLite> Children { get; } = new List<XmlNodeLite>();

        /// <summary>Text that sits directly inside this element, trimmed.</summary>
        public string Text
        {
            get { return _text.ToString().Trim(); }
        }

        internal void AppendText(string text)
        {
            _text.Append(text);
        }

        /// <summary>First direct child with the given name (case-insensitive), or null.</summary>
        public XmlNodeLite Child(string name)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                if (string.Equals(Children[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return Children[i];
                }
            }

            return null;
        }

        /// <summary>Text of the first direct child with the given name, or null.</summary>
        public string ChildText(string name)
        {
            XmlNodeLite child = Child(name);
            return child == null ? null : child.Text;
        }

        /// <summary>First element below this one (depth first, document order) with the given name, or null.</summary>
        public XmlNodeLite Descendant(string name)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                XmlNodeLite child = Children[i];
                if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }

                XmlNodeLite deeper = child.Descendant(name);
                if (deeper != null)
                {
                    return deeper;
                }
            }

            return null;
        }

        /// <summary>Text of the first element below this one with the given name, or null.</summary>
        public string DescendantText(string name)
        {
            XmlNodeLite node = Descendant(name);
            return node == null ? null : node.Text;
        }

        /// <summary>Every element below this one with the given name, in document order.</summary>
        public List<XmlNodeLite> Descendants(string name)
        {
            List<XmlNodeLite> result = new List<XmlNodeLite>();
            CollectDescendants(name, result);
            return result;
        }

        private void CollectDescendants(string name, List<XmlNodeLite> result)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                XmlNodeLite child = Children[i];
                if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(child);
                }

                child.CollectDescendants(name, result);
            }
        }
    }

    /// <summary>
    /// A small, forgiving XML reader for the documents UPnP routers send. Many routers produce XML that strict
    /// parsers reject (unescaped characters, wrong case, missing end tags), so this reader never throws on bad
    /// input. It does not expand entity declarations and skips DOCTYPE sections, so hostile documents cannot
    /// trigger external requests or entity expansion attacks.
    /// </summary>
    internal static class LiteXml
    {
        private const int MaxNodes = 20000;
        private const int MaxDepth = 64;

        /// <summary>Parses the text and returns a synthetic root element named "#document". Never returns null.</summary>
        public static XmlNodeLite Parse(string text)
        {
            XmlNodeLite root = new XmlNodeLite("#document", null);
            if (string.IsNullOrEmpty(text))
            {
                return root;
            }

            // Some routers prepend a byte order mark or whitespace.
            int start = 0;
            while (start < text.Length && (text[start] == '\uFEFF' || char.IsWhiteSpace(text[start])))
            {
                start++;
            }

            XmlNodeLite current = root;
            int depth = 0;
            int nodeCount = 0;
            int i = start;
            int n = text.Length;

            while (i < n)
            {
                if (text[i] != '<')
                {
                    int next = text.IndexOf('<', i);
                    if (next < 0)
                    {
                        next = n;
                    }

                    if (current != root)
                    {
                        current.AppendText(Decode(text.Substring(i, next - i)));
                    }

                    i = next;
                    continue;
                }

                if (StartsWith(text, i, "<!--"))
                {
                    int end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        break;
                    }

                    i = end + 3;
                    continue;
                }

                if (StartsWith(text, i, "<![CDATA["))
                {
                    int end = text.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                    int stop = end < 0 ? n : end;
                    if (current != root)
                    {
                        current.AppendText(text.Substring(i + 9, stop - (i + 9)));
                    }

                    i = end < 0 ? n : end + 3;
                    continue;
                }

                if (StartsWith(text, i, "<?"))
                {
                    int end = text.IndexOf("?>", i + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        break;
                    }

                    i = end + 2;
                    continue;
                }

                if (StartsWith(text, i, "<!"))
                {
                    i = SkipDeclaration(text, i);
                    continue;
                }

                if (i + 1 < n && text[i + 1] == '/')
                {
                    int end = text.IndexOf('>', i);
                    if (end < 0)
                    {
                        break;
                    }

                    string closing = LocalName(text.Substring(i + 2, end - i - 2).Trim());

                    // Close the nearest open element with this name. A stray end tag is ignored.
                    XmlNodeLite walker = current;
                    int levels = 0;
                    while (walker != root && !string.Equals(walker.Name, closing, StringComparison.OrdinalIgnoreCase))
                    {
                        walker = walker.Parent;
                        levels++;
                    }

                    if (walker != root)
                    {
                        current = walker.Parent;
                        depth -= levels + 1;
                    }

                    i = end + 1;
                    continue;
                }

                int tagEnd = FindTagEnd(text, i);
                if (tagEnd < 0)
                {
                    break;
                }

                bool selfClosing = text[tagEnd - 1] == '/';
                int innerLength = tagEnd - i - 1 - (selfClosing ? 1 : 0);
                string inner = innerLength > 0 ? text.Substring(i + 1, innerLength) : string.Empty;
                string name = LocalName(ReadName(inner));

                if (name.Length > 0)
                {
                    if (++nodeCount > MaxNodes)
                    {
                        break;
                    }

                    XmlNodeLite node = new XmlNodeLite(name, current);
                    current.Children.Add(node);

                    if (!selfClosing)
                    {
                        if (depth >= MaxDepth)
                        {
                            break;
                        }

                        current = node;
                        depth++;
                    }
                }

                i = tagEnd + 1;
            }

            return root;
        }

        private static bool StartsWith(string text, int index, string value)
        {
            return string.CompareOrdinal(text, index, value, 0, value.Length) == 0;
        }

        /// <summary>Skips a "&lt;!DOCTYPE ...&gt;" style declaration including an internal subset.</summary>
        private static int SkipDeclaration(string text, int index)
        {
            int bracketDepth = 0;
            for (int k = index + 2; k < text.Length; k++)
            {
                char c = text[k];
                if (c == '[')
                {
                    bracketDepth++;
                }
                else if (c == ']')
                {
                    bracketDepth--;
                }
                else if (c == '>' && bracketDepth <= 0)
                {
                    return k + 1;
                }
            }

            return text.Length;
        }

        /// <summary>Index of the '&gt;' that ends the tag starting at <paramref name="index"/>, ignoring '&gt;' inside quotes.</summary>
        private static int FindTagEnd(string text, int index)
        {
            char quote = '\0';
            for (int k = index + 1; k < text.Length; k++)
            {
                char c = text[k];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                }
                else if (c == '>')
                {
                    return k;
                }
            }

            // Unbalanced quotes: fall back to the first '>'.
            return text.IndexOf('>', index);
        }

        private static string ReadName(string inner)
        {
            int length = 0;
            while (length < inner.Length && !char.IsWhiteSpace(inner[length]) && inner[length] != '/')
            {
                length++;
            }

            return inner.Substring(0, length);
        }

        private static string LocalName(string name)
        {
            int colon = name.LastIndexOf(':');
            return colon >= 0 ? name.Substring(colon + 1) : name;
        }

        /// <summary>Decodes the predefined entities and numeric character references. Anything else is kept as is.</summary>
        internal static string Decode(string text)
        {
            if (text.IndexOf('&') < 0)
            {
                return text;
            }

            StringBuilder result = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c != '&')
                {
                    result.Append(c);
                    i++;
                    continue;
                }

                int semicolon = text.IndexOf(';', i + 1);
                if (semicolon < 0 || semicolon - i > 10)
                {
                    result.Append(c);
                    i++;
                    continue;
                }

                string entity = text.Substring(i + 1, semicolon - i - 1);
                string replacement = null;
                switch (entity)
                {
                    case "amp":
                        replacement = "&";
                        break;
                    case "lt":
                        replacement = "<";
                        break;
                    case "gt":
                        replacement = ">";
                        break;
                    case "quot":
                        replacement = "\"";
                        break;
                    case "apos":
                        replacement = "'";
                        break;
                    default:
                        replacement = DecodeNumeric(entity);
                        break;
                }

                if (replacement == null)
                {
                    result.Append(c);
                    i++;
                }
                else
                {
                    result.Append(replacement);
                    i = semicolon + 1;
                }
            }

            return result.ToString();
        }

        private static string DecodeNumeric(string entity)
        {
            if (entity.Length < 2 || entity[0] != '#')
            {
                return null;
            }

            int codePoint;
            bool ok;
            if (entity[1] == 'x' || entity[1] == 'X')
            {
                ok = int.TryParse(entity.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out codePoint);
            }
            else
            {
                ok = int.TryParse(entity.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out codePoint);
            }

            if (!ok || codePoint <= 0 || codePoint > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
            {
                return null;
            }

            return char.ConvertFromUtf32(codePoint);
        }

        /// <summary>Escapes text for use inside an XML element.</summary>
        internal static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            StringBuilder result = null;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                string replacement = null;
                switch (c)
                {
                    case '&':
                        replacement = "&amp;";
                        break;
                    case '<':
                        replacement = "&lt;";
                        break;
                    case '>':
                        replacement = "&gt;";
                        break;
                    case '"':
                        replacement = "&quot;";
                        break;
                    case '\'':
                        replacement = "&apos;";
                        break;
                    default:
                        // Characters that are not allowed in XML 1.0 are dropped.
                        if (c < ' ' && c != '\t' && c != '\n' && c != '\r')
                        {
                            replacement = string.Empty;
                        }

                        break;
                }

                if (replacement != null)
                {
                    if (result == null)
                    {
                        result = new StringBuilder(text.Length + 16);
                        result.Append(text, 0, i);
                    }

                    result.Append(replacement);
                }
                else if (result != null)
                {
                    result.Append(c);
                }
            }

            return result == null ? text : result.ToString();
        }
    }
}
