# Run interactively after the SEC-01 deployment. Never pass passwords on the command line.
# Creates exactly two clearly-labelled synthetic quotations, preserving automatic delivery.
# Test accounts are deactivated in finally; quotations/delivery history are retained.
param(
    [string]$ReportPath = (Join-Path $PSScriptRoot '../scratch/security-audit/sec01-production.json')
)

$ErrorActionPreference = 'Stop'
$baseUri = 'https://factory-quote.runasp.net'
$checks = [System.Collections.Generic.List[object]]::new()
$testUsers = [System.Collections.Generic.List[object]]::new()
$quoteIds = [System.Collections.Generic.List[int]]::new()
$http = [System.Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromSeconds(180)
$startedAt = [DateTime]::UtcNow.ToString('o')
$ownerToken = $null
$adminToken = $null

function Request([string]$Method, [string]$Path, [string]$Token = $null, $Body = $null) {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), "$baseUri$Path")
    try {
        if ($Token) { $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Token) }
        if ($null -ne $Body) {
            $request.Content = [System.Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 15 -Compress), [Text.Encoding]::UTF8, 'application/json')
        }
        $response = $http.SendAsync($request).GetAwaiter().GetResult()
        try {
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $json = $null
            if ($text) { try { $json = $text | ConvertFrom-Json } catch { } }
            return [pscustomobject]@{ Status = [int]$response.StatusCode; Json = $json; NoStore = $response.Headers.CacheControl.NoStore }
        } finally { $response.Dispose() }
    } finally { $request.Dispose() }
}

function Check([string]$Name, [bool]$Passed) {
    $checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed })
    Write-Host "$(if ($Passed) { 'PASS' } else { 'FAIL' }) $Name"
    if (-not $Passed) { throw 'A production verification check failed; see the redacted report.' }
}

function Login([string]$Role) {
    $securePassword = Read-Host "Existing authorized $Role password (hidden; sent only to $baseUri)" -AsSecureString
    try {
        $credential = [System.Net.NetworkCredential]::new('', $securePassword)
        $response = Request POST '/api/auth/login' -Body @{ role = $Role; password = $credential.Password }
        Check "$Role authorized login" ($response.Status -eq 200 -and [bool]$response.Json.token)
        return $response.Json.token
    } finally { $securePassword.Dispose(); $credential = $null; $response = $null }
}

function AssertSafeJson($Value) {
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [ValueType]) { return $true }
    if ($Value -is [array]) {
        foreach ($item in $Value) { if (-not (AssertSafeJson $item)) { return $false } }
    } else {
        foreach ($property in $Value.PSObject.Properties) {
            if ($property.Name -match '^(user|passwordHash|password|token|sessionSalt|signingKey|role|auditLogs|telegramDeliveries|quote|botToken|isActive|userId|createdByUserId)$') { return $false }
            if (-not (AssertSafeJson $property.Value)) { return $false }
        }
    }
    return $true
}

