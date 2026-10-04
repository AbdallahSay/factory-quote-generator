-- =====================================================================
-- MonsterASP.NET MS SQL Server: Complete Drop & Re-Create Script
-- Decoupled Architecture: Friction-Free Quote Generation System
-- Target: Microsoft SQL Server 2019 / 2022
-- =====================================================================

-- =====================================================================
-- STEP 1: DROP TABLES IN DEPENDENCY ORDER TO PREVENT FK CONFLICTS
-- =====================================================================
PRINT '--- Step 1: Dropping Existing Tables (In Dependency Order) ---';

IF OBJECT_ID(N'dbo.AuditLogs', N'U') IS NOT NULL
BEGIN
    DROP TABLE [dbo].[AuditLogs];
    PRINT 'Dropped [dbo].[AuditLogs].';
END;

IF OBJECT_ID(N'dbo.QuoteItems', N'U') IS NOT NULL
BEGIN
    DROP TABLE [dbo].[QuoteItems];
    PRINT 'Dropped [dbo].[QuoteItems].';
END;

IF OBJECT_ID(N'dbo.Quotes', N'U') IS NOT NULL
BEGIN
    DROP TABLE [dbo].[Quotes];
    PRINT 'Dropped [dbo].[Quotes].';
END;

IF OBJECT_ID(N'dbo.Products', N'U') IS NOT NULL
BEGIN
    DROP TABLE [dbo].[Products];
    PRINT 'Dropped [dbo].[Products].';
END;

IF OBJECT_ID(N'dbo.Clients', N'U') IS NOT NULL
BEGIN
    DROP TABLE [dbo].[Clients];
    PRINT 'Dropped [dbo].[Clients].';
END;

IF OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL
BEGIN
    DROP TABLE [dbo].[Users];
    PRINT 'Dropped [dbo].[Users].';
END;
GO

-- =====================================================================
-- STEP 2: CREATE NEW SIMPLIFIED SCHEMA FROM SCRATCH
-- =====================================================================
PRINT '--- Step 2: Creating Tables and Constraints ---';

-- 1. Users Table
CREATE TABLE [dbo].[Users] (
    [Id] INT IDENTITY(1,1) NOT NULL,
    [FullName] NVARCHAR(150) NOT NULL,
    [Email] NVARCHAR(150) NOT NULL,
    [PasswordHash] NVARCHAR(MAX) NOT NULL,
    [Role] NVARCHAR(50) NOT NULL CONSTRAINT [DF_Users_Role] DEFAULT ('Sales'),
    [IsActive] BIT NOT NULL CONSTRAINT [DF_Users_IsActive] DEFAULT (1),
    [CreatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_Users_CreatedAt] DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT [PK_Users] PRIMARY KEY CLUSTERED ([Id] ASC)
);

CREATE UNIQUE NONCLUSTERED INDEX [IX_Users_Email] ON [dbo].[Users] ([Email] ASC);
PRINT 'Created [dbo].[Users].';
GO

-- 2. Clients Table (Standalone / Lookup Suggestions)
CREATE TABLE [dbo].[Clients] (
    [Id] INT IDENTITY(1,1) NOT NULL,
    [ClientName] NVARCHAR(200) NOT NULL,
    [Phone] NVARCHAR(50) NOT NULL CONSTRAINT [DF_Clients_Phone] DEFAULT (''),
    [Email] NVARCHAR(150) NOT NULL CONSTRAINT [DF_Clients_Email] DEFAULT (''),
    [ContactPerson] NVARCHAR(150) NOT NULL CONSTRAINT [DF_Clients_ContactPerson] DEFAULT (''),
    [Address] NVARCHAR(250) NOT NULL CONSTRAINT [DF_Clients_Address] DEFAULT (''),
    [CreatedById] INT NULL,
    [CreatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_Clients_CreatedAt] DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT [PK_Clients] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_Clients_Users_CreatedById] FOREIGN KEY ([CreatedById]) REFERENCES [dbo].[Users] ([Id])
);

CREATE NONCLUSTERED INDEX [IX_Clients_CreatedById] ON [dbo].[Clients] ([CreatedById] ASC);
PRINT 'Created [dbo].[Clients].';
GO

-- 3. Products Table (Standalone / Lookup Suggestions)
CREATE TABLE [dbo].[Products] (
    [Id] INT IDENTITY(1,1) NOT NULL,
    [ProductName] NVARCHAR(200) NOT NULL,
    [Size] NVARCHAR(100) NOT NULL CONSTRAINT [DF_Products_Size] DEFAULT (''),
    [Capacity] NVARCHAR(100) NOT NULL CONSTRAINT [DF_Products_Capacity] DEFAULT (''),
    [DefaultPrice] DECIMAL(18,2) NOT NULL CONSTRAINT [DF_Products_DefaultPrice] DEFAULT (0.00),
    [IsActive] BIT NOT NULL CONSTRAINT [DF_Products_IsActive] DEFAULT (1),
    CONSTRAINT [PK_Products] PRIMARY KEY CLUSTERED ([Id] ASC)
);
PRINT 'Created [dbo].[Products].';
GO

