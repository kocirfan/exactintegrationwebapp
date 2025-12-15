using System;
using System.Text.Json.Serialization;

public class ExactAddress
{
    [JsonPropertyName("ID")]
    public Guid Id { get; set; }

    [JsonPropertyName("Account")]
    public Guid AccountId { get; set; }

    [JsonPropertyName("AccountName")]
    public string AccountName { get; set; }

    [JsonPropertyName("AccountIsSupplier")]
    public bool AccountIsSupplier { get; set; }

    [JsonPropertyName("AddressLine1")]
    public string AddressLine1 { get; set; }

    [JsonPropertyName("AddressLine2")]
    public string AddressLine2 { get; set; }

    [JsonPropertyName("AddressLine3")]
    public string AddressLine3 { get; set; }

    [JsonPropertyName("City")]
    public string City { get; set; }

    [JsonPropertyName("Contact")]
    public Guid? ContactId { get; set; }

    [JsonPropertyName("ContactName")]
    public string ContactName { get; set; }

    [JsonPropertyName("Country")]
    public string CountryCode { get; set; } // "NL ", "BE " formatında

    [JsonPropertyName("CountryName")]
    public string CountryName { get; set; }

    [JsonPropertyName("Created")]
    [JsonIgnore]
    public DateTime? Created { get; set; }

    [JsonPropertyName("Creator")]
    public Guid? CreatorId { get; set; }

    [JsonPropertyName("CreatorFullName")]
    public string CreatorFullName { get; set; }

    [JsonPropertyName("Division")]
    public int Division { get; set; }

    [JsonPropertyName("Fax")]
    public string Fax { get; set; }

    [JsonPropertyName("FreeBoolField_01")]
    [JsonIgnore]
    public bool FreeBoolField01 { get; set; }

    [JsonPropertyName("FreeBoolField_02")]
    [JsonIgnore]
    public bool FreeBoolField02 { get; set; }

    [JsonPropertyName("FreeBoolField_03")]
    [JsonIgnore]
    public bool FreeBoolField03 { get; set; }

    [JsonPropertyName("FreeBoolField_04")]
    [JsonIgnore]
    public bool FreeBoolField04 { get; set; }

    [JsonPropertyName("FreeBoolField_05")]
    [JsonIgnore]
    public bool FreeBoolField05 { get; set; }

    [JsonPropertyName("FreeDateField_01")]
    [JsonIgnore]

    public DateTime? FreeDateField01 { get; set; }

    [JsonPropertyName("FreeDateField_02")]
    [JsonIgnore]
    public DateTime? FreeDateField02 { get; set; }

    [JsonPropertyName("FreeDateField_03")]
    [JsonIgnore]

    public DateTime? FreeDateField03 { get; set; }

    [JsonPropertyName("FreeDateField_04")]
    [JsonIgnore]

    public DateTime? FreeDateField04 { get; set; }

    [JsonPropertyName("FreeDateField_05")]
    [JsonIgnore]

    public DateTime? FreeDateField05 { get; set; }

    [JsonPropertyName("FreeNumberField_01")]
    [JsonIgnore]
    public decimal FreeNumberField01 { get; set; }

    [JsonPropertyName("FreeNumberField_02")]
    [JsonIgnore]
    public decimal FreeNumberField02 { get; set; }

    [JsonPropertyName("FreeNumberField_03")]
    [JsonIgnore]
    public decimal FreeNumberField03 { get; set; }

    [JsonPropertyName("FreeNumberField_04")]
    [JsonIgnore]
    public decimal FreeNumberField04 { get; set; }

    [JsonPropertyName("FreeNumberField_05")]
    [JsonIgnore]
    public decimal FreeNumberField05 { get; set; }

    [JsonPropertyName("FreeTextField_01")]
    
    public string FreeTextField01 { get; set; }

    [JsonPropertyName("FreeTextField_02")]
    public string FreeTextField02 { get; set; }

    [JsonPropertyName("FreeTextField_03")]
    public string FreeTextField03 { get; set; }

    [JsonPropertyName("FreeTextField_04")]
    public string FreeTextField04 { get; set; }

    [JsonPropertyName("FreeTextField_05")]
    public string FreeTextField05 { get; set; }

    [JsonPropertyName("Mailbox")]
    public string Email { get; set; }

    [JsonPropertyName("Main")]
    public bool IsMain { get; set; }

    [JsonPropertyName("Modified")]
    [JsonIgnore]
    // [JsonConverter(typeof(ExactDateTimeConverter))]
    public DateTime? Modified { get; set; }

    [JsonPropertyName("Modifier")]
    public Guid? ModifierId { get; set; }

    [JsonPropertyName("ModifierFullName")]
    public string ModifierFullName { get; set; }

    [JsonPropertyName("NicNumber")]
    public string NicNumber { get; set; }

    [JsonPropertyName("Notes")]
    public string Notes { get; set; }

    [JsonPropertyName("Phone")]
    public string Phone { get; set; }

    [JsonPropertyName("PhoneExtension")]
    public string PhoneExtension { get; set; }

    [JsonPropertyName("Postcode")]
    public string PostalCode { get; set; }

    [JsonPropertyName("State")]
    public string StateCode { get; set; }

    [JsonPropertyName("StateDescription")]
    public string StateDescription { get; set; }

    [JsonPropertyName("Type")]
    public int? Type { get; set; }

    [JsonPropertyName("TypeDescription")]
    public string TypeDescription { get; set; }

    [JsonPropertyName("Warehouse")]
    public Guid? WarehouseId { get; set; }

    [JsonPropertyName("WarehouseCode")]
    public string WarehouseCode { get; set; }

    [JsonPropertyName("WarehouseDescription")]
    public string WarehouseDescription { get; set; }

    [JsonPropertyName("Source")]
    public string Source { get; set; }

    // Custom properties (not from API)
    public string FullAddress
    {
        get
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(AddressLine1))
                parts.Add(AddressLine1);

            if (!string.IsNullOrWhiteSpace(AddressLine2))
                parts.Add(AddressLine2);

            if (!string.IsNullOrWhiteSpace(AddressLine3))
                parts.Add(AddressLine3);

            if (!string.IsNullOrWhiteSpace(PostalCode))
                parts.Add(PostalCode);

            if (!string.IsNullOrWhiteSpace(City))
                parts.Add(City);

            return string.Join(", ", parts);
        }
    }

    // public string AddressTypeDescription
    // {
    //     get
    //     {
    //         return Type switch
    //         {
    //             1 => "Fatura Adresi",
    //             2 => "Sevk Adresi",
    //             3 => "Posta Adresi",
    //             4 => "Diğer",
    //             _ => "Bilinmeyen"
    //         };
    //     }
    // }

    public bool IsActive
    {
        get
        {
            // Ek bir "Active" alanı olmadığı için varsayılan olarak true döndürüyoruz
            return true;
        }
    }

    public override string ToString()
    {
        return $"{AccountName} - {FullAddress}";
    }
}
