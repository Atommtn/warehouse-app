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
    public int? UnitId { get; set; }
    public Unit? Unit { get; set; }
    public decimal PricePerUnit { get; set; }
    public decimal MinStockLevel { get; set; }
    public decimal CurrentStock { get; set; }
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public string Notes { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<StockEntry> Entries { get; set; } = new();
    public List<StockWithdrawal> Withdrawals { get; set; } = new();
}

public class StockEntry
{
    public int Id { get; set; }
    public int MaterialId { get; set; }
    public Material? Material { get; set; }
    public decimal Quantity { get; set; }
    public decimal PricePerUnit { get; set; }
    public DateTime EntryDate { get; set; }
    public DateTime ExpiryDate { get; set; }
    public string Notes { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int CreatedByUserId { get; set; }
    public string CreatedByUsername { get; set; } = "";
}

public class StockWithdrawal
{
    public int Id { get; set; }
    public int MaterialId { get; set; }
    public Material? Material { get; set; }
    public decimal Quantity { get; set; }
    public DateTime WithdrawalDate { get; set; }
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int CreatedByUserId { get; set; }
    public string CreatedByUsername { get; set; } = "";
    public WithdrawalStatus Status { get; set; } = WithdrawalStatus.Pending;
    public int? ApprovedByUserId { get; set; }
    public string? ApprovedByUsername { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? RejectReason { get; set; }
}

public enum WithdrawalStatus { Pending, Approved, Rejected }

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

// ── Recipe ──
public class Recipe
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public decimal BakingLossPercent { get; set; } = 0;   // درصد افت پخت
    public decimal PieceWeightGrams { get; set; } = 0;    // وزن هر دانه (گرم)
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
    public decimal Quantity { get; set; }    // مقدار (بر اساس واحد ماده)
    public bool IsTopping { get; set; }      // روکاری؟
}
