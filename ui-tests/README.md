# Admin dashboard browser verification

Run from the repository root:

```sh
npm ci --prefix ui-tests
cd ui-tests
npx playwright install --with-deps chromium firefox webkit
npm test
```

Use `PHASE=before BASELINE_REF=origin/main npm test` to capture the base branch without changing the checkout. On PowerShell set those environment variables with `$env:PHASE='before'` and `$env:BASELINE_REF='origin/main'`. Unset them for the after run. `ENGINES=chromium,firefox,webkit` selects engines; `BASE_URL` tests deployed HTML. All `/api/**` requests, authentication and documents are intercepted with synthetic fixtures, including on deployed origins. No real accounts, documents or Telegram mutations are used.

The suite checks widths 320, 360, 375, 390, 430, 768, 1024, 1280, 1440 and 1920 in all three browsers. Assertions cover page overflow, clipped cell text, overlapping controls, 44px touch targets, KPI columns, popup bounds and visible confirmations. Functional cases cover PDF/Word audit requests, general and single-recipient retries, recipient add/edit/activate/deactivate/delete, owner-authenticated discovery, login, empty/error states, long Arabic/English text, portrait and landscape, and reduced viewport heights for keyboard space.

Native Chromium browser zoom is set to 200% through a test-only unpacked extension. The test asserts a 1280px viewport reflows to `innerWidth=640` and `devicePixelRatio=2`; it then tests both tabs and a dialog. Firefox/WebKit additionally run at the equivalent reduced CSS viewport. The extension is only loaded in an isolated temporary test profile and is never shipped with either admin page. Desktop browser emulation does not replace physical-device keyboard testing.

Artifacts are written to `artifacts/before` and `artifacts/after`. They contain screenshots of both tabs at every required width, normal-data mobile/tablet/desktop views, short dialog views, login, empty/error states, native zoom, and machine-readable results. The PR workflow uploads these as `admin-responsive-browser-evidence` for 30 days. Before screenshots deliberately tolerate existing overflow; after assertions fail on any regression. Both checked-in admin files must be byte-identical.

Existing security regressions:

```sh
node security-tests/xss-regression-tests.cjs
dotnet test security-tests/FactoryQuoteApi.SecurityTests.csproj -c Release
```

Keep production API verification read-only. Browser mocks verify frontend wiring; the .NET synthetic-host suite verifies actual authentication, authorization, signed document access, audit integrity and Telegram outbox behavior. No live Telegram message is required or sent by this UI suite.
