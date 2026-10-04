# 🏭 Factory Quotation Generation & Dispatch System
### نظام إصدار وإرسال عروض الأسعار الذكي لمصانع البناء والتشييد

[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![ASP.NET Core Web API](https://img.shields.io/badge/ASP.NET_Core-Web_API-239120?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/apps/aspnet)
[![SQL Server](https://img.shields.io/badge/SQL_Server-MSSQL-CC292B?logo=microsoft-sql-server&logoColor=white)](https://www.microsoft.com/sql-server)
[![Vercel Frontend](https://img.shields.io/badge/Frontend-Vercel-black?logo=vercel&logoColor=white)](https://vercel.com)
[![MonsterASP.NET](https://img.shields.io/badge/Hosting-MonsterASP.NET-blue?logo=windows&logoColor=white)](https://monsterasp.net)
[![GitHub Actions CI/CD](https://img.shields.io/badge/CI%2FCD-GitHub_Actions-2088FF?logo=github-actions&logoColor=white)](https://github.com/features/actions)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

---

## 🌟 Overview | نظرة عامة

**English:**  
The **Factory Quotation Generation System** is an end-to-end, enterprise-grade open-source solution designed for industrial manufacturers, concrete factories, and building material suppliers. It enables sales representatives to quickly generate professional Word (`.docx`) and PDF quotation documents directly from customizable templates, calculate line totals automatically, store audit records in Microsoft SQL Server, and fire asynchronous background notifications (Email with PDF attachment and WhatsApp automation webhooks).

**العربية:**  
**منظومة إصدار عروض الأسعار للمصانع** هي منصة مفتوحة المصدر عالية الأداء مخصصة لمصانع الطوب الآلي والخرسانة وشركات المقاولات والتوريدات. تتيح لمندوبي المبيعات إصدار عروض أسعار رسمية معتمدة بصيغتي Word (`.docx`) و PDF مستخرجة آلياً من قوالب جاهزة، مع احتساب الإجماليات بدقة، وحفظ السجلات في قاعدة بيانات SQL Server، وإطلاق إشعارات فورية في الخلفية (إرسال بريد إلكتروني مرفق به ملف العرض وربط Webhook مع WhatsApp/n8n) دون تأخير استجابة واجهة المستخدم.

---

## 🚀 Key Features | أبرز المميزات

1. **Friction-Free & Decoupled Data Entry (إدخال بيانات مرن وسريع):**
   - No rigid foreign key restrictions: Sales reps can freely type new client names and custom product names on the fly.
   - Built-in dynamic suggestions via HTML5 `<datalist>` for fast selection.
   - Contact fields (Phone & Email) are completely optional.

2. **Dynamic OpenXML Template Engine (توليد المستندات عبر OpenXML):**
   - Populates Word templates (`DocumentFormat.OpenXml`) while handling run-splitting gracefully across Arabic text.
   - Dynamically clones and populates product table rows based on user input.
   - High-fidelity PDF generation with zero visual distortion.

3. **Non-Blocking Background Tasks (معالجة المهام في الخلفية):**
   - Utilizes `IBackgroundTaskQueue` and `IHostedService` to process notifications asynchronously.
   - **MailKit Emailing:** Sends professional branded emails with attached PDFs.
   - **WhatsApp Automation Webhook:** Posts JSON payloads directly to n8n, Make, or custom WhatsApp gateways.

4. **Modern Decoupled Frontend (واجهة مبيعات ولوحة إدارة سريعة):**
   - **Sales Interface (`index.html`):** Mobile-first, responsive, and intuitive interface with automatic total calculation.
   - **Admin Dashboard (`admin.html`):** Real-time monitoring of generated quotes, item breakdowns, audit logs, and download links.

5. **Automated CI/CD Pipeline (نشر آلي متكامل):**
   - GitHub Actions workflow (`deploy-backend.yml`) automatically builds, tests, and deploys the backend to MonsterASP.NET via FTP on every push to `main`.
   - Frontend independently hosted on Vercel Edge Network for lightning-fast global delivery.

---

## 🏛️ Project Architecture | هيكل المشروع

```text
factory-quote-generator/
│
├── .github/
│   └── workflows/
│       └── deploy-backend.yml      # CI/CD: Automated build & FTP deploy to MonsterASP.NET
│
├── backend/                        # ASP.NET Core 10.0 Web API
│   ├── Controllers/                # Quotes, Clients, Products, Admin Controllers
│   ├── Data/                       # EF Core ApplicationDbContext
│   ├── DTOs/                       # Strongly typed request/response models
│   ├── Models/                     # User, Quote, QuoteItem, Product, Client, AuditLog
│   ├── Services/                   # OpenXML Generator, PDF Converter, Background Queue
│   ├── Templates/                  # Word Quotation Templates (.docx)
│   ├── wwwroot/                    # Embedded static assets & generated quote storage
│   ├── appsettings.json            # Sanitized configuration template
│   ├── web.config                  # IIS in-process hosting configuration
│   └── FactoryQuoteApi.csproj      # .NET Project Configuration
│
├── database/                       # Database Setup & Migrations
│   └── database_setup.sql          # Complete Drop & Create SQL schema + seed data
│
├── frontend/                       # Decoupled Static UI (Vercel-ready)
│   ├── index.html                  # Mobile-first Sales Representative Interface
│   ├── admin.html                  # Administrative Audit & Analytics Dashboard
│   ├── config.js                   # Dynamic environment & API Base URL resolver
│   └── vercel.json                 # Vercel deployment routes and CORS headers
│
├── .gitignore                      # Git ignore rules for .NET, Web, and Python
├── LICENSE                         # MIT License
└── README.md                       # Comprehensive bilingual project documentation
```

---

## 🛠️ Tech Stack | التقنيات المستخدمة

| Layer | Technologies |
| :--- | :--- |
| **Backend API** | ASP.NET Core 10.0 (C#), Entity Framework Core |
| **Document Processing** | `DocumentFormat.OpenXml` (3.2.0), Custom PDF Engine |
| **Asynchronous Notifications** | `MailKit` & `MimeKit` (SMTP), Background Queue, `HttpClient` Webhooks |
| **Database** | Microsoft SQL Server (MSSQL 2019 / 2022) |
| **Frontend** | Vanilla JavaScript, HTML5, TailwindCSS (CDN), Fetch API |
| **Hosting** | **Backend:** MonsterASP.NET (IIS Windows) \| **Frontend:** Vercel |
| **CI / CD** | GitHub Actions (`SamKirkland/FTP-Deploy-Action@v4.3.5`) |

---

## 📖 Step-by-Step Deployment Guide | دليل النشر خطوة بخطوة

Follow this guide to deploy your own instance of the Factory Quote Generation System for free.

### Step 1: Fork or Clone the Repository
```bash
git clone https://github.com/YOUR_USERNAME/factory-quote-generator.git
cd factory-quote-generator
```

---

### Step 2: Setup Database on MonsterASP.NET (or any MSSQL Server)
1. Sign up or log into [MonsterASP.NET](https://www.monsterasp.net).
2. Go to **Databases** ➔ **MS SQL** ➔ Create a new database.
3. Open the **Web-based Query Analyzer** (or connect via SSMS).
4. Copy the entire contents of [`database/database_setup.sql`](database/database_setup.sql), paste it into the query window, and click **Execute**.
5. Copy your database connection string for Step 3.

---

### Step 3: Configure Backend CI/CD via GitHub Actions
To automate backend deployments on every `git push`:
1. In your GitHub repository, navigate to **Settings** ➔ **Secrets and variables** ➔ **Actions**.
2. Click **New repository secret** and add the following 3 secrets:

| Secret Name | Description | Example |
| :--- | :--- | :--- |
| `FTP_SERVER` | MonsterASP FTP Host | `ftp.monsterasp.net` or `your-site.monsterasp.net` |
| `FTP_USERNAME` | MonsterASP FTP Username | `site_user` |
| `FTP_PASSWORD` | MonsterASP FTP Password | `YourSecurePassword123` |

3. In `backend/appsettings.Production.json`, update the connection string with your credentials:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=YOUR_HOST;Database=YOUR_DB;User Id=YOUR_USER;Password=YOUR_PASS;TrustServerCertificate=True;MultipleActiveResultSets=True;"
  }
}
```
4. Push your changes to the `main` branch. GitHub Actions will automatically restore, build, and deploy the backend to `/site/wwwroot/`.

---

### Step 4: Deploy Frontend to Vercel
1. Install the Vercel CLI (or connect your GitHub repo on [vercel.com](https://vercel.com)):
```bash
npm install -g vercel
```
2. In [`frontend/config.js`](frontend/config.js), update `PROD_API_URL` to point to your live MonsterASP domain:
```javascript
const PROD_API_URL = "https://your-site.monsterasp.net";
```
3. Deploy directly from the `frontend/` directory:
```bash
cd frontend
vercel --prod
```
4. Your frontend is now live globally with zero configuration!

---

## 🧪 Local Development & Testing | التشغيل والتطوير المحلي

### Prerequisites
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- Local SQL Server or LocalDB
- Modern Web Browser

### Running Backend Locally:
```bash
cd backend
dotnet run --launch-profile http
```
The API will start listening at: `http://127.0.0.1:5000`

### Running Frontend Locally:
Open `frontend/index.html` directly in any modern browser, or serve it with any static server:
```bash
cd frontend
npx serve .
```
`config.js` will automatically detect `localhost` and communicate with `http://127.0.0.1:5000`.

---

## 🔒 Security Best Practices (DevSecOps)

- **Sanitized Secrets:** No credentials, connection strings, or production passwords are committed to source control.
- **Strict CORS Protection:** ASP.NET Core backend restricts origin headers exclusively to verified Vercel production domains.
- **Fail-Safe Background Tasks:** If a client email or phone number is missing, the background dispatcher logs the omission and safely bypasses the service without crashing worker threads.
- **Encrypted Communication:** Full HTTPS enforcement across Vercel CDN and MonsterASP.NET endpoints.

---

## 🤝 Contributing | المساهمة في المشروع

Contributions are welcome! Please feel free to submit a Pull Request.
1. Fork the Project
2. Create your Feature Branch (`git checkout -b feature/AmazingFeature`)
3. Commit your Changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the Branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

---

## 📄 License | الترخيص

Distributed under the **MIT License**. See [`LICENSE`](LICENSE) for more information.

---

<div align="center">
  <sub>Developed with ❤️ for the open-source engineering community.</sub>
</div>
