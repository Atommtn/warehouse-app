namespace WarehouseApp.Models;

public enum UserRole { Admin, Manager, Operator, Attendant, Viewer }

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string FullName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Unit
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<Material> Materials { get; set; } = new();
}

public class MaterialGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<Material> Materials { get; set; } = new();
}

public class Supplier
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string ContactPerson { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Address { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<Material> Materials { get; set; } = new();
}

public class Material
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int? GroupId { get; set; }
    public MaterialGroup? Group { get; set; }
    // Legacy stock-unit link, kept in sync with StockUnitName so old screens and upgrades keep working.
    public int? UnitId { get; set; }
    public Unit? Unit { get; set; }
    // Last purchase price of one StockUnitName.
    public decimal PricePerUnit { get; set; }
    // Stock quantities are counted in StockUnitName (e.g. کارتن).
    public decimal MinStockLevel { get; set; }
    public decimal CurrentStock { get; set; }
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public string Notes { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    // ── واحدها ──
    // واحد موجودی: همه‌ی مقدارهای انبار با این واحد ذخیره می‌شوند (مثلاً کارتن).
    public string StockUnitName { get; set; } = "";
    // واحد رسپی: فقط مبنای قیمت و هزینه‌ی رسپی است (مثلاً کیلوگرم). Stored in the old BaseUnitName column.
    public string RecipeUnitName { get; set; } = "کیلوگرم";
    // Legacy columns from the old "base unit" model; only read by the database upgrade.
    public decimal BaseQuantity { get; set; } = 1;
    public string DisplayUnitName { get; set; } = "";
    public bool UnitsNormalized { get; set; }
    public List<StockEntry> Entries { get; set; } = new();
    public List<StockWithdrawal> Withdrawals { get; set; } = new();
}

public class StockEntry
{
    public int Id { get; set; }
    public int MaterialId { get; set; }
    public Material? Material { get; set; }
    // Quantity in the material's stock unit.
    public decimal Quantity { get; set; }
    // Price of one stock unit (TotalPrice / Quantity).
    public decimal PricePerUnit { get; set; }
    public decimal TotalPrice { get; set; }
    public DateTime EntryDate { get; set; }
    public DateTime ExpiryDate { get; set; }
    public string Notes { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int CreatedByUserId { get; set; }
    public string CreatedByUsername { get; set; } = "";
    public int? WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public bool PriceConfirmed { get; set; }
    public string PriceConfirmedBy { get; set; } = "";
    public DateTime? PriceConfirmedAt { get; set; }
    // What the user typed, e.g. 3 کارتن; ConversionFactor = stock units in one entered unit.
    public decimal EnteredQuantity { get; set; }
    public string EnteredUnitName { get; set; } = "";
    public decimal ConversionFactor { get; set; } = 1;
    public decimal PricePerStockUnit => Quantity > 0 && TotalPrice > 0 ? TotalPrice / Quantity : PricePerUnit;
}

public class StockWithdrawal
{
    public int Id { get; set; }
    public int MaterialId { get; set; }
    public Material? Material { get; set; }
    // Quantity in the material's stock unit.
    public decimal Quantity { get; set; }
    public DateTime WithdrawalDate { get; set; }
    public string Reason { get; set; } = "";
    public string Department { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int CreatedByUserId { get; set; }
    public string CreatedByUsername { get; set; } = "";
    public WithdrawalStatus Status { get; set; } = WithdrawalStatus.Pending;
    public int? ApprovedByUserId { get; set; }
    public string? ApprovedByUsername { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? RejectReason { get; set; }
    public int? WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public decimal EnteredQuantity { get; set; }
    public string EnteredUnitName { get; set; } = "";
    public decimal ConversionFactor { get; set; } = 1;
}

public enum WithdrawalStatus { Pending, Approved, Rejected }

public class Warehouse
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    public string ResponsiblePerson { get; set; } = "";
    public string AccountingDetailCode { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class WarehouseStock
{
    public int Id { get; set; }
    public int WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public int MaterialId { get; set; }
    public Material? Material { get; set; }
    public decimal Quantity { get; set; }
    public decimal MinStockLevel { get; set; }
}

public enum InventoryTransactionType { OpeningBalance, Entry, Withdrawal, TransferOut, TransferIn, Adjustment }

public class RolePagePermission
{
    public int Id { get; set; }
    public UserRole Role { get; set; }
    public string PageKey { get; set; } = "";
    public bool IsAllowed { get; set; }
}

public class InventoryTransaction
{
    public long Id { get; set; }
    public string DocumentNumber { get; set; } = "";
    public InventoryTransactionType Type { get; set; }
    public int WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public int? RelatedWarehouseId { get; set; }
    public Warehouse? RelatedWarehouse { get; set; }
    public int MaterialId { get; set; }
    public Material? Material { get; set; }
    public decimal IncomingQuantity { get; set; }
    public decimal OutgoingQuantity { get; set; }
    public decimal BalanceAfter { get; set; }
    public decimal UnitPrice { get; set; }
    public DateTime TransactionDate { get; set; }
    public string Description { get; set; } = "";
    public int CreatedByUserId { get; set; }
    public string CreatedByUsername { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class StockAlert
{
    public int MaterialId { get; set; }
    public string MaterialName { get; set; } = "";
    public string MaterialCode { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal CurrentStock { get; set; }
    public decimal MinStockLevel { get; set; }
    public AlertType Type { get; set; }
    public string Message { get; set; } = "";
    public DateTime? ExpiryDate { get; set; }
}

public enum AlertType { LowStock, Expiring, Expired }

public class Recipe
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public decimal BakingLossPercent { get; set; } = 0;
    public decimal PieceWeightGrams { get; set; } = 0;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<RecipeIngredient> Ingredients { get; set; } = new();
}

public class RecipeIngredient
{
    public int Id { get; set; }
    public int RecipeId { get; set; }
    public Recipe? Recipe { get; set; }
    public int MaterialId { get; set; }
    public Material? Material { get; set; }
    public decimal Quantity { get; set; }   // مقدار بر حسب UnitName
    public bool IsTopping { get; set; }
    public string UnitName { get; set; } = "";
}

public class MaterialUnitConversion
{
    public int Id { get; set; }
    public int MaterialId { get; set; }
    public Material? Material { get; set; }
    public string UnitName { get; set; } = "";
    // How many of this unit make one stock unit: کارتن 1، شانه 6، عدد 180، کیلوگرم 12.6.
    public decimal PerStockUnit { get; set; } = 1;
    // The relation is an estimate (e.g. the weight of an egg); it only affects prices, never stock.
    public bool IsApproximate { get; set; }
    // How the user wrote the definition: "DefinedCount UnitName = DefinedAmount DefinedRefUnit", e.g. 1 کارتن = 6 شانه.
    public string DefinedRefUnit { get; set; } = "";
    public decimal DefinedCount { get; set; } = 1;
    public decimal DefinedAmount { get; set; }
    public bool IsActive { get; set; } = true;
    // Legacy columns of the old base-unit model.
    public decimal FactorToBaseUnit { get; set; } = 1;
    public bool IsLegacyStockUnit { get; set; }
}

public class Asset
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public DateTime PurchaseDate { get; set; } = DateTime.Today;
    public string Notes { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedByUsername { get; set; } = "";
    public List<AssetDisposal> Disposals { get; set; } = new();
    public decimal TotalPrice => Quantity * UnitPrice;
}

public class AssetDisposal
{
    public int Id { get; set; }
    public int AssetId { get; set; }
    public Asset? Asset { get; set; }
    public decimal Quantity { get; set; }
    public DateTime DisposalDate { get; set; } = DateTime.Today;
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedByUsername { get; set; } = "";
}

/// <summary>Small key/value settings editable from the UI (SMS recipients, schedule...). Credentials never go here.</summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public class SmsLog
{
    public int Id { get; set; }
    public DateTime SentAt { get; set; } = DateTime.Now;
    public string Kind { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string Message { get; set; } = "";
    public bool Success { get; set; }
    public string Response { get; set; } = "";
    // Asanak msgid, used to ask msgstatus whether the message was delivered.
    public string MessageId { get; set; } = "";
    public string DeliveryStatus { get; set; } = "";
    public DateTime? StatusCheckedAt { get; set; }
}
