# سیستم مدیریت انبار - Warehouse Management System

یک سیستم کامل مدیریت مواد اولیه انبار با تکنولوژی Blazor Server و .NET 8

---

## 🚀 اجرا با Docker (توصیه شده)

### پیش‌نیازها
- Docker Desktop یا Docker Engine
- Docker Compose

### مراحل اجرا

```bash
# ۱. وارد پوشه پروژه شوید
cd WarehouseApp

# ۲. Build و اجرا
docker-compose up -d --build

# ۳. مشاهده لاگ‌ها
docker-compose logs -f
```

برنامه روی آدرس زیر در دسترس است:
```
http://[IP-سرور]:8080
```

---

## 👤 کاربران پیش‌فرض

| نام کاربری | رمز عبور | سطح دسترسی |
|------------|----------|------------|
| admin | admin123 | مدیر (کامل) |
| operator | op123 | اپراتور |

**⚠️ حتماً رمز عبور پیش‌فرض را تغییر دهید!**

---

## 🔐 سطوح دسترسی

### مدیر (Admin)
- مدیریت مواد اولیه (افزودن، ویرایش، حذف)
- تعیین قیمت و حد هشدار موجودی
- ثبت ورود مواد به انبار با تاریخ ورود و انقضا
- مشاهده تمام گزارشات و هشدارها

### اپراتور (Operator)
- ثبت خروج مواد از انبار
- مشاهده موجودی و تاریخچه
- مشاهده هشدارها

---

## 📦 امکانات سیستم

- **داشبورد**: نمای کلی موجودی، ارزش انبار، هشدارهای فعال
- **مواد اولیه**: مدیریت کامل مواد با واحد و قیمت
- **واحدهای کالا**: هر کالا یک «واحد موجودی» دارد که انبار با آن شمرده می‌شود (مثلاً کارتن) و یک «واحد رسپی» برای قیمت و هزینه (مثلاً کیلوگرم).
  بقیه‌ی واحدها نسبت به هم تعریف می‌شوند: ۱ کارتن = ۶ شانه، ۱ شانه = ۳۰ عدد، ۱ عدد ≈ ۷۰ گرم (تقریبی).
  رابطه‌های تقریبی فقط روی قیمت اثر دارند و موجودی و سوابق را تغییر نمی‌دهند.
- **ورود به انبار**: ثبت مقدار + واحد + مبلغ کل فاکتور؛ قیمت هر کارتن، شانه، عدد و کیلو خودکار نمایش داده می‌شود
- **خروج از انبار**: ثبت برداشت با نام کاربر و تاریخ
- **هشدارها**:
  - موجودی کم‌تر از حد تعریف‌شده
  - مواد در آستانه انقضا (کمتر از ۷ روز)
  - مواد منقضی‌شده
- **تاریخچه**: تمام ورودی‌ها و خروجی‌ها

---

## 💾 ذخیره‌سازی داده

داده‌ها در SQL Server (کانتینر `sql2019` روی شبکه‌ی `shared-network`) نگه داشته می‌شوند. رشته‌ی اتصال در `docker-compose.yml` است.

هنگام اجرای نسخه‌ی جدید، ساختار دیتابیس و داده‌ها خودکار ارتقا داده می‌شوند (`Data/DatabaseSchemaUpgrader.cs` و `Data/StockUnitMigration.cs`).
**قبل از به‌روزرسانی حتماً بکاپ بگیرید:**
```bash
docker exec sql2019 /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P '<password>' -C \
  -Q "BACKUP DATABASE WarehouseDB TO DISK='/var/opt/mssql/backup/WarehouseDB_before_update.bak' WITH INIT"
```

---

## 🔧 توقف و راه‌اندازی مجدد

```bash
# توقف
docker-compose down

# راه‌اندازی مجدد (بدون از دست دادن داده)
docker-compose up -d

# حذف کامل با داده‌ها
docker-compose down -v
```

---

## 🛠️ اجرا بدون Docker (توسعه)

```bash
dotnet restore
dotnet run
```
آدرس: `http://localhost:5000`

---

## ساختار پروژه

```
WarehouseApp/
├── Models/          # مدل‌های داده
├── Data/            # DbContext و مقداردهی اولیه
├── Services/        # سرویس‌های تجاری
├── Shared/          # کامپوننت‌های Blazor
│   ├── LoginPage.razor
│   ├── MainLayout.razor
│   ├── DashboardPage.razor
│   ├── MaterialsPage.razor
│   ├── StockEntryPage.razor
│   ├── WithdrawalPage.razor
│   ├── AlertsPage.razor
│   └── HistoryPage.razor
├── wwwroot/css/     # استایل‌ها
├── Dockerfile
├── docker-compose.yml
└── Program.cs
```
