using System;
using System.Globalization;
using System.Text.RegularExpressions;
// using Newtonsoft.Json;
using System.Text.Json.Serialization;


public class ExactProduct
{
    [JsonIgnore]
    public string Metadata { get; set; }

    [JsonPropertyName("IsSerialNumberItem")]
    public string IsSerialNumberItem { get; set; }

    [JsonPropertyName("IsBatchNumberItem")]
    public int? IsBatchNumberItem { get; set; }

    [JsonPropertyName("ID")]
    public Guid ID { get; set; }

    [JsonPropertyName("StandardSalesPrice")]
    public decimal? StandardSalesPrice { get; set; }

    [JsonPropertyName("Class_01")]
    public string Class_01 { get; set; }
    [JsonPropertyName("Class_02")]
    public string Class_02 { get; set; }
    [JsonPropertyName("Class_03")]
    public string Class_03 { get; set; }
    [JsonPropertyName("Class_04")]
    public string Class_04 { get; set; }
    [JsonPropertyName("Class_05")]
    public string Class_05 { get; set; }
    [JsonPropertyName("Class_06")]
    public string Class_06 { get; set; }
    [JsonPropertyName("Class_07")]
    public string Class_07 { get; set; }
    [JsonPropertyName("Class_08")]
    public string Class_08 { get; set; }
    [JsonPropertyName("Class_09")]
    public string Class_09 { get; set; }
    [JsonPropertyName("Class_10")]
    public string Class_10 { get; set; }

    [JsonPropertyName("Code")]
    public string Code { get; set; }

    [JsonPropertyName("CopyRemarks")]
    public int? CopyRemarks { get; set; }

    [JsonPropertyName("CostPriceCurrency")]
    public string CostPriceCurrency { get; set; }

    [JsonPropertyName("CostPriceNew")]
    public string CostPriceNew { get; set; }

    [JsonPropertyName("CostPriceStandard")]
    public decimal? CostPriceStandard { get; set; }

    [JsonPropertyName("AverageCost")]
    public string AverageCost { get; set; }

    [JsonPropertyName("Barcode")]
    public string Barcode { get; set; }

    // Raw date strings as returned by Exact: "/Date(1390511122233)/"
    [JsonPropertyName("Created")]
    public string CreatedRaw { get; set; }

    [JsonIgnore]
    public DateTimeOffset? Created => ParseExactDate(CreatedRaw);

    [JsonPropertyName("CreatorFullName")]
    public string CreatorFullName { get; set; }

    [JsonPropertyName("Creator")]
    public Guid? Creator { get; set; }

    [JsonPropertyName("CustomField")]
    public string CustomField { get; set; }

    [JsonPropertyName("Description")]
    public string Description { get; set; }

    [JsonPropertyName("Division")]
    public int? Division { get; set; }

    [JsonPropertyName("EndDate")]
    public string EndDateRaw { get; set; }

    [JsonIgnore]
    public DateTimeOffset? EndDate => ParseExactDate(EndDateRaw);

    [JsonPropertyName("ExtraDescription")]
    public string ExtraDescription { get; set; }

    // Free fields
    [JsonPropertyName("FreeBoolField_01")]
    public bool? FreeBoolField_01 { get; set; }
    [JsonPropertyName("FreeBoolField_02")]
    public bool? FreeBoolField_02 { get; set; }
    [JsonPropertyName("FreeBoolField_03")]
    public bool? FreeBoolField_03 { get; set; }
    [JsonPropertyName("FreeBoolField_04")]
    public bool? FreeBoolField_04 { get; set; }
    [JsonPropertyName("FreeBoolField_05")]
    public bool? FreeBoolField_05 { get; set; }

    [JsonPropertyName("FreeDateField_01")]
    public string FreeDateField_01 { get; set; }
    [JsonPropertyName("FreeDateField_02")]
    public string FreeDateField_02 { get; set; }
    [JsonPropertyName("FreeDateField_03")]
    public string FreeDateField_03 { get; set; }
    [JsonPropertyName("FreeDateField_04")]
    public string FreeDateField_04 { get; set; }
    [JsonPropertyName("FreeDateField_05")]
    public string FreeDateField_05 { get; set; }

