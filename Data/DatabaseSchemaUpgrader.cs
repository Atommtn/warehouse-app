using Microsoft.EntityFrameworkCore;

namespace WarehouseApp.Data;

public static class DatabaseSchemaUpgrader
{
    public static void Upgrade(AppDbContext db)
    {
        // Tables copied into the database by hand (e.g. Units) can have an Id column without IDENTITY, so EF inserts fail with
        // "Cannot insert NULL into column Id". Give such Id columns a sequence default that starts after the current maximum;
        // existing rows, keys and foreign keys stay untouched.
        db.Database.ExecuteSqlRaw("""
DECLARE @table sysname,@type sysname,@sequence sysname,@next bigint,@sql nvarchar(max);
DECLARE id_cursor CURSOR LOCAL FAST_FORWARD FOR
 SELECT t.[name],ty.[name] FROM sys.tables t
 JOIN sys.columns c ON c.[object_id]=t.[object_id] AND c.[name]=N'Id'
 JOIN sys.types ty ON ty.[user_type_id]=c.[user_type_id]
 WHERE t.[is_ms_shipped]=0 AND c.[is_identity]=0 AND c.[default_object_id]=0 AND ty.[name] IN (N'int',N'bigint')
   AND EXISTS(SELECT 1 FROM sys.index_columns ic JOIN sys.indexes i ON i.[object_id]=ic.[object_id] AND i.[index_id]=ic.[index_id] WHERE i.[is_primary_key]=1 AND ic.[object_id]=t.[object_id] AND ic.[column_id]=c.[column_id]);
OPEN id_cursor; FETCH NEXT FROM id_cursor INTO @table,@type;
WHILE @@FETCH_STATUS=0
BEGIN
 SET @sequence=N'Seq_'+@table+N'_Id';
 SET @sql=N'SELECT @n=ISNULL(MAX([Id]),0)+1 FROM '+QUOTENAME(@table); EXEC sp_executesql @sql,N'@n bigint OUTPUT',@n=@next OUTPUT;
 IF OBJECT_ID(@sequence,N'SO') IS NULL
 BEGIN SET @sql=N'CREATE SEQUENCE '+QUOTENAME(@sequence)+N' AS '+@type+N' START WITH '+CAST(@next AS nvarchar(30))+N' INCREMENT BY 1'; EXEC sp_executesql @sql; END
 ELSE
 BEGIN SET @sql=N'ALTER SEQUENCE '+QUOTENAME(@sequence)+N' RESTART WITH '+CAST(@next AS nvarchar(30)); EXEC sp_executesql @sql; END;
 SET @sql=N'ALTER TABLE '+QUOTENAME(@table)+N' ADD CONSTRAINT '+QUOTENAME(N'DF_'+@table+N'_Id')+N' DEFAULT (NEXT VALUE FOR '+QUOTENAME(@sequence)+N') FOR [Id]'; EXEC sp_executesql @sql;
 FETCH NEXT FROM id_cursor INTO @table,@type;
END;
CLOSE id_cursor; DEALLOCATE id_cursor;
""");

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
IF COL_LENGTH('StockEntries', 'PriceConfirmed') IS NULL
BEGIN
    ALTER TABLE [StockEntries] ADD [PriceConfirmed] bit NOT NULL CONSTRAINT [DF_StockEntries_PriceConfirmed] DEFAULT 0;
    ALTER TABLE [StockEntries] ADD [PriceConfirmedBy] nvarchar(200) NOT NULL CONSTRAINT [DF_StockEntries_PriceConfirmedBy] DEFAULT N'';
    ALTER TABLE [StockEntries] ADD [PriceConfirmedAt] datetime2 NULL;
END;
IF OBJECT_ID(N'[RolePagePermissions]', N'U') IS NULL
BEGIN
    CREATE TABLE [RolePagePermissions](
      [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_RolePagePermissions] PRIMARY KEY,
      [Role] int NOT NULL,[PageKey] nvarchar(100) NOT NULL,[IsAllowed] bit NOT NULL
    );
    CREATE UNIQUE INDEX [IX_RolePagePermissions_Role_PageKey] ON [RolePagePermissions]([Role],[PageKey]);
END;
IF COL_LENGTH('Materials','UnitsNormalized') IS NULL ALTER TABLE [Materials] ADD [UnitsNormalized] bit NOT NULL CONSTRAINT [DF_Materials_UnitsNormalized] DEFAULT 0;
IF COL_LENGTH('StockEntries','EnteredQuantity') IS NULL
BEGIN
 ALTER TABLE [StockEntries] ADD [EnteredQuantity] decimal(18,3) NOT NULL CONSTRAINT [DF_StockEntries_EnteredQuantity] DEFAULT 0;
 ALTER TABLE [StockEntries] ADD [EnteredUnitName] nvarchar(100) NOT NULL CONSTRAINT [DF_StockEntries_EnteredUnitName] DEFAULT N'';
 ALTER TABLE [StockEntries] ADD [ConversionFactor] decimal(18,6) NOT NULL CONSTRAINT [DF_StockEntries_ConversionFactor] DEFAULT 1;
END;
IF COL_LENGTH('StockWithdrawals','EnteredQuantity') IS NULL
BEGIN
 ALTER TABLE [StockWithdrawals] ADD [EnteredQuantity] decimal(18,3) NOT NULL CONSTRAINT [DF_StockWithdrawals_EnteredQuantity] DEFAULT 0;
 ALTER TABLE [StockWithdrawals] ADD [EnteredUnitName] nvarchar(100) NOT NULL CONSTRAINT [DF_StockWithdrawals_EnteredUnitName] DEFAULT N'';
 ALTER TABLE [StockWithdrawals] ADD [ConversionFactor] decimal(18,6) NOT NULL CONSTRAINT [DF_StockWithdrawals_ConversionFactor] DEFAULT 1;
END;
IF OBJECT_ID(N'[MaterialUnitConversions]',N'U') IS NOT NULL AND COL_LENGTH('MaterialUnitConversions','DefinedRefUnit') IS NULL
BEGIN
 ALTER TABLE [MaterialUnitConversions] ADD [DefinedRefUnit] nvarchar(100) NOT NULL CONSTRAINT [DF_MaterialUnitConversions_DefinedRefUnit] DEFAULT N'';
 ALTER TABLE [MaterialUnitConversions] ADD [DefinedCount] decimal(24,6) NOT NULL CONSTRAINT [DF_MaterialUnitConversions_DefinedCount] DEFAULT 1;
 ALTER TABLE [MaterialUnitConversions] ADD [DefinedAmount] decimal(24,6) NOT NULL CONSTRAINT [DF_MaterialUnitConversions_DefinedAmount] DEFAULT 0;
END;
-- Databases created by EF have these columns without defaults; the raw INSERTs below rely on them.
IF COL_LENGTH('MaterialUnitConversions','DefinedRefUnit') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM sys.default_constraints WHERE [name]=N'DF_MaterialUnitConversions_DefinedRefUnit')
BEGIN
 ALTER TABLE [MaterialUnitConversions] ADD CONSTRAINT [DF_MaterialUnitConversions_DefinedRefUnit] DEFAULT N'' FOR [DefinedRefUnit];
 ALTER TABLE [MaterialUnitConversions] ADD CONSTRAINT [DF_MaterialUnitConversions_DefinedCount] DEFAULT 1 FOR [DefinedCount];
 ALTER TABLE [MaterialUnitConversions] ADD CONSTRAINT [DF_MaterialUnitConversions_DefinedAmount] DEFAULT 0 FOR [DefinedAmount];
END;
IF COL_LENGTH('Materials','DisplayUnitName') IS NULL ALTER TABLE [Materials] ADD [DisplayUnitName] nvarchar(100) NOT NULL CONSTRAINT [DF_Materials_DisplayUnitName] DEFAULT N'';
IF COL_LENGTH('StockWithdrawals','Department') IS NULL ALTER TABLE [StockWithdrawals] ADD [Department] nvarchar(200) NOT NULL CONSTRAINT [DF_StockWithdrawals_Department] DEFAULT N'';
IF COL_LENGTH('RecipeIngredients','UnitName') IS NULL ALTER TABLE [RecipeIngredients] ADD [UnitName] nvarchar(100) NOT NULL CONSTRAINT [DF_RecipeIngredients_UnitName] DEFAULT N'';
IF OBJECT_ID(N'[MaterialUnitConversions]',N'U') IS NULL
BEGIN
 CREATE TABLE [MaterialUnitConversions]([Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_MaterialUnitConversions] PRIMARY KEY,[MaterialId] int NOT NULL,[UnitName] nvarchar(100) NOT NULL,[FactorToBaseUnit] decimal(18,6) NOT NULL,[IsLegacyStockUnit] bit NOT NULL,[IsActive] bit NOT NULL,CONSTRAINT [FK_MaterialUnitConversions_Materials] FOREIGN KEY([MaterialId]) REFERENCES [Materials]([Id]) ON DELETE CASCADE);
 CREATE UNIQUE INDEX [IX_MaterialUnitConversions_MaterialId_UnitName] ON [MaterialUnitConversions]([MaterialId],[UnitName]);
END;
IF OBJECT_ID(N'[AppDataMigrations]',N'U') IS NULL CREATE TABLE [AppDataMigrations]([MigrationKey] nvarchar(200) NOT NULL CONSTRAINT [PK_AppDataMigrations] PRIMARY KEY,[AppliedAt] datetime2 NOT NULL);
IF COL_LENGTH('StockWithdrawals', 'WarehouseId') IS NULL
BEGIN
    ALTER TABLE [StockWithdrawals] ADD [WarehouseId] int NULL;
    ALTER TABLE [StockWithdrawals] ADD CONSTRAINT [FK_StockWithdrawals_Warehouses] FOREIGN KEY ([WarehouseId]) REFERENCES [Warehouses]([Id]);
END;
-- MaterialUnitConversions may have just been created above without the definition columns.
IF COL_LENGTH('MaterialUnitConversions','DefinedRefUnit') IS NULL
BEGIN
 ALTER TABLE [MaterialUnitConversions] ADD [DefinedRefUnit] nvarchar(100) NOT NULL CONSTRAINT [DF_MaterialUnitConversions_DefinedRefUnit] DEFAULT N'';
 ALTER TABLE [MaterialUnitConversions] ADD [DefinedCount] decimal(24,6) NOT NULL CONSTRAINT [DF_MaterialUnitConversions_DefinedCount] DEFAULT 1;
 ALTER TABLE [MaterialUnitConversions] ADD [DefinedAmount] decimal(24,6) NOT NULL CONSTRAINT [DF_MaterialUnitConversions_DefinedAmount] DEFAULT 0;
END;
-- Stock-unit model: quantities are counted in Materials.StockUnitName; other units say how many of them make one stock unit.
IF COL_LENGTH('Materials','StockUnitName') IS NULL ALTER TABLE [Materials] ADD [StockUnitName] nvarchar(100) NOT NULL CONSTRAINT [DF_Materials_StockUnitName] DEFAULT N'';
IF COL_LENGTH('MaterialUnitConversions','PerStockUnit') IS NULL ALTER TABLE [MaterialUnitConversions] ADD [PerStockUnit] decimal(28,10) NOT NULL CONSTRAINT [DF_MaterialUnitConversions_PerStockUnit] DEFAULT 1;
IF COL_LENGTH('MaterialUnitConversions','IsApproximate') IS NULL ALTER TABLE [MaterialUnitConversions] ADD [IsApproximate] bit NOT NULL CONSTRAINT [DF_MaterialUnitConversions_IsApproximate] DEFAULT 0;
IF COL_LENGTH('StockEntries','TotalPrice') IS NULL ALTER TABLE [StockEntries] ADD [TotalPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_StockEntries_TotalPrice] DEFAULT 0;
IF OBJECT_ID(N'[AppSettings]',N'U') IS NULL CREATE TABLE [AppSettings]([Key] nvarchar(100) NOT NULL CONSTRAINT [PK_AppSettings] PRIMARY KEY,[Value] nvarchar(max) NOT NULL);
IF OBJECT_ID(N'[SmsLogs]',N'U') IS NULL CREATE TABLE [SmsLogs]([Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_SmsLogs] PRIMARY KEY,[SentAt] datetime2 NOT NULL,[Kind] nvarchar(50) NOT NULL,[Recipient] nvarchar(50) NOT NULL,[Message] nvarchar(max) NOT NULL,[Success] bit NOT NULL,[Response] nvarchar(max) NOT NULL);
IF COL_LENGTH('SmsLogs','MessageId') IS NULL
BEGIN
 ALTER TABLE [SmsLogs] ADD [MessageId] nvarchar(100) NOT NULL CONSTRAINT [DF_SmsLogs_MessageId] DEFAULT N'';
 ALTER TABLE [SmsLogs] ADD [DeliveryStatus] nvarchar(max) NOT NULL CONSTRAINT [DF_SmsLogs_DeliveryStatus] DEFAULT N'';
 ALTER TABLE [SmsLogs] ADD [StatusCheckedAt] datetime2 NULL;
END;
-- Stock-unit quantities need 10 decimals so 10 eggs of a 180-egg carton (0.0555555556 کارتن) add back up exactly.
DECLARE @qtyColumns TABLE([TableName] sysname,[ColumnName] sysname);
INSERT INTO @qtyColumns VALUES(N'StockEntries',N'Quantity'),(N'StockWithdrawals',N'Quantity'),(N'Materials',N'CurrentStock'),(N'Materials',N'MinStockLevel'),(N'WarehouseStocks',N'Quantity'),(N'WarehouseStocks',N'MinStockLevel'),(N'InventoryTransactions',N'IncomingQuantity'),(N'InventoryTransactions',N'OutgoingQuantity'),(N'InventoryTransactions',N'BalanceAfter'),(N'StockEntries',N'ConversionFactor'),(N'StockWithdrawals',N'ConversionFactor');
DECLARE @tableName sysname,@columnName sysname,@constraintName sysname,@sql nvarchar(max);
DECLARE qty_cursor CURSOR LOCAL FAST_FORWARD FOR
 SELECT q.[TableName],q.[ColumnName] FROM @qtyColumns q JOIN sys.columns c ON c.[object_id]=OBJECT_ID(q.[TableName]) AND c.[name]=q.[ColumnName] WHERE c.[scale]<10;
OPEN qty_cursor; FETCH NEXT FROM qty_cursor INTO @tableName,@columnName;
WHILE @@FETCH_STATUS=0
BEGIN
 SET @constraintName=(SELECT TOP(1) dc.[name] FROM sys.default_constraints dc JOIN sys.columns c ON c.[object_id]=dc.[parent_object_id] AND c.[column_id]=dc.[parent_column_id] WHERE dc.[parent_object_id]=OBJECT_ID(@tableName) AND c.[name]=@columnName);
 IF @constraintName IS NOT NULL BEGIN SET @sql=N'ALTER TABLE '+QUOTENAME(@tableName)+N' DROP CONSTRAINT '+QUOTENAME(@constraintName); EXEC sp_executesql @sql; END;
 SET @sql=N'ALTER TABLE '+QUOTENAME(@tableName)+N' ALTER COLUMN '+QUOTENAME(@columnName)+N' decimal(28,10) NOT NULL'; EXEC sp_executesql @sql;
 IF @constraintName IS NOT NULL BEGIN SET @sql=N'ALTER TABLE '+QUOTENAME(@tableName)+N' ADD CONSTRAINT '+QUOTENAME(@constraintName)+N' DEFAULT 0 FOR '+QUOTENAME(@columnName); EXEC sp_executesql @sql; END;
 FETCH NEXT FROM qty_cursor INTO @tableName,@columnName;
END;
CLOSE qty_cursor; DEALLOCATE qty_cursor;
IF OBJECT_ID(N'[Assets]',N'U') IS NULL
 CREATE TABLE [Assets]([Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Assets] PRIMARY KEY,[Name] nvarchar(300) NOT NULL,[Location] nvarchar(200) NOT NULL CONSTRAINT [DF_Assets_Location] DEFAULT N'',[Quantity] decimal(18,3) NOT NULL,[UnitPrice] decimal(18,2) NOT NULL,[PurchaseDate] datetime2 NOT NULL,[Notes] nvarchar(max) NOT NULL CONSTRAINT [DF_Assets_Notes] DEFAULT N'',[CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_Assets_Created] DEFAULT SYSUTCDATETIME(),[CreatedByUsername] nvarchar(200) NOT NULL CONSTRAINT [DF_Assets_CreatedBy] DEFAULT N'');
IF OBJECT_ID(N'[AssetDisposals]',N'U') IS NULL
 CREATE TABLE [AssetDisposals]([Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_AssetDisposals] PRIMARY KEY,[AssetId] int NOT NULL,[Quantity] decimal(18,3) NOT NULL,[DisposalDate] datetime2 NOT NULL,[Reason] nvarchar(max) NOT NULL CONSTRAINT [DF_AssetDisposals_Reason] DEFAULT N'',[CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_AssetDisposals_Created] DEFAULT SYSUTCDATETIME(),[CreatedByUsername] nvarchar(200) NOT NULL CONSTRAINT [DF_AssetDisposals_CreatedBy] DEFAULT N'',CONSTRAINT [FK_AssetDisposals_Assets] FOREIGN KEY([AssetId]) REFERENCES [Assets]([Id]) ON DELETE CASCADE);
""");

        db.Database.ExecuteSqlRaw("""
SET XACT_ABORT ON;
BEGIN TRANSACTION;
BEGIN TRY
-- A database that already has per-warehouse stock but never ran the steps below (warehouses were set up by hand) must skip them:
-- they would create a second "MAIN" warehouse from Materials.CurrentStock and merge it into the real one, doubling every stock.
-- Its quantities are still in each material's stock unit, which the stock-units-v1 step (C#) relies on.
IF NOT EXISTS(SELECT 1 FROM [AppDataMigrations] WHERE [MigrationKey] IN (N'merge-duplicate-warehouses-v2',N'allocate-warehouses-and-rebuild-cardex-v1',N'normalize-units-v1'))
   AND EXISTS(SELECT 1 FROM [WarehouseStocks])
BEGIN
 INSERT INTO [AppDataMigrations] VALUES(N'merge-duplicate-warehouses-v2',SYSUTCDATETIME()),(N'allocate-warehouses-and-rebuild-cardex-v1',SYSUTCDATETIME()),(N'normalize-units-v1',SYSUTCDATETIME()),(N'normalize-units-v1-skipped',SYSUTCDATETIME());
END;
-- A brand-new or pre-warehouse database has nothing in base units yet either.
IF NOT EXISTS(SELECT 1 FROM [AppDataMigrations] WHERE [MigrationKey]=N'normalize-units-v1')
BEGIN
 INSERT INTO [AppDataMigrations] VALUES(N'normalize-units-v1',SYSUTCDATETIME()),(N'normalize-units-v1-skipped',SYSUTCDATETIME());
END;
IF NOT EXISTS(SELECT 1 FROM [AppDataMigrations] WHERE [MigrationKey]=N'merge-duplicate-warehouses-v2')
BEGIN
 IF NOT EXISTS (SELECT 1 FROM [Warehouses] WHERE [Code]=N'MAIN') INSERT INTO [Warehouses] ([Code],[Name]) VALUES (N'MAIN',N'انبار اصلی');
 IF NOT EXISTS (SELECT 1 FROM [Warehouses] WHERE [Code]=N'SECOND') INSERT INTO [Warehouses] ([Code],[Name]) VALUES (N'SECOND',N'انبار دوم');
 IF NOT EXISTS (SELECT 1 FROM [Warehouses] WHERE [Code]=N'BOX') INSERT INTO [Warehouses] ([Code],[Name]) VALUES (N'BOX',N'انبار جعبه');
 IF NOT EXISTS (SELECT 1 FROM [Warehouses] WHERE [Code]=N'STORE') INSERT INTO [Warehouses] ([Code],[Name]) VALUES (N'STORE',N'انبار فروشگاه');
 IF NOT EXISTS (SELECT 1 FROM [Warehouses] WHERE [Code]=N'COLD') INSERT INTO [Warehouses] ([Code],[Name]) VALUES (N'COLD',N'سردخانه');
END;

DECLARE @mainId int=(SELECT TOP(1) [Id] FROM [Warehouses] WHERE [Code]=N'MAIN');
UPDATE [StockEntries] SET [WarehouseId]=@mainId WHERE [WarehouseId] IS NULL;
UPDATE [StockWithdrawals] SET [WarehouseId]=@mainId WHERE [WarehouseId] IS NULL;
IF NOT EXISTS(SELECT 1 FROM [AppDataMigrations] WHERE [MigrationKey]=N'allocate-warehouses-and-rebuild-cardex-v1')
BEGIN
 INSERT INTO [WarehouseStocks] ([WarehouseId],[MaterialId],[Quantity],[MinStockLevel])
 SELECT @mainId,m.[Id],m.[CurrentStock],m.[MinStockLevel]
 FROM [Materials] m
 WHERE NOT EXISTS (SELECT 1 FROM [WarehouseStocks] s WHERE s.[WarehouseId]=@mainId AND s.[MaterialId]=m.[Id]);