-- 4. Quotes Table (Direct ClientName, Optional Phone & Email, No Client FK)
CREATE TABLE [dbo].[Quotes] (
    [Id] INT IDENTITY(1,1) NOT NULL,
    [QuoteNumber] NVARCHAR(50) NOT NULL,
    [UserId] INT NOT NULL,
    [ClientName] NVARCHAR(200) NOT NULL CONSTRAINT [DF_Quotes_ClientName] DEFAULT (''),
    [ClientPhone] NVARCHAR(50) NULL,
    [ClientEmail] NVARCHAR(150) NULL,
    [ContactPerson] NVARCHAR(150) NOT NULL CONSTRAINT [DF_Quotes_ContactPerson] DEFAULT (''),
    [ProjectName] NVARCHAR(200) NOT NULL CONSTRAINT [DF_Quotes_ProjectName] DEFAULT (''),
    [Location] NVARCHAR(200) NOT NULL CONSTRAINT [DF_Quotes_Location] DEFAULT (''),
    [TotalAmount] DECIMAL(18,2) NOT NULL,
    [PdfUrl] NVARCHAR(500) NULL,
    [DocxUrl] NVARCHAR(500) NULL,
    [CreatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_Quotes_CreatedAt] DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT [PK_Quotes] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_Quotes_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([Id])
);

CREATE UNIQUE NONCLUSTERED INDEX [IX_Quotes_QuoteNumber] ON [dbo].[Quotes] ([QuoteNumber] ASC);
CREATE NONCLUSTERED INDEX [IX_Quotes_UserId] ON [dbo].[Quotes] ([UserId] ASC);
PRINT 'Created [dbo].[Quotes].';
GO

-- 5. QuoteItems Table (Direct ProductName, No Product FK)
CREATE TABLE [dbo].[QuoteItems] (
    [Id] INT IDENTITY(1,1) NOT NULL,
    [QuoteId] INT NOT NULL,
    [ProductName] NVARCHAR(200) NOT NULL CONSTRAINT [DF_QuoteItems_ProductName] DEFAULT (''),
    [Size] NVARCHAR(100) NOT NULL CONSTRAINT [DF_QuoteItems_Size] DEFAULT (''),
    [Capacity] NVARCHAR(100) NOT NULL CONSTRAINT [DF_QuoteItems_Capacity] DEFAULT (''),
    [Quantity] DECIMAL(18,2) NOT NULL,
    [UnitPrice] DECIMAL(18,2) NOT NULL,
    [LineTotal] DECIMAL(18,2) NOT NULL,
    CONSTRAINT [PK_QuoteItems] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_QuoteItems_Quotes_QuoteId] FOREIGN KEY ([QuoteId]) REFERENCES [dbo].[Quotes] ([Id]) ON DELETE CASCADE
);

CREATE NONCLUSTERED INDEX [IX_QuoteItems_QuoteId] ON [dbo].[QuoteItems] ([QuoteId] ASC);
PRINT 'Created [dbo].[QuoteItems].';
GO

-- 6. AuditLogs Table
CREATE TABLE [dbo].[AuditLogs] (
    [Id] INT IDENTITY(1,1) NOT NULL,
    [UserId] INT NOT NULL,
    [Action] NVARCHAR(100) NOT NULL,
    [EntityId] NVARCHAR(100) NULL,
    [Timestamp] DATETIME2(7) NOT NULL CONSTRAINT [DF_AuditLogs_Timestamp] DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT [PK_AuditLogs] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_AuditLogs_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([Id])
);

CREATE NONCLUSTERED INDEX [IX_AuditLogs_UserId] ON [dbo].[AuditLogs] ([UserId] ASC);
PRINT 'Created [dbo].[AuditLogs].';
GO

-- =====================================================================
-- STEP 3: SEED INITIAL SYSTEM DATA
-- =====================================================================
PRINT '--- Step 3: Seeding Initial Data ---';

-- Seed Administrative & Sales Users
INSERT INTO [dbo].[Users] ([FullName], [Email], [PasswordHash], [Role], [IsActive])
VALUES 
    (N'مدير النظام', 'admin@factory-eg.com', 'admin_hash_123', 'Admin', 1),
    (N'مسؤول المبيعات', 'sales@factory-eg.com', 'sales_hash_123', 'Sales', 1);

DECLARE @SalesUserId INT = (SELECT TOP 1 [Id] FROM [dbo].[Users] WHERE [Role] = 'Sales');

-- Seed Standard Factory Products
INSERT INTO [dbo].[Products] ([ProductName], [Size], [Capacity], [DefaultPrice], [IsActive])
VALUES 
    (N'طوب الي مصمت', N'25*12*6', N'1000', 1450.00, 1),
    (N'طوب مصمت عادي', N'25*12*6', N'1000', 1300.00, 1),
    (N'بلوك خرساني مفرغ', N'40*20*20', N'500', 3200.00, 1),
    (N'بردورة رصيف خرسانية', N'50*30*15', N'200', 95.00, 1),
    (N'إنترلوك سداسي مضلع', N'6 سم ملون', N'1000', 185.00, 1);

-- Seed Initial Client Contacts for Suggestions
INSERT INTO [dbo].[Clients] ([ClientName], [Phone], [Email], [ContactPerson], [Address], [CreatedById])
VALUES 
    (N'شركة اتريم للمقاولات والاعمال المتخصصة', N'+201012345678', N'sara@atreem.com', N'م / سارة شريف', N'القاهرة - التجمع الخامس', @SalesUserId),
    (N'شركة النصر للتجارة والمقاولات', N'+201198765432', N'ahmed@elnasr.com', N'م / أحمد فؤاد', N'الجيزة - مدينة 6 أكتوبر', @SalesUserId),
    (N'شركة الرواد للاستثمار العقاري', N'+201055566677', N'info@alrowad.com', N'م / كريم الدسوقي', N'العاصمة الإدارية الجديدة', @SalesUserId);

PRINT '=====================================================================';
PRINT 'MonsterASP.NET Database Reset & Schema Creation Completed Successfully!';
PRINT '=====================================================================';
GO
