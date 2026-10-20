# 📋 سجل التغييرات والإصدارات | Changelog

All notable changes to the **Factory Quotation Generation System** will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [1.2.0] - 2026-10-06

### 🚀 Added (إضافات وميزات جديدة)
- **Direct Word Template Mutation Engine (تعديل مباشر على القالب الأصلي):**
  - Refactored `WordQuoteGeneratorService` to use the authentic master Word document (`backend/Templates/عرض سعر شركة اتريم.docx`) as the single source of truth without recreation or visual redesign.
  - Document mutation operates exclusively in-place via OpenXML, strictly preserving all original document fonts, line spacing, margins, watermarks, headers, footers, and RTL properties.
  - SmartArt diagram manipulation (`word/diagrams/data1.xml` and `word/diagrams/drawing1.xml`) dynamically injecting Arabic day name, standardized date (`dd-MM-yyyy`), and outgoing quote number into graphic header shapes.
  - Precise in-place updating of customer information (Company Name, Contact Title, Contact Person, Project Name, Location) and quote issuer data (Job Title, Prefix, Name).
  - Product table row cloning inheriting cell styles, alignments, and borders without emitting unnecessary summary total rows into the client document.

- **Dynamic Terms & Conditions Management (إدارة ديناميكية حية للشروط والأحكام):**
  - Endpoint `GET /api/quotes/terms` dynamically extracts all 9 default factory terms directly from the master Word document.
  - Fully interactive terms manager on the frontend allowing sales representatives to edit, remove, or append terms.
  - Added terms dynamically clone prototype OpenXML paragraphs inheriting `pStyle="ListParagraph"` and numbering definition `numId="2"`.

- **Interactive Document Preview (معاينة فورية تفاعلية للمستند):**
  - Instant live quotation preview modal on the frontend displaying customer details, issuer credentials, formatted items table, and active terms before document generation.
  - Comprehensive audit trail endpoint (`POST /api/admin/audit`) recording preview, generation, and download telemetry.

- **Database Architecture Extension (ترحيل وتوسيع قاعدة البيانات):**
  - Applied schema migration on MonsterASP.NET SQL Server adding `ContactTitle`, `IssuerName`, `IssuerJobTitle`, `IssuerPrefix`, and `TermsJson` to the `Quotes` table for historical auditability.

---

## [1.1.0] - 2026-10-05

### 🚀 Added (إضافات وميزات جديدة)
- **Dynamic Data Grid (جدول بيانات تفاعلي مصغر):**
  - Editable table column headers with native `contenteditable` inline editing.
  - Drag-and-drop column reordering powered by **SortableJS**.
  - Dynamic buttons to append custom columns or remove unnecessary columns with automated cell synchronization across all rows.
  - Dynamic OpenXML table generation in ASP.NET Core that translates arbitrary frontend column configurations into high-fidelity Word table elements.

- **SweetAlert2 UI Integration (ترقية نوافذ الحوار والتنبيهات):**
  - Replaced all blocking native browser dialogs (`window.alert`, `window.confirm`) with modern, animated **SweetAlert2** modals.
  - Added Promise-based confirmation modals before deleting columns or rows with customized destructive action styling.
  - Branded success notifications with direct quote reference number and auto-dismiss timers for administrative actions.

- **Dynamic Arabic File Naming (تسمية ملفات ذكية للأرشفة):**
  - Generated output files automatically adopt the descriptive format: `{ClientName} {ProjectName}.docx` and `.pdf` (e.g., `شركة اتريم للمقاولات العامة مشروع النور.docx`).
  - Added strict sanitization helper with Regular Expressions to strip illegal Windows file system characters (`\ / : * ? " < > |`) preventing file system exceptions.
  - Safe URL encoding via `Uri.EscapeDataString` for seamless browser downloads and WhatsApp webhook delivery without URL fragmentation.

- **Authentic Word Design & True PDF Conversion (تحويل مستندات عالي الدقة):**
  - Restored and protected the authentic master template (`عرض سعر شركة اتريم.docx`, 1.5MB) containing high-resolution factory letterheads, company logos, and official styling.
  - Integrated **FreeSpire.Doc** for server-side DOCX-to-PDF conversion without requiring MS Office on the hosting server.
  - Removed dummy PDF text generator; output PDFs are true visual replicas of the generated Word document.

- **Decoupled Architecture & CI/CD Pipelines (نشر سحابي آلي متكامل):**
  - Configured GitHub Actions workflow (`.github/workflows/deploy-backend.yml`) for automated .NET 10 build, publish, and FTP deployment to **MonsterASP.NET** with `app_offline.htm` unlock cycles.
  - Automatic deployment integration for the decoupled frontend to the **Vercel Edge Network**.
  - Sanitized configuration repository files ensuring zero secrets, passwords, or connection strings are checked into version control.

### 🔄 Changed (تعديلات وتحسينات منطق العمل)
- **Internal Total Amount Handling (حفظ الإجمالي داخلياً فقط):**
  - Business logic updated so `TotalAmount` is calculated securely on the server side (`quoteItems.Sum(LineTotal)`) and preserved in the SQL Server database for the Admin Dashboard and Revenue KPI.
  - Completely removed `{{TotalAmount}}` placeholder substitution and any summary "Total" footer row from generated client-facing Word and PDF documents.
- **Frontend Form UX & Clean Placeholders (تحسين إدخال البيانات):**
  - Removed all hardcoded `value="..."` attributes from client, project, contact, and item inputs so forms load completely blank.
  - Added descriptive Arabic `placeholder` attributes across all form fields to guide sales representatives friction-free.

---

## [1.0.0] - 2026-10-04

### Initial Open-Source Release
- ASP.NET Core 10.0 Web API with Entity Framework Core and SQL Server support.
- OpenXML word generation engine and PDF conversion dispatcher.
- Asynchronous background task queue for email notifications (MailKit) and WhatsApp webhooks.
- Decoupled TailwindCSS frontend for sales representatives and administrators.
- Comprehensive database drop & create schema script with initial seeds.
