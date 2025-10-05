// AmlLoader.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MtpSimulator.Models;

#if USE_AML_ENGINE
using Aml.Engine;
using Aml.Engine.Xml;
#endif

namespace MtpSimulator
{
    /// <summary>
    /// Loads AML and extracts ExternalInterface nodes describing OPC UA items.
    /// Attempt to use Aml.Engine if available; otherwise use XML parsing fallback.
    /// </summary>
    public static class AmlLoader
    {
        public static List<OpcUaItem> LoadOpcUaItemsFromAml(string path)
        {
            // Try to use AML Engine if referenced and working (the code below uses AmlProvider pattern).
            try
            {
#if USE_AML_ENGINE
                // If your Aml.Engine assembly exposes AmlProvider.Instance.LoadDocument(...) this will work.
                var doc = AmlProvider.Instance.LoadDocument(path);
                if (doc != null)
                {
                    // Different Aml.Engine versions expose different helpers.
                    // Attempt to get raw XML back from the doc (fallback will always work).
                    string xml = doc.ToString();
                    return ParseWithXDocument(xml);
                }
#endif
            }
            catch (Exception ex)
            {
                // if AML Engine fails, swallow and fallback to raw XML
                System.Diagnostics.Debug.WriteLine("AML Engine load failed: " + ex.Message);
            }

            // Fallback: plain XML parsing (robust).
            return ParseWithXDocument(File.ReadAllText(path));
        }

        private static List<OpcUaItem> ParseWithXDocument(string xml)
        {
            var doc = XDocument.Parse(xml);
            // Namespace agnostic search for ExternalInterface elements (they may be in default NS).
            var externalInterfaces = doc.Descendants().Where(e => e.Name.LocalName == "ExternalInterface");

            var list = new List<OpcUaItem>();
            foreach (var ei in externalInterfaces)
            {
                // ID attribute on ExternalInterface element
                var idAttr = (string)ei.Attribute("ID") ?? (string)ei.Attribute("Id");
                var nameAttr = (string)ei.Attribute("Name") ?? (string)ei.Attribute("name");

                // Find child Attribute elements under ExternalInterface
                // <Attribute Name="Identifier"> <Value>R0011</Value> ...
                string identifier = null, ns = null, dataType = null, value = null;
                int access = 0;

                foreach (var attr in ei.Elements().Where(x => x.Name.LocalName == "Attribute"))
                {
                    var attrName = (string)attr.Attribute("Name");
                    var valElem = attr.Elements().FirstOrDefault(x => x.Name.LocalName == "Value");
                    var attrValue = valElem != null ? valElem.Value.Trim() : (string)attr.Attribute("Value") ?? string.Empty;

                    if (string.IsNullOrEmpty(attrName)) continue;
                    switch (attrName)
                    {
                        case "Identifier":
                            identifier = attrValue;
                            break;
                        case "Namespace":
                            ns = attrValue;
                            break;
                        case "Access":
                            if (int.TryParse(attrValue, out var a)) access = a;
                            break;
                        case "AttributeDataType":
                            dataType = attrValue;
                            break;
                        case "Value":
                            value = attrValue;
                            break;
                    }
                }

                // If AttributeDataType not present, maybe there's an attribute "AttributeDataType" on ExternalInterface?
                if (string.IsNullOrEmpty(dataType))
                {
                    // try a child <Attribute Name="AttributeDataType"><Value>xs:string</Value></Attribute>
                    var dtEl = ei.Elements().FirstOrDefault(x => (string)x.Attribute("Name") == "AttributeDataType");
                    if (dtEl != null)
                        dataType = (dtEl.Element(dtEl.GetDefaultNamespace() + "Value")?.Value) ?? dtEl.Value;
                }

                var item = new OpcUaItem
                {
                    Id = idAttr ?? identifier ?? Guid.NewGuid().ToString(),
                    Name = nameAttr ?? identifier ?? idAttr,
                    Identifier = identifier ?? idAttr,
                    Namespace = ns ?? "urn:Default",
                    DataType = dataType ?? "xs:string",
                    Access = access,
                    InitialValue = value ?? string.Empty
                };

                list.Add(item);
            }

            return list;
        }
    }
}
