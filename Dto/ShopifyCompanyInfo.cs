using System;
using System.Collections.Generic;
using System.Linq;

// Shopify siparişinin note_attributes alanından gelen kurumsal müşteri bilgileri.
// company_name doluysa sipariş kurumsal akışla işlenir.
public class ShopifyCompanyInfo
{
    public string Name { get; set; }
    public string ContactPerson { get; set; }
    public string Address { get; set; }
    public string PostalCode { get; set; }
    public string City { get; set; }
    public string Phone { get; set; }
    public string Email { get; set; }

    public bool HasCompany => !string.IsNullOrWhiteSpace(Name);

    public string FullAddress
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Address)) parts.Add(Address.Trim());
            if (!string.IsNullOrWhiteSpace(PostalCode)) parts.Add(PostalCode.Trim());
            if (!string.IsNullOrWhiteSpace(City)) parts.Add(City.Trim());
            return string.Join(", ", parts);
        }
    }

    public static ShopifyCompanyInfo FromNoteAttributes(List<ShopifyNoteAttribute> noteAttributes)
    {
        var info = new ShopifyCompanyInfo();
        if (noteAttributes == null || noteAttributes.Count == 0)
            return info;

        string Get(string name) => noteAttributes
            .FirstOrDefault(attr => string.Equals(attr.Name, name, StringComparison.OrdinalIgnoreCase))?
            .Value?.Trim();

        info.Name = Get("company_name");
        info.ContactPerson = Get("company_contact_person");
        info.Address = Get("company_address");
        info.PostalCode = Get("company_postal_code");
        info.City = Get("company_city");
        info.Phone = Get("company_phone");
        info.Email = Get("company_email");
        return info;
    }
}
