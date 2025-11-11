using System;
using System.Collections.Generic;
using System.Xml.Serialization;

public class ItemData
{
    public List<Item> results { get; set; }
    public string __next { get; set; }
}