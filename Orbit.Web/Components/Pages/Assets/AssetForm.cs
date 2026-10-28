using System.Globalization;
using Orbit.Application.Assets;
using Orbit.Application.Models;
using Orbit.Data.Entities;
using ValidationException = Orbit.Application.ValidationException;

namespace Orbit.Components.Pages.Assets;

/// <summary>The asset form (spec §6.19). Property values are keyed by property id; the purchase value is parsed invariantly.</summary>
public sealed class AssetForm
{
    /// <summary>The ERP asset register number, when the asset is on it; blank for none.</summary>
    public string? AssetNumber { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? AssetTypeId { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public AssetStatus Status { get; set; } = AssetStatus.Active;
    public Guid? AssetLocationId { get; set; }
    public DateOnly? PurchaseDate { get; set; }
    /// <summary>Text, so "1234.50" means the same whatever the server's culture; parsed in <see cref="ToInput"/>.</summary>
    public string? PurchaseValue { get; set; }
    public string? PurchaseOrder { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? Supplier { get; set; }
    public DateOnly? WarrantyExpiresOn { get; set; }
    public DateOnly? DisposedOn { get; set; }
    public List<Guid> AssigneeIds { get; set; } = [];
    /// <summary>Property values keyed by the property's id (as text). A field posted blank clears the value.</summary>
    public Dictionary<string, string?> Properties { get; set; } = new();

    /// <summary>
    /// The service input. Every one of the chosen type's properties is passed - a blank one clears its value - and values left over
    /// from another type are ignored, so the form is the whole state.
    /// </summary>
    public AssetInput ToInput(IEnumerable<AssetTypeProperty> typeProperties)
    {
        var properties = typeProperties.ToDictionary(p => p.Id, p => Properties.GetValueOrDefault(p.Id.ToString()));
        return new AssetInput
        {
            AssetNumber = AssetNumber, Name = Name, Description = Description, DepartmentId = DepartmentId,
            AssetTypeId = AssetTypeId ?? Guid.Empty, Manufacturer = Manufacturer, Model = Model, SerialNumber = SerialNumber,
            Status = Status, AssetLocationId = AssetLocationId, PurchaseDate = PurchaseDate, PurchaseValue = ParseValue(PurchaseValue),
            PurchaseOrder = PurchaseOrder, InvoiceNumber = InvoiceNumber, Supplier = Supplier, WarrantyExpiresOn = WarrantyExpiresOn,
            DisposedOn = DisposedOn, AssigneeIds = AssigneeIds, Properties = properties
        };
    }

    public static AssetForm From(Asset a) => new()
    {
        AssetNumber = a.AssetNumber, Name = a.Name, Description = a.Description, DepartmentId = a.DepartmentId, AssetTypeId = a.AssetTypeId,
        Manufacturer = a.Manufacturer, Model = a.Model, SerialNumber = a.SerialNumber, Status = a.Status, AssetLocationId = a.AssetLocationId,
        PurchaseDate = a.PurchaseDate, PurchaseValue = a.PurchaseValue?.ToString("0.00", CultureInfo.InvariantCulture),
        PurchaseOrder = a.PurchaseOrder, InvoiceNumber = a.InvoiceNumber, Supplier = a.Supplier, WarrantyExpiresOn = a.WarrantyExpiresOn,
        DisposedOn = a.DisposedOn, AssigneeIds = a.Assignments.Select(x => x.UserId).ToList(),
        Properties = a.PropertyValues.ToDictionary(v => v.AssetTypePropertyId.ToString(), v => (string?)v.Value)
    };

    /// <summary>
    /// A new asset's form filled from <paramref name="source"/> (the asset page's Copy, §6.19): every detail and property value
    /// except what belongs to the one item - its ERP asset number, serial number and holders. A copy of a disposed asset is its
    /// replacement, so it starts Active.
    /// </summary>
    public static AssetForm CopyOf(Asset source)
    {
        var form = From(source);
        form.AssetNumber = null;
        form.SerialNumber = null;
        form.AssigneeIds = [];
        if (form.Status == AssetStatus.Disposed)
        {
            form.Status = AssetStatus.Active;
            form.DisposedOn = null;
        }
        return form;
    }

    /// <summary>A money amount as typed: spaces ignored, a lone comma read as the decimal point.</summary>
    public static decimal? ParseValue(string? text)
    {
        var t = text?.Replace(" ", "").Replace(" ", "").Trim();
        if (string.IsNullOrEmpty(t)) return null;
        if (!t.Contains('.') && t.Count(c => c == ',') == 1) t = t.Replace(',', '.');
        return decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)
            ? d
            : throw new ValidationException($"The purchase value must be an amount like 1234.50; got \"{text}\".");
    }
}

/// <summary>The type-driven property fields (spec §6.19): what the form shows for the chosen type, worked out again when the type changes.</summary>
public sealed class AssetPropertyFields
{
    public IReadOnlyList<AssetTypeProperty> Properties { get; init; } = [];
    public IReadOnlyDictionary<Guid, string> Values { get; init; } = new Dictionary<Guid, string>();
    /// <summary>On a change of type: the current values that won't carry over ("RAM: 16").</summary>
    public IReadOnlyList<string> Dropped { get; init; } = [];
    /// <summary>On a change of type: the names of the values that will carry over.</summary>
    public IReadOnlyList<string> Carried { get; init; } = [];
    public bool TypeChosen { get; init; }

    /// <summary>
    /// The fields for <paramref name="type"/>. With <paramref name="asset"/> on another type, the values that would carry over are
    /// filled in and the rest listed as dropped; <paramref name="posted"/> (a redisplayed form) wins over both.
    /// </summary>
    public static AssetPropertyFields For(AssetType? type, Asset? asset, IReadOnlyDictionary<string, string?>? posted = null)
    {
        if (type is null) return new AssetPropertyFields();
        var properties = type.Properties.OrderBy(p => p.DisplayOrder).ThenBy(p => p.Name).ToList();
        var values = new Dictionary<Guid, string>();
        var dropped = new List<string>();
        var carried = new List<string>();
        if (asset is not null)
        {
            if (asset.AssetTypeId == type.Id)
            {
                foreach (var v in asset.PropertyValues) values[v.AssetTypePropertyId] = v.Value;
            }
            else
            {
                var oldProperties = asset.AssetType.Properties.ToDictionary(p => p.Id);
                var current = asset.PropertyValues.Where(v => oldProperties.ContainsKey(v.AssetTypePropertyId))
                    .Select(v => (Property: oldProperties[v.AssetTypePropertyId], v.Value)).ToList();
                var kept = AssetPropertyRules.CarryOver(current, properties);
                foreach (var (id, value) in kept) values[id] = value;
                carried = properties.Where(p => kept.ContainsKey(p.Id)).Select(p => p.Name).ToList();
                dropped = current.Where(c => !carried.Contains(c.Property.Name, StringComparer.OrdinalIgnoreCase))
                    .Select(c => $"{c.Property.Name}: {AssetPropertyRules.Display(c.Property.PropertyType, c.Value)}").ToList();
            }
        }
        if (posted is not null)
        {
            foreach (var p in properties)
            {
                if (!posted.TryGetValue(p.Id.ToString(), out var raw)) continue;
                if (string.IsNullOrWhiteSpace(raw)) values.Remove(p.Id);
                else values[p.Id] = raw.Trim();
            }
        }
        return new AssetPropertyFields { Properties = properties, Values = values, Dropped = dropped, Carried = carried, TypeChosen = true };
    }
}
