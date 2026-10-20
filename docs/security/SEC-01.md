# SEC-01 — Quotation read API

Scope: finding F02 in `SECURITY_AUDIT.md`. Only `GET /api/quotes/{id}` application behavior changes. No database migration, generation/notification change, frontend change or public-file-access change is included.

## Authorization and response

The action validates the existing server session before querying the quotation. Missing, malformed, invalid, expired or revoked employee tokens return 401. Valid Owner/Admin session roles read all quotations. Employee tokens receive only quotations whose non-null `CreatedByUserId` and `UserId` both match the authenticated employee; query/header `UserId` or role hints have no effect. Other accepted legacy/unmapped roles have no read grant. Missing and inaccessible quotations both return the same 404 ProblemDetails contract (a per-request trace ID may differ).

Historical attribution needs an additional conservative check: existing shared role sessions impersonate an Admin Users row, or any user when no Admin exists. An employee token for an Admin surrogate cannot claim those records as personal ownership. Without an Admin surrogate, personal attribution is ambiguous and employee reads fail closed. Owner/Admin still read those records. Null/conflicting IDs are never backfilled by a read. No history is rewritten. This does not replace the later provenance/session/RBAC migration described in the audit; manually changed historical role assignments need separate review.

The EF query projects directly to `QuoteReadResponseDto` and `QuoteReadItemDto`. It never loads/projects the User entity. Responses contain business quotation/item fields only: no User navigation, creator/account IDs, password material, session internals, delivery/recipient secrets or audit graph. The endpoint sets no-store/no-cache response policy. Business fields and item IDs remain available; public PDF/DOCX URLs remain business fields because file protection is a separate task.

## Local and CI validation

```powershell
dotnet test security-tests/FactoryQuoteApi.SecurityTests.csproj -c Release
```

24 HTTP tests passed locally using real MVC routing/serialization and SecurityService, isolated in-memory SQLite, fabricated sessions/users/quotations, a synthetic DOCX and a socket-free Telegram transport. The production startup, SQL Server connection and real external delivery are not executed. The tests cover anonymous/invalid/expired/tampered/revoked sessions, both employee isolation directions, own reads, Admin/Owner reads, safe JSON keys recursively, header/query spoofing, null/conflicting/shared legacy attribution, no-admin fail-closed behavior, no read side effects, and unchanged generation/creator/automatic PDF dispatch/independent recipients/Sent retry suppression.

The deployment workflow now runs this explicit test project before publish/deploy, rather than silently skipping absent tests. Only test dependencies are added; the application package graph is unchanged. Existing dependency advisories from F18 remain outside this patch. The test-only SQLite bundle is pinned to 3.0.5 to avoid introducing the legacy native SQLite package advisory.

## Production verification

After successful CI/CD, use two Owner-issued synthetic employees and unique synthetic quotation names. Keep real passwords and tokens out of chat, logs, reports and command-line arguments. No default credential guessing, real-record enumeration, SQL edits, recipient changes or webhook diagnostics are required.

An interactive verifier is supplied:

```powershell
pwsh -NoProfile -File ./security-tests/Verify-Production.ps1
```

It prompts for existing authorized Owner/Admin passwords using hidden secure input, keeps tokens only in memory, creates two clearly labelled test employees and quotations, checks all role boundaries and structural sensitive-field exclusion, checks authenticated creator/delivery history and Sent retry suppression, and confirms recipient IDs/names/activation were not changed. Normal automatic Telegram delivery remains enabled: the two synthetic PDFs will reach existing active recipients. It deactivates its test employees in `finally` and preserves quotation/delivery history. It saves only redacted checks and synthetic fixture identifiers in ignored `scratch/security-audit/sec01-production.json`.

The Owner browser sign-in confirms UI access but cannot by itself authorize arbitrary API requests through the browser tool's isolated read-only evaluation environment. The verifier is available when no secure credentialed API test session is accessible to the agent. A production result must not be marked PASS until the authenticated checks actually run; local success and liveness alone are insufficient.

Audit validation sources in `docs/security/validation` are historical baseline probes against revision `22b4d56`; their original F02 disclosure assertions are expected to fail after SEC-01. Use the dedicated security test project above for the fixed endpoint.