    [JsonPropertyName("FreeNumberField_01")]
    public string FreeNumberField_01 { get; set; }
    [JsonPropertyName("FreeNumberField_02")]
    public string FreeNumberField_02 { get; set; }
    [JsonPropertyName("FreeNumberField_03")]
    public string FreeNumberField_03 { get; set; }
    [JsonPropertyName("FreeNumberField_04")]
    public string FreeNumberField_04 { get; set; }
    [JsonPropertyName("FreeNumberField_05")]
    public string FreeNumberField_05 { get; set; }
    [JsonPropertyName("FreeNumberField_06")]
    public string FreeNumberField_06 { get; set; }
    [JsonPropertyName("FreeNumberField_07")]
    public string FreeNumberField_07 { get; set; }
    [JsonPropertyName("FreeNumberField_08")]
    public string FreeNumberField_08 { get; set; }

    [JsonPropertyName("FreeTextField_01")]
    public string FreeTextField_01 { get; set; }
    [JsonPropertyName("FreeTextField_02")]
    public string FreeTextField_02 { get; set; }
    [JsonPropertyName("FreeTextField_03")]
    public string FreeTextField_03 { get; set; }
    [JsonPropertyName("FreeTextField_04")]
    public string FreeTextField_04 { get; set; }
    [JsonPropertyName("FreeTextField_05")]
    public string FreeTextField_05 { get; set; }
    [JsonPropertyName("FreeTextField_06")]
    public string FreeTextField_06 { get; set; }
    [JsonPropertyName("FreeTextField_07")]
    public string FreeTextField_07 { get; set; }
    [JsonPropertyName("FreeTextField_08")]
    public string FreeTextField_08 { get; set; }
    [JsonPropertyName("FreeTextField_09")]
    public string FreeTextField_09 { get; set; }
    [JsonPropertyName("FreeTextField_10")]
    public string FreeTextField_10 { get; set; }

    // GL fields
    [JsonPropertyName("GLCostsCode")]
    public string GLCostsCode { get; set; }
    [JsonPropertyName("GLCostsDescription")]
    public string GLCostsDescription { get; set; }
    [JsonPropertyName("GLCosts")]
    public string GLCosts { get; set; }

    [JsonPropertyName("GLCostsWorkInProgress")]
    public string GLCostsWorkInProgress { get; set; }
    [JsonPropertyName("GLCostsWorkInProgressCode")]
    public string GLCostsWorkInProgressCode { get; set; }
    [JsonPropertyName("GLCostsWorkInProgressDescription")]
    public string GLCostsWorkInProgressDescription { get; set; }

    [JsonPropertyName("GLRevenueCode")]
    public string GLRevenueCode { get; set; }
    [JsonPropertyName("GLRevenueDescription")]
    public string GLRevenueDescription { get; set; }
    [JsonPropertyName("GLRevenue")]
    public string GLRevenue { get; set; }

    [JsonPropertyName("GLRevenueWorkInProgress")]
    public string GLRevenueWorkInProgress { get; set; }
    [JsonPropertyName("GLRevenueWorkInProgressCode")]
    public string GLRevenueWorkInProgressCode { get; set; }
    [JsonPropertyName("GLRevenueWorkInProgressDescription")]
    public string GLRevenueWorkInProgressDescription { get; set; }

    [JsonPropertyName("GLStockCode")]
    public string GLStockCode { get; set; }
    [JsonPropertyName("GLStockDescription")]
    public string GLStockDescription { get; set; }
    [JsonPropertyName("GLStock")]
    public string GLStock { get; set; }

    [JsonPropertyName("IsBatchItem")]
    public int? IsBatchItem { get; set; }

    [JsonPropertyName("IsFractionAllowedItem")]
    public bool? IsFractionAllowedItem { get; set; }

    [JsonPropertyName("IsMakeItem")]
    public int? IsMakeItem { get; set; }

    [JsonPropertyName("IsNewContract")]
    public int? IsNewContract { get; set; }

    [JsonPropertyName("IsOnDemandItem")]
    public int? IsOnDemandItem { get; set; }

    [JsonPropertyName("IsPackageItem")]
    public bool? IsPackageItem { get; set; }

    [JsonPropertyName("IsPurchaseItem")]
    public bool? IsPurchaseItem { get; set; }

    [JsonPropertyName("IsRegistrationCodeItem")]
    public int? IsRegistrationCodeItem { get; set; }