try {
    # Use an invalid/nonexistent ID first: do not probe historical quotation records.
    Check 'anonymous read rejected' ((Request GET '/api/quotes/2147483647').Status -eq 401)
    Check 'invalid bearer rejected' ((Request GET '/api/quotes/2147483647' 'synthetic-invalid-token').Status -eq 401)
    $ownerToken = Login 'owner'
    $adminToken = Login 'admin'
    $recipientsBefore = Request GET '/api/admin/telegram/recipients' $ownerToken
    Check 'authorized independent recipient read' ($recipientsBefore.Status -eq 200)
    $runId = [Guid]::NewGuid().ToString('N').Substring(0, 12)

    foreach ($label in @('A', 'B')) {
        $password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
        $email = "sec01-$($label.ToLower())-$runId@example.invalid"
        $fullName = "SEC-01 SYNTHETIC Employee $label $runId"
        $created = Request POST '/api/admin/employees' $ownerToken @{ fullName = $fullName; email = $email; password = $password; confirmPassword = $password }
        Check "Owner creates synthetic employee $label" ($created.Status -eq 201 -and $created.Json.user.id -gt 0)
        $account = [pscustomobject]@{ Id = [int]$created.Json.user.id; FullName = $fullName; Email = $email; Token = $null }
        $testUsers.Add($account)
        $login = Request POST '/api/auth/login' -Body @{ email = $email; password = $password }
        $password = $null
        Check "synthetic employee $label login" ($login.Status -eq 200 -and [bool]$login.Json.token)
        $account.Token = $login.Json.token
    }
    foreach ($index in @(0, 1)) {
        $account = $testUsers[$index]
        $generated = Request POST '/api/quotes/generate' $account.Token @{
            userId = $testUsers[1 - $index].Id
            clientName = "SEC-01 SYNTHETIC TEST $runId $index"
            projectName = 'Security regression verification - not a commercial offer'
            issuerName = $account.FullName
            items = @(@{ productName = 'SYNTHETIC TEST ITEM'; quantity = 1; unitPrice = 0 })
        }
        Check "synthetic quote $index generation" ($generated.Status -eq 200 -and $generated.Json.quoteId -gt 0)
        $quoteIds.Add([int]$generated.Json.quoteId)
    }
    foreach ($index in @(0, 1)) {
        $id = $quoteIds[$index]
        Check "anonymous denied synthetic quote $index" ((Request GET "/api/quotes/$id").Status -eq 401)
        $own = Request GET "/api/quotes/$id" $testUsers[$index].Token
        Check "employee $index reads own synthetic quote" ($own.Status -eq 200 -and $own.Json.id -eq $id)
        Check "employee $index safe JSON" (AssertSafeJson $own.Json)
        Check "employee $index private no-store response" ([bool]$own.NoStore)
        $other = Request GET "/api/quotes/$id" $testUsers[1 - $index].Token
        $missing = Request GET '/api/quotes/2147483647' $testUsers[1 - $index].Token
        Check "other employee $index non-disclosing denial" ($other.Status -eq 404 -and $missing.Status -eq 404 -and $other.Json.title -eq $missing.Json.title)
        foreach ($privileged in @(@{ Role = 'owner'; Token = $ownerToken }, @{ Role = 'admin'; Token = $adminToken })) {
            $read = Request GET "/api/quotes/$id" $privileged.Token
            Check "$($privileged.Role) reads synthetic quote $index safely" ($read.Status -eq 200 -and (AssertSafeJson $read.Json))
        }
    }
    $recent = Request GET '/api/admin/quotes' $adminToken
    Check 'synthetic delivery history available' ($recent.Status -eq 200)
    foreach ($index in @(0, 1)) {
        $quote = @($recent.Json | Where-Object { $_.id -eq $quoteIds[$index] })
        Check "synthetic quote $index authenticated creator preserved" ($quote.Count -eq 1 -and $quote[0].createdByUserId -eq $testUsers[$index].Id -and $quote[0].createdByFullName -eq $testUsers[$index].FullName)
        $deliveries = @($quote[0].telegramDeliveries)
        Check "synthetic quote $index automatically delivered" ($deliveries.Count -gt 0 -and @($deliveries | Where-Object status -ne 'Sent').Count -eq 0)
        # Only an already-Sent quote is retried; this must suppress a second external send.
        $retry = Request POST "/api/admin/quotes/$($quoteIds[$index])/retry-telegram" $adminToken
        Check "synthetic quote $index Sent retry suppressed" ($retry.Status -eq 200 -and @($retry.Json.results).Count -gt 0 -and @($retry.Json.results | Where-Object { -not $_.alreadySent }).Count -eq 0)
    }
    $recipientsAfter = Request GET '/api/admin/telegram/recipients' $ownerToken
    $beforeState = @($recipientsBefore.Json | Sort-Object id | Select-Object id, chatId, displayName, isActive) | ConvertTo-Json -Depth 5 -Compress
    $afterState = @($recipientsAfter.Json | Sort-Object id | Select-Object id, chatId, displayName, isActive) | ConvertTo-Json -Depth 5 -Compress
    Check 'recipient IDs/names/activation unchanged' ($recipientsAfter.Status -eq 200 -and $beforeState -ceq $afterState)
} catch {
    # Never print exception objects or request/response bodies that may contain credentials.
    Write-Host 'Production verification stopped. Inspect the redacted check report; no credentials were saved.'
    if (-not @($checks | Where-Object { -not $_.Passed }).Count) {
        $checks.Add([pscustomobject]@{ Name = 'verification interrupted before completion'; Passed = $false })
    }
} finally {
    foreach ($account in $testUsers) {
        try {
            $disabled = Request POST "/api/admin/employees/$($account.Id)/toggle-status" $ownerToken @{ isActive = $false }
            $checks.Add([pscustomobject]@{ Name = "synthetic employee $($account.Id) deactivated"; Passed = ($disabled.Status -eq 200 -and $disabled.Json.isActive -eq $false) })
        } catch {
            $checks.Add([pscustomobject]@{ Name = "synthetic employee $($account.Id) requires deactivation"; Passed = $false })
        }
        $account.Token = $null
    }
    $report = [pscustomobject]@{
        StartedUtc = $startedAt; CompletedUtc = [DateTime]::UtcNow.ToString('o'); Backend = $baseUri
        Checks = @($checks.ToArray()); SyntheticQuoteIds = @($quoteIds.ToArray())
        SyntheticUsers = @($testUsers | Select-Object Id, FullName, Email)
        AllPassed = ($checks.Count -gt 0 -and @($checks | Where-Object { -not $_.Passed }).Count -eq 0)
    }
    $reportDirectory = Split-Path ([IO.Path]::GetFullPath($ReportPath)) -Parent
    New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding utf8
    $ownerToken = $null; $adminToken = $null; $password = $null; $http.Dispose()
    Write-Host "Redacted report: $([IO.Path]::GetFullPath($ReportPath))"
}
if (-not $report.AllPassed) { exit 1 }