 INSERT INTO [InventoryTransactions]
 ([DocumentNumber],[Type],[WarehouseId],[MaterialId],[IncomingQuantity],[OutgoingQuantity],[BalanceAfter],[UnitPrice],[TransactionDate],[Description],[CreatedByUserId],[CreatedByUsername])
 SELECT CONCAT(N'OPEN-',m.[Code]),0,@mainId,m.[Id],m.[CurrentStock],0,m.[CurrentStock],m.[PricePerUnit],SYSUTCDATETIME(),N'انتقال مانده اولیه هنگام فعال‌سازی چند انبار',0,N'system'
 FROM [Materials] m
 WHERE m.[CurrentStock]<>0 AND NOT EXISTS (SELECT 1 FROM [InventoryTransactions] t WHERE t.[DocumentNumber]=CONCAT(N'OPEN-',m.[Code]));
END;

DECLARE @pages TABLE([PageKey] nvarchar(100));
INSERT INTO @pages VALUES (N'dashboard'),(N'materials'),(N'archive'),(N'warehouses'),(N'unit-config'),(N'definitions'),(N'entry'),(N'withdrawal'),(N'transfer'),(N'warehouse-stock'),(N'cardex'),(N'history'),(N'prices'),(N'recipes'),(N'alerts'),(N'users'),(N'assets');
DECLARE @roles TABLE([Role] int); INSERT INTO @roles VALUES(0),(1),(2),(3),(4);
INSERT INTO [RolePagePermissions]([Role],[PageKey],[IsAllowed])
SELECT r.[Role],p.[PageKey],CASE
 WHEN r.[Role]=0 THEN 1
 WHEN r.[Role]=1 AND p.[PageKey]<>N'users' THEN 1
 WHEN r.[Role]=2 AND p.[PageKey] IN(N'dashboard',N'materials',N'definitions',N'withdrawal',N'transfer',N'warehouse-stock',N'cardex',N'history',N'prices',N'recipes',N'alerts') THEN 1
 WHEN r.[Role]=3 AND p.[PageKey] IN(N'dashboard',N'materials',N'withdrawal',N'transfer',N'warehouse-stock',N'cardex',N'history',N'alerts') THEN 1
 WHEN r.[Role]=4 AND p.[PageKey] IN(N'dashboard',N'materials',N'warehouse-stock',N'cardex',N'history',N'prices',N'recipes',N'alerts') THEN 1
 ELSE 0 END
FROM @roles r CROSS JOIN @pages p
WHERE NOT EXISTS(SELECT 1 FROM [RolePagePermissions] x WHERE x.[Role]=r.[Role] AND x.[PageKey]=p.[PageKey]);

IF NOT EXISTS(SELECT 1 FROM [AppDataMigrations] WHERE [MigrationKey]=N'normalize-units-v1')
BEGIN
 INSERT INTO [MaterialUnitConversions]([MaterialId],[UnitName],[FactorToBaseUnit],[IsLegacyStockUnit],[IsActive])
 SELECT m.[Id],ISNULL(NULLIF(u.[Name],N''),N'واحد'),CASE WHEN m.[BaseQuantity]>0 THEN m.[BaseQuantity] ELSE 1 END,1,1 FROM [Materials] m LEFT JOIN [Units] u ON u.[Id]=m.[UnitId]
 WHERE NOT EXISTS(SELECT 1 FROM [MaterialUnitConversions] c WHERE c.[MaterialId]=m.[Id] AND c.[UnitName]=ISNULL(NULLIF(u.[Name],N''),N'واحد'));
 INSERT INTO [MaterialUnitConversions]([MaterialId],[UnitName],[FactorToBaseUnit],[IsLegacyStockUnit],[IsActive])
 SELECT m.[Id],m.[BaseUnitName],1,0,1 FROM [Materials] m WHERE m.[BaseUnitName]<>N'' AND NOT EXISTS(SELECT 1 FROM [MaterialUnitConversions] c WHERE c.[MaterialId]=m.[Id] AND c.[UnitName]=m.[BaseUnitName]);
 UPDATE e SET [EnteredQuantity]=e.[Quantity],[EnteredUnitName]=ISNULL(NULLIF(u.[Name],N''),N'واحد'),[ConversionFactor]=CASE WHEN m.[BaseQuantity]>0 THEN m.[BaseQuantity] ELSE 1 END,[Quantity]=e.[Quantity]*(CASE WHEN m.[BaseQuantity]>0 THEN m.[BaseQuantity] ELSE 1 END) FROM [StockEntries] e JOIN [Materials] m ON m.[Id]=e.[MaterialId] LEFT JOIN [Units] u ON u.[Id]=m.[UnitId];
 UPDATE w SET [EnteredQuantity]=w.[Quantity],[EnteredUnitName]=ISNULL(NULLIF(u.[Name],N''),N'واحد'),[ConversionFactor]=CASE WHEN m.[BaseQuantity]>0 THEN m.[BaseQuantity] ELSE 1 END,[Quantity]=w.[Quantity]*(CASE WHEN m.[BaseQuantity]>0 THEN m.[BaseQuantity] ELSE 1 END) FROM [StockWithdrawals] w JOIN [Materials] m ON m.[Id]=w.[MaterialId] LEFT JOIN [Units] u ON u.[Id]=m.[UnitId];
 UPDATE t SET [IncomingQuantity]=t.[IncomingQuantity]*(CASE WHEN m.[BaseQuantity]>0 THEN m.[BaseQuantity] ELSE 1 END),[OutgoingQuantity]=t.[OutgoingQuantity]*(CASE WHEN m.[BaseQuantity]>0 THEN m.[BaseQuantity] ELSE 1 END),[BalanceAfter]=t.[BalanceAfter]*(CASE WHEN m.[BaseQuantity]>0 THEN m.[BaseQuantity] ELSE 1 END),[UnitPrice]=t.[UnitPrice]/(CASE WHEN m.[BaseQuantity]>0 THEN m.[BaseQuantity] ELSE 1 END) FROM [InventoryTransactions] t JOIN [Materials] m ON m.[Id]=t.[MaterialId];
 UPDATE ri SET [UnitName]=m.[BaseUnitName] FROM [RecipeIngredients] ri JOIN [Materials] m ON m.[Id]=ri.[MaterialId] WHERE ri.[UnitName]=N'';
 UPDATE ws SET [Quantity]=ws.[Quantity]*(CASE WHEN m.[BaseQuantity]>0 THEN m.[BaseQuantity] ELSE 1 END),[MinStockLevel]=ws.[MinStockLevel]*(CASE WHEN m.[BaseQuantity]>0 THEN m.[BaseQuantity] ELSE 1 END) FROM [WarehouseStocks] ws JOIN [Materials] m ON m.[Id]=ws.[MaterialId];
 UPDATE [Materials] SET [CurrentStock]=[CurrentStock]*(CASE WHEN [BaseQuantity]>0 THEN [BaseQuantity] ELSE 1 END),[MinStockLevel]=[MinStockLevel]*(CASE WHEN [BaseQuantity]>0 THEN [BaseQuantity] ELSE 1 END),[UnitsNormalized]=1;
 INSERT INTO [AppDataMigrations] VALUES(N'normalize-units-v1',SYSUTCDATETIME());
END;

IF NOT EXISTS(SELECT 1 FROM [AppDataMigrations] WHERE [MigrationKey]=N'allocate-warehouses-and-rebuild-cardex-v1')
BEGIN
 DECLARE @main int=(SELECT [Id] FROM [Warehouses] WHERE [Code]=N'MAIN'),@box int=(SELECT [Id] FROM [Warehouses] WHERE [Code]=N'BOX'),@store int=(SELECT [Id] FROM [Warehouses] WHERE [Code]=N'STORE');
 UPDATE e SET [WarehouseId]=CASE WHEN m.[Code] BETWEEN N'05000' AND N'05016' THEN @box WHEN m.[Code] BETWEEN N'07001' AND N'07005' THEN @store ELSE @main END FROM [StockEntries] e JOIN [Materials] m ON m.[Id]=e.[MaterialId];
 UPDATE w SET [WarehouseId]=CASE WHEN m.[Code] BETWEEN N'05000' AND N'05016' THEN @box WHEN m.[Code] BETWEEN N'07001' AND N'07005' THEN @store ELSE @main END FROM [StockWithdrawals] w JOIN [Materials] m ON m.[Id]=w.[MaterialId];
 UPDATE t SET [WarehouseId]=CASE WHEN m.[Code] BETWEEN N'05000' AND N'05016' THEN @box WHEN m.[Code] BETWEEN N'07001' AND N'07005' THEN @store ELSE @main END FROM [InventoryTransactions] t JOIN [Materials] m ON m.[Id]=t.[MaterialId] WHERE t.[Type] IN(1,2);
 INSERT INTO [WarehouseStocks]([WarehouseId],[MaterialId],[Quantity],[MinStockLevel]) SELECT CASE WHEN m.[Code] BETWEEN N'05000' AND N'05016' THEN @box WHEN m.[Code] BETWEEN N'07001' AND N'07005' THEN @store ELSE @main END,m.[Id],0,m.[MinStockLevel] FROM [Materials] m WHERE NOT EXISTS(SELECT 1 FROM [WarehouseStocks] ws WHERE ws.[MaterialId]=m.[Id] AND ws.[WarehouseId]=CASE WHEN m.[Code] BETWEEN N'05000' AND N'05016' THEN @box WHEN m.[Code] BETWEEN N'07001' AND N'07005' THEN @store ELSE @main END);
 UPDATE target SET target.[Quantity]=target.[Quantity]+source.[Quantity] FROM [WarehouseStocks] target JOIN [Materials] m ON m.[Id]=target.[MaterialId] JOIN [WarehouseStocks] source ON source.[MaterialId]=target.[MaterialId] AND source.[WarehouseId]=@main AND source.[Id]<>target.[Id] WHERE target.[WarehouseId]=CASE WHEN m.[Code] BETWEEN N'05000' AND N'05016' THEN @box WHEN m.[Code] BETWEEN N'07001' AND N'07005' THEN @store ELSE @main END;
 DELETE source FROM [WarehouseStocks] source JOIN [Materials] m ON m.[Id]=source.[MaterialId] WHERE source.[WarehouseId]=@main AND (m.[Code] BETWEEN N'05000' AND N'05016' OR m.[Code] BETWEEN N'07001' AND N'07005');
 DECLARE @cutover datetime2=ISNULL((SELECT MIN([CreatedAt]) FROM [InventoryTransactions] WHERE [DocumentNumber] LIKE N'OPEN-%'),SYSUTCDATETIME());
 DELETE FROM [InventoryTransactions] WHERE [DocumentNumber] LIKE N'OPEN-%' OR [DocumentNumber] LIKE N'LEGACY-%' OR [DocumentNumber] LIKE N'RECON-%' OR [Type]=5;
 ;WITH ev AS (
  SELECT CONCAT(N'LEGACY-IN-',e.[Id]) doc,1 typ,e.[WarehouseId] wid,e.[MaterialId] mid,e.[Quantity] incoming,CAST(0 AS decimal(18,3)) outgoing,e.[PricePerUnit]/CASE WHEN e.[ConversionFactor]>0 THEN e.[ConversionFactor] ELSE 1 END price,e.[EntryDate] dt,e.[Notes] descr,e.[CreatedByUserId] uid,e.[CreatedByUsername] uname,e.[CreatedAt] created,e.[Id] sourceid FROM [StockEntries] e WHERE e.[CreatedAt]<=@cutover
  UNION ALL SELECT CONCAT(N'LEGACY-OUT-',w.[Id]),2,w.[WarehouseId],w.[MaterialId],0,w.[Quantity],CAST(0 AS decimal(18,2)),w.[WithdrawalDate],w.[Reason],w.[CreatedByUserId],w.[CreatedByUsername],w.[CreatedAt],w.[Id] FROM [StockWithdrawals] w WHERE w.[Status]=1 AND w.[CreatedAt]<=@cutover
 ), balances AS (SELECT *,SUM(incoming-outgoing) OVER(PARTITION BY wid,mid ORDER BY dt,typ,sourceid ROWS UNBOUNDED PRECEDING) bal FROM ev)
 INSERT INTO [InventoryTransactions]([DocumentNumber],[Type],[WarehouseId],[MaterialId],[IncomingQuantity],[OutgoingQuantity],[BalanceAfter],[UnitPrice],[TransactionDate],[Description],[CreatedByUserId],[CreatedByUsername],[CreatedAt]) SELECT doc,typ,wid,mid,incoming,outgoing,bal,price,dt,descr,uid,uname,created FROM balances;
 ;WITH hist AS(SELECT [WarehouseId],[MaterialId],SUM([IncomingQuantity]-[OutgoingQuantity]) qty FROM [InventoryTransactions] WHERE [DocumentNumber] LIKE N'LEGACY-%' GROUP BY [WarehouseId],[MaterialId]), postcutover AS(SELECT [WarehouseId],[MaterialId],SUM([IncomingQuantity]-[OutgoingQuantity]) qty FROM [InventoryTransactions] WHERE [CreatedAt]>@cutover AND [DocumentNumber] NOT LIKE N'LEGACY-%' AND [DocumentNumber] NOT LIKE N'RECON-%' GROUP BY [WarehouseId],[MaterialId])
 INSERT INTO [InventoryTransactions]([DocumentNumber],[Type],[WarehouseId],[MaterialId],[IncomingQuantity],[OutgoingQuantity],[BalanceAfter],[UnitPrice],[TransactionDate],[Description],[CreatedByUserId],[CreatedByUsername])
 SELECT CONCAT(N'RECON-',m.[Code],N'-',ws.[WarehouseId]),5,ws.[WarehouseId],ws.[MaterialId],CASE WHEN ws.[Quantity]-ISNULL(p.qty,0)-ISNULL(h.qty,0)>0 THEN ws.[Quantity]-ISNULL(p.qty,0)-ISNULL(h.qty,0) ELSE 0 END,CASE WHEN ws.[Quantity]-ISNULL(p.qty,0)-ISNULL(h.qty,0)<0 THEN -(ws.[Quantity]-ISNULL(p.qty,0)-ISNULL(h.qty,0)) ELSE 0 END,ws.[Quantity]-ISNULL(p.qty,0),m.[PricePerUnit],@cutover,N'اصلاح شفاف مغایرت مانده با سوابق قدیمی',0,N'system' FROM [WarehouseStocks] ws JOIN [Materials] m ON m.[Id]=ws.[MaterialId] LEFT JOIN hist h ON h.[WarehouseId]=ws.[WarehouseId] AND h.[MaterialId]=ws.[MaterialId] LEFT JOIN postcutover p ON p.[WarehouseId]=ws.[WarehouseId] AND p.[MaterialId]=ws.[MaterialId] WHERE ws.[Quantity]-ISNULL(p.qty,0)<>ISNULL(h.qty,0);
 INSERT INTO [AppDataMigrations] VALUES(N'allocate-warehouses-and-rebuild-cardex-v1',SYSUTCDATETIME());
END;

IF NOT EXISTS(SELECT 1 FROM [AppDataMigrations] WHERE [MigrationKey]=N'merge-duplicate-warehouses-v2')
BEGIN
 DECLARE @warehouseMap TABLE([SourceId] int,[TargetId] int);
 INSERT INTO @warehouseMap
 SELECT s.[Id],t.[Id] FROM (VALUES(N'MAIN',N'010000'),(N'SECOND',N'020000'),(N'BOX',N'030000'),(N'STORE',N'040000'),(N'COLD',N'050000')) v([SourceCode],[TargetCode])
 JOIN [Warehouses] s ON s.[Code]=v.[SourceCode]
 JOIN [Warehouses] t ON t.[Code]=v.[TargetCode] AND t.[Id]<>s.[Id];

