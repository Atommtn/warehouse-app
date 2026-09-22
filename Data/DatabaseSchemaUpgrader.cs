using Microsoft.EntityFrameworkCore;

namespace WarehouseApp.Data;

public static class DatabaseSchemaUpgrader
{
    public static void Upgrade(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
IF OBJECT_ID(N'[Warehouses]', N'U') IS NULL
BEGIN
    CREATE TABLE [Warehouses] (
        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Warehouses] PRIMARY KEY,
        [Code] nvarchar(50) NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [Location] nvarchar(max) NOT NULL CONSTRAINT [DF_Warehouses_Location] DEFAULT N'',
        [ResponsiblePerson] nvarchar(max) NOT NULL CONSTRAINT [DF_Warehouses_Responsible] DEFAULT N'',
        [AccountingDetailCode] nvarchar(max) NOT NULL CONSTRAINT [DF_Warehouses_Accounting] DEFAULT N'',
        [Notes] nvarchar(max) NOT NULL CONSTRAINT [DF_Warehouses_Notes] DEFAULT N'',
        [IsActive] bit NOT NULL CONSTRAINT [DF_Warehouses_Active] DEFAULT 1,
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_Warehouses_Created] DEFAULT SYSUTCDATETIME()
    );
    CREATE UNIQUE INDEX [IX_Warehouses_Code] ON [Warehouses]([Code]);
END;

IF OBJECT_ID(N'[WarehouseStocks]', N'U') IS NULL
BEGIN
    CREATE TABLE [WarehouseStocks] (
        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_WarehouseStocks] PRIMARY KEY,
        [WarehouseId] int NOT NULL,
        [MaterialId] int NOT NULL,
        [Quantity] decimal(18,3) NOT NULL CONSTRAINT [DF_WarehouseStocks_Quantity] DEFAULT 0,
        [MinStockLevel] decimal(18,3) NOT NULL CONSTRAINT [DF_WarehouseStocks_Min] DEFAULT 0,
        CONSTRAINT [FK_WarehouseStocks_Warehouses] FOREIGN KEY ([WarehouseId]) REFERENCES [Warehouses]([Id]),
        CONSTRAINT [FK_WarehouseStocks_Materials] FOREIGN KEY ([MaterialId]) REFERENCES [Materials]([Id])
    );
    CREATE UNIQUE INDEX [IX_WarehouseStocks_WarehouseId_MaterialId] ON [WarehouseStocks]([WarehouseId],[MaterialId]);
END;

IF OBJECT_ID(N'[InventoryTransactions]', N'U') IS NULL
BEGIN
    CREATE TABLE [InventoryTransactions] (
        [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_InventoryTransactions] PRIMARY KEY,
        [DocumentNumber] nvarchar(100) NOT NULL,
        [Type] int NOT NULL,
        [WarehouseId] int NOT NULL,
        [RelatedWarehouseId] int NULL,
        [MaterialId] int NOT NULL,
        [IncomingQuantity] decimal(18,3) NOT NULL DEFAULT 0,
        [OutgoingQuantity] decimal(18,3) NOT NULL DEFAULT 0,
        [BalanceAfter] decimal(18,3) NOT NULL DEFAULT 0,
        [UnitPrice] decimal(18,2) NOT NULL DEFAULT 0,
        [TransactionDate] datetime2 NOT NULL,
        [Description] nvarchar(max) NOT NULL DEFAULT N'',
        [CreatedByUserId] int NOT NULL,
        [CreatedByUsername] nvarchar(200) NOT NULL DEFAULT N'',
        [CreatedAt] datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [FK_InventoryTransactions_Warehouses] FOREIGN KEY ([WarehouseId]) REFERENCES [Warehouses]([Id]),
        CONSTRAINT [FK_InventoryTransactions_RelatedWarehouse] FOREIGN KEY ([RelatedWarehouseId]) REFERENCES [Warehouses]([Id]),
        CONSTRAINT [FK_InventoryTransactions_Materials] FOREIGN KEY ([MaterialId]) REFERENCES [Materials]([Id])
    );
    CREATE INDEX [IX_InventoryTransactions_MaterialId_TransactionDate] ON [InventoryTransactions]([MaterialId],[TransactionDate]);
END;

IF COL_LENGTH('StockEntries', 'WarehouseId') IS NULL
BEGIN
    ALTER TABLE [StockEntries] ADD [WarehouseId] int NULL;
    ALTER TABLE [StockEntries] ADD CONSTRAINT [FK_StockEntries_Warehouses] FOREIGN KEY ([WarehouseId]) REFERENCES [Warehouses]([Id]);
END;
IF COL_LENGTH('StockWithdrawals', 'WarehouseId') IS NULL
BEGIN
    ALTER TABLE [StockWithdrawals] ADD [WarehouseId] int NULL;
    ALTER TABLE [StockWithdrawals] ADD CONSTRAINT [FK_StockWithdrawals_Warehouses] FOREIGN KEY ([WarehouseId]) REFERENCES [Warehouses]([Id]);
END;
""");

        db.Database.ExecuteSqlRaw("""
IF NOT EXISTS (SELECT 1 FROM [Warehouses] WHERE [Code]=N'MAIN') INSERT INTO [Warehouses] ([Code],[Name]) VALUES (N'MAIN',N'انبار اصلی');
IF NOT EXISTS (SELECT 1 FROM [Warehouses] WHERE [Code]=N'SECOND') INSERT INTO [Warehouses] ([Code],[Name]) VALUES (N'SECOND',N'انبار دوم');
IF NOT EXISTS (SELECT 1 FROM [Warehouses] WHERE [Code]=N'BOX') INSERT INTO [Warehouses] ([Code],[Name]) VALUES (N'BOX',N'انبار جعبه');
IF NOT EXISTS (SELECT 1 FROM [Warehouses] WHERE [Code]=N'STORE') INSERT INTO [Warehouses] ([Code],[Name]) VALUES (N'STORE',N'انبار فروشگاه');
IF NOT EXISTS (SELECT 1 FROM [Warehouses] WHERE [Code]=N'COLD') INSERT INTO [Warehouses] ([Code],[Name]) VALUES (N'COLD',N'سردخانه');

DECLARE @mainId int=(SELECT TOP(1) [Id] FROM [Warehouses] WHERE [Code]=N'MAIN');
UPDATE [StockEntries] SET [WarehouseId]=@mainId WHERE [WarehouseId] IS NULL;
UPDATE [StockWithdrawals] SET [WarehouseId]=@mainId WHERE [WarehouseId] IS NULL;
INSERT INTO [WarehouseStocks] ([WarehouseId],[MaterialId],[Quantity],[MinStockLevel])
SELECT @mainId,m.[Id],m.[CurrentStock],m.[MinStockLevel]
FROM [Materials] m
WHERE NOT EXISTS (SELECT 1 FROM [WarehouseStocks] s WHERE s.[WarehouseId]=@mainId AND s.[MaterialId]=m.[Id]);

INSERT INTO [InventoryTransactions]
([DocumentNumber],[Type],[WarehouseId],[MaterialId],[IncomingQuantity],[OutgoingQuantity],[BalanceAfter],[UnitPrice],[TransactionDate],[Description],[CreatedByUserId],[CreatedByUsername])
SELECT CONCAT(N'OPEN-',m.[Code]),0,@mainId,m.[Id],m.[CurrentStock],0,m.[CurrentStock],m.[PricePerUnit],SYSUTCDATETIME(),N'انتقال مانده اولیه هنگام فعال‌سازی چند انبار',0,N'system'
FROM [Materials] m
WHERE m.[CurrentStock]<>0 AND NOT EXISTS (SELECT 1 FROM [InventoryTransactions] t WHERE t.[DocumentNumber]=CONCAT(N'OPEN-',m.[Code]));
""");
    }
}
