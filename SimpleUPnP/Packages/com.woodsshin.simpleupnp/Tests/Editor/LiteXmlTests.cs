// Copyright 2017-present WoodsShin, Inc. All Rights Reserved.

using System.Text;
using NUnit.Framework;

namespace WoodsShin.SimpleUPNP.Tests
{
    [TestFixture]
    public class LiteXmlTests
    {
        [Test]
        public void ParsesNestedElementsAndStripsPrefixes()
        {
            XmlNodeLite root = LiteXml.Parse(
                "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"x\"><s:Body><u:Go xmlns:u=\"y\"><A>1</A><B> two </B></u:Go></s:Body></s:Envelope>");

            XmlNodeLite go = root.Descendant("Go");
            Assert.IsNotNull(go);
            Assert.AreEqual("1", go.ChildText("A"));
            Assert.AreEqual("two", go.ChildText("B"), "text is trimmed");
            Assert.AreEqual("Envelope", root.Children[0].Name);
        }

        [Test]
        public void DecodesEntitiesAndCData()
        {
            XmlNodeLite root = LiteXml.Parse("<a><b>Tom &amp; Jerry &lt;3 &#65;&#x42; &quot;q&quot;</b><c><![CDATA[<raw & text>]]></c></a>");
            Assert.AreEqual("Tom & Jerry <3 AB \"q\"", root.DescendantText("b"));
            Assert.AreEqual("<raw & text>", root.DescendantText("c"));
        }

        [Test]
        public void ToleratesBrokenMarkup()
        {
            // Unclosed element, stray end tag, unescaped ampersand and an unterminated comment.
            XmlNodeLite root = LiteXml.Parse("<root><name>Fast & Free</name></stray><open><x>1</x><!-- never closed");
            Assert.AreEqual("Fast & Free", root.DescendantText("name"));
            Assert.AreEqual("1", root.DescendantText("x"));

            Assert.IsNotNull(LiteXml.Parse(null));
            Assert.IsNotNull(LiteXml.Parse(string.Empty));
            Assert.IsNotNull(LiteXml.Parse("<"));
            Assert.IsNotNull(LiteXml.Parse("<a b=\"unterminated"));
            Assert.IsNotNull(LiteXml.Parse("plain text without markup"));
        }

        [Test]
        public void IgnoresDoctypeAndDoesNotExpandEntities()
        {
            XmlNodeLite root = LiteXml.Parse(
                "<?xml version=\"1.0\"?><!DOCTYPE lol [<!ENTITY a \"boom\"><!ENTITY b \"&a;&a;&a;\">]><x>&b;</x>");
            string text = root.DescendantText("x");
            StringAssert.DoesNotContain("boom", text, "entity declarations must never be expanded");
            Assert.AreEqual("&b;", text, "unknown entities stay as they are");
        }

        [Test]
        public void LookupIsCaseInsensitive()
        {
            XmlNodeLite root = LiteXml.Parse("<Root><ModelNumber>7</ModelNumber></Root>");
            Assert.AreEqual("7", root.DescendantText("modelnumber"));
            Assert.AreEqual("7", root.Descendant("root").ChildText("MODELNUMBER"));
        }

        [Test]
        public void ReturnsAllDescendantsInDocumentOrder()
        {
            XmlNodeLite root = LiteXml.Parse("<l><service><id>1</id></service><d><service><id>2</id></service></d><service><id>3</id></service></l>");
            var services = root.Descendants("service");
            Assert.AreEqual(3, services.Count);
            Assert.AreEqual("1", services[0].ChildText("id"));
            Assert.AreEqual("2", services[1].ChildText("id"));
            Assert.AreEqual("3", services[2].ChildText("id"));
        }

        [Test]
        public void SelfClosingElementsHaveNoChildren()
        {
            XmlNodeLite root = LiteXml.Parse("<a><b/><c x=\"1\" /><d>t</d></a>");
            Assert.AreEqual(3, root.Descendant("a").Children.Count);
            Assert.AreEqual("t", root.DescendantText("d"));
            Assert.AreEqual(string.Empty, root.DescendantText("b"));
        }

        [Test]
        public void GreaterThanInsideAttributeValueDoesNotEndTheTag()
        {
            XmlNodeLite root = LiteXml.Parse("<a title=\"x > y\"><b>ok</b></a>");
            Assert.AreEqual("ok", root.DescendantText("b"));
        }

        [Test]
        public void LimitsDepthAndNodeCountWithoutThrowing()
        {
            StringBuilder deep = new StringBuilder();
            for (int i = 0; i < 5000; i++)
            {
                deep.Append("<n>");
            }

            Assert.IsNotNull(LiteXml.Parse(deep.ToString()));

            StringBuilder wide = new StringBuilder("<r>");
            for (int i = 0; i < 100000; i++)
            {
                wide.Append("<i>1</i>");
            }

            XmlNodeLite root = LiteXml.Parse(wide.ToString());
            Assert.Less(root.Descendant("r").Children.Count, 20001, "the node limit applies");
        }

        [Test]
        public void EscapeProducesTextThatDecodesBack()
        {
            string original = "A&B <tag> \"quote\" 'apos' end";
            string escaped = LiteXml.Escape(original);
            StringAssert.DoesNotContain("<", escaped);
            Assert.AreEqual(original, LiteXml.Decode(escaped));
            Assert.AreEqual(string.Empty, LiteXml.Escape(null));
            Assert.AreEqual("plain", LiteXml.Escape("plain"));
            Assert.AreEqual("ab", LiteXml.Escape("a\u0001b"), "control characters are dropped");
        }
    }
}
