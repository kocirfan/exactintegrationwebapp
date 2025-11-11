using System;
using System.Text.Json.Serialization;

public class Item
{
    public Guid? ID { get; set; }
    public string Code { get; set; }
    public string Barcode { get; set; }
    public string Description { get; set; }
    public string ExtraDescription { get; set; }
    public string Unit { get; set; }
    public string UnitType { get; set; }
    public string UnitDescription { get; set; }
    public Guid? ItemGroup { get; set; }
    public string ItemGroupCode { get; set; }
    public string ItemGroupDescription { get; set; }
    
    // KDV hesaplaması için - API'den gelmez, bizim hesaplamamız
    [JsonIgnore]
    public decimal? SalesVat { get; set; }
    
    public string SalesVatCode { get; set; }
    public double? Stock { get; set; }
    public double? AverageCost { get; set; }
    public string CostPriceCurrency { get; set; }
    public double? StandardSalesPrice { get; set; }
    public double? CostPriceStandard { get; set; }
    public string Notes { get; set; }
    public string SearchCode { get; set; }
    public byte[] Picture { get; set; }
    public string PictureName { get; set; }
    public string PictureUrl { get; set; }
}