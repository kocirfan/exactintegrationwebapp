using System;
using Newtonsoft.Json;
public class ExactOrderLine
{
    public Guid ID { get; set; }
    public Guid Item { get; set; }
    public string Description { get; set; }
    public double Quantity { get; set; }
    public double UnitPrice { get; set; }
    public double NetPrice { get; set; }
    public double Discount { get; set; }
    public double VATPercentage { get; set; }
    public string VATCode { get; set; }
    public string UnitCode { get; set; }
    public DateTime DeliveryDate { get; set; }
    public int Division { get; set; }
    public int? OrderNumber { get; set; }
    public Guid? OrderID { get; set; }
}