    [JsonPropertyName("IsSalesItem")]
    public bool? IsSalesItem { get; set; }

    [JsonPropertyName("IsSerialItem")]
    public bool? IsSerialItem { get; set; }

    [JsonPropertyName("IsStockItem")]
    public bool? IsStockItem { get; set; }

    [JsonPropertyName("IsSubcontractedItem")]
    public bool? IsSubcontractedItem { get; set; }

    [JsonPropertyName("IsTaxableItem")]
    public string IsTaxableItem { get; set; }

    [JsonPropertyName("IsTime")]
    public int? IsTime { get; set; }

    [JsonPropertyName("IsWebshopItem")]
    public int? IsWebshopItem { get; set; }

    [JsonPropertyName("ItemGroupCode")]
    public string ItemGroupCode { get; set; }

    [JsonPropertyName("ItemGroupDescription")]
    public string ItemGroupDescription { get; set; }

    [JsonPropertyName("ItemGroup")]
    public Guid? ItemGroup { get; set; }

    [JsonPropertyName("Modified")]
    public string ModifiedRaw { get; set; }

    [JsonIgnore]
    public DateTimeOffset? Modified => ParseExactDate(ModifiedRaw);

    [JsonPropertyName("ModifierFullName")]
    public string ModifierFullName { get; set; }

    [JsonPropertyName("Modifier")]
    public Guid? Modifier { get; set; }

    [JsonPropertyName("GrossWeight")]
    public string GrossWeight { get; set; }

    [JsonPropertyName("NetWeight")]
    public string NetWeight { get; set; }

    [JsonPropertyName("NetWeightUnit")]
    public string NetWeightUnit { get; set; }

    [JsonPropertyName("Notes")]
    public string Notes { get; set; }

    [JsonPropertyName("Picture")]
    public string Picture { get; set; }

    [JsonPropertyName("PictureName")]
    public string PictureName { get; set; }

    [JsonPropertyName("PictureUrl")]
    public string PictureUrl { get; set; }

    [JsonPropertyName("PictureThumbnailUrl")]
    public string PictureThumbnailUrl { get; set; }

    [JsonPropertyName("SalesVatCodeDescription")]
    public string SalesVatCodeDescription { get; set; }

    [JsonPropertyName("SalesVatCode")]
    public string SalesVatCode { get; set; }

    [JsonPropertyName("SearchCode")]
    public string SearchCode { get; set; }

    [JsonPropertyName("SecurityLevel")]
    public int? SecurityLevel { get; set; }

    [JsonPropertyName("StartDate")]
    public string StartDateRaw { get; set; }

    [JsonIgnore]
    public DateTimeOffset? StartDate => ParseExactDate(StartDateRaw);

    [JsonPropertyName("StatisticalCode")]
    public string StatisticalCode { get; set; }

    [JsonPropertyName("StatisticalNetWeight")]
    public string StatisticalNetWeight { get; set; }

    [JsonPropertyName("StatisticalUnits")]
    public int? StatisticalUnits { get; set; }

    [JsonPropertyName("StatisticalValue")]
    public string StatisticalValue { get; set; }

    [JsonPropertyName("Stock")]
    public decimal? Stock { get; set; }

    [JsonPropertyName("Unit")]
    public string Unit { get; set; }

    [JsonPropertyName("UnitDescription")]
    public string UnitDescription { get; set; }

    [JsonPropertyName("UnitType")]
    public string UnitType { get; set; }

    [JsonPropertyName("AssembledLeadDays")]
    public string AssembledLeadDays { get; set; }

    [JsonPropertyName("BatchQuantity")]
    public string BatchQuantity { get; set; }

    [JsonPropertyName("UseExplosion")]
    public string UseExplosion { get; set; }

    // --- Helper for parsing Exact's /Date(###)/ format ---
    private static DateTimeOffset? ParseExactDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        // Expect format like: "/Date(1390511122233)/" or "1390511122233"
        var m = Regex.Match(raw, @"-?\d+");
        if (!m.Success)
            return null;

        if (!long.TryParse(m.Value, out var ms))
            return null;

        // Exact returns milliseconds since Unix epoch (UTC)
        try
        {
            var dto = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            return dto;
        }
        catch
        {
            return null;
        }
    }
}