 UPDATE target SET target.[Quantity]=target.[Quantity]+source.[Quantity],target.[MinStockLevel]=CASE WHEN target.[MinStockLevel]>source.[MinStockLevel] THEN target.[MinStockLevel] ELSE source.[MinStockLevel] END
 FROM [WarehouseStocks] source JOIN @warehouseMap map ON map.[SourceId]=source.[WarehouseId]
 JOIN [WarehouseStocks] target ON target.[WarehouseId]=map.[TargetId] AND target.[MaterialId]=source.[MaterialId];
 DELETE source FROM [WarehouseStocks] source JOIN @warehouseMap map ON map.[SourceId]=source.[WarehouseId]
 WHERE EXISTS(SELECT 1 FROM [WarehouseStocks] target WHERE target.[WarehouseId]=map.[TargetId] AND target.[MaterialId]=source.[MaterialId]);
 UPDATE source SET source.[WarehouseId]=map.[TargetId] FROM [WarehouseStocks] source JOIN @warehouseMap map ON map.[SourceId]=source.[WarehouseId];
 UPDATE e SET e.[WarehouseId]=map.[TargetId] FROM [StockEntries] e JOIN @warehouseMap map ON map.[SourceId]=e.[WarehouseId];
 UPDATE w SET w.[WarehouseId]=map.[TargetId] FROM [StockWithdrawals] w JOIN @warehouseMap map ON map.[SourceId]=w.[WarehouseId];
 UPDATE t SET t.[WarehouseId]=map.[TargetId] FROM [InventoryTransactions] t JOIN @warehouseMap map ON map.[SourceId]=t.[WarehouseId];
 UPDATE t SET t.[RelatedWarehouseId]=map.[TargetId] FROM [InventoryTransactions] t JOIN @warehouseMap map ON map.[SourceId]=t.[RelatedWarehouseId];
 ;WITH recalculated AS(SELECT [Id],SUM([IncomingQuantity]-[OutgoingQuantity]) OVER(PARTITION BY [WarehouseId],[MaterialId] ORDER BY [TransactionDate],[Id] ROWS UNBOUNDED PRECEDING) AS [NewBalance] FROM [InventoryTransactions])
 UPDATE t SET t.[BalanceAfter]=r.[NewBalance] FROM [InventoryTransactions] t JOIN recalculated r ON r.[Id]=t.[Id];
 DELETE w FROM [Warehouses] w JOIN @warehouseMap map ON map.[SourceId]=w.[Id];
 INSERT INTO [AppDataMigrations] VALUES(N'merge-duplicate-warehouses-v2',SYSUTCDATETIME());
END;

-- Superseded by stock-units-v1, which gives every material its units.
IF NOT EXISTS(SELECT 1 FROM [AppDataMigrations] WHERE [MigrationKey]=N'stock-units-v1')
BEGIN
INSERT INTO [MaterialUnitConversions]([MaterialId],[UnitName],[FactorToBaseUnit],[IsLegacyStockUnit],[IsActive])
SELECT m.[Id],u.[Name],CASE WHEN m.[BaseQuantity]>0 THEN m.[BaseQuantity] ELSE 1 END,1,1 FROM [Materials] m JOIN [Units] u ON u.[Id]=m.[UnitId]
WHERE u.[Name]<>N'' AND NOT EXISTS(SELECT 1 FROM [MaterialUnitConversions] c WHERE c.[MaterialId]=m.[Id] AND (c.[IsActive]=1 OR c.[UnitName]=u.[Name]));
INSERT INTO [MaterialUnitConversions]([MaterialId],[UnitName],[FactorToBaseUnit],[IsLegacyStockUnit],[IsActive])
SELECT m.[Id],m.[BaseUnitName],1,CASE WHEN EXISTS(SELECT 1 FROM [MaterialUnitConversions] c WHERE c.[MaterialId]=m.[Id] AND c.[IsLegacyStockUnit]=1) THEN 0 ELSE 1 END,1 FROM [Materials] m
WHERE m.[BaseUnitName]<>N'' AND NOT EXISTS(SELECT 1 FROM [MaterialUnitConversions] c WHERE c.[MaterialId]=m.[Id] AND c.[UnitName]=m.[BaseUnitName]);
END;
COMMIT TRANSACTION;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
""");

        StockUnitMigration.Run(db);
    }
}
