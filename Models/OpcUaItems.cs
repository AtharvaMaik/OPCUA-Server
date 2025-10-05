// Models/OpcUaItem.cs
using System;

namespace MtpSimulator.Models
{
    public class OpcUaItem
    {
        public string Id { get; set; }            // ExternalInterface ID attribute or Identifier attribute value
        public string Name { get; set; }          // Name attribute (or ID if missing)
        public string Namespace { get; set; }     // e.g., "urn:Honeywell:UA:UOC"
        public string Identifier { get; set; }    // Node identifier string (NodeId identifier)
        public string DataType { get; set; }      // e.g., "xs:string", "xs:int"
        public int Access { get; set; }           // 0..3 (No/Read/Write/ReadWrite)
        public string InitialValue { get; set; }  // optional initial value
    }
}
