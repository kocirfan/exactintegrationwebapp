using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Text.Json.Serialization;

public class ExactOrder
{
    public double AmountDC { get; set; }
    public double AmountDiscount { get; set; }
    public double Discount { get; set; }
    public double AmountDiscountExclVat { get; set; }
    public double AmountFC { get; set; }
    public double AmountFCExclVat { get; set; }
    public Guid? OrderID { get; set; }
    public Guid OrderedBy { get; set; }
    public Guid DeliverTo { get; set; }
    public Guid InvoiceTo { get; set; }

    public Guid? ShippingMethod { get; set; }
    
    // ExactOnline için string formatında tarih gerekli
    public DateTime OrderDate { get; set; }
    public DateTime? DeliveryDate { get; set; }  // Teslimat tarihi

    public string Description { get; set; }
    public int? OrderNumber { get; set; }
    public string Currency { get; set; }
    public string YourRef { get; set; }
    public Guid? DeliveryAddress { get; set; }
    public int Status { get; set; }
    public int Division { get; set; }
    
    // Nullable yapıldı
    public Guid? WarehouseID { get; set; }
    
    // Nullable yapıldı - eğer config'de yoksa null olacak; null ise JSON'a dahil etme
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? Salesperson { get; set; }

    // Kurumsal siparişlerde firmanın ilgili kişisi (crm/Contacts GUID); null ise JSON'a dahil etme
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? OrderedByContactPerson { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DeliverToContactPerson { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? InvoiceToContactPerson { get; set; }
    
    public List<ExactOrderLine> SalesOrderLines { get; set; }
}