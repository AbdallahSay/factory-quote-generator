// Automated XSS Regression Tests for Factory Quote Generator
// Verifies that quotation, audit, recipient, and external Telegram data
// are rendered safely via text/DOM APIs and cannot inject executable HTML or javascript: URLs.

const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');

class MockNode {
    constructor(nodeType = 1, nodeName = 'DIV') {
        this.nodeType = nodeType;
        this.nodeName = nodeName.toUpperCase();
        this.tagName = this.nodeName;
        this.childNodes = [];
        this.attributes = {};
        this._textContent = '';
        this._innerHTML = '';
        this.className = '';
        this.style = {};
        this.classList = {
            add: (c) => {},
            remove: (c) => {},
            contains: (c) => false
        };
        this.dataset = {};
        this.eventListeners = {};
        this.value = '';
        this.placeholder = '';
        this.type = 'text';
        this.title = '';
        this.href = '';
        this.target = '';
        this.rel = '';
        this.colSpan = 1;
        this.contentEditable = 'inherit';
        this.spellcheck = true;
    }

    appendChild(child) {
        if (child) {
            this.childNodes.push(child);
        }
        return child;
    }

    insertBefore(newChild, refChild) {
        const idx = this.childNodes.indexOf(refChild);
        if (idx >= 0) {
            this.childNodes.splice(idx, 0, newChild);
        } else {
            this.childNodes.push(newChild);
        }
        return newChild;
    }

    removeChild(child) {
        const idx = this.childNodes.indexOf(child);
        if (idx >= 0) this.childNodes.splice(idx, 1);
        return child;
    }

    addEventListener(event, handler) {
        this.eventListeners[event] = this.eventListeners[event] || [];
        this.eventListeners[event].push(handler);
    }

    setAttribute(key, val) {
        this.attributes[key] = String(val);
    }

    getAttribute(key) {
        return this.attributes[key] || null;
    }

    querySelector(sel) {
        // Simple mock querySelector
        for (const child of this.childNodes) {
            if (child.nodeType === 1) {
                if (sel === 'div' && child.nodeName === 'DIV') return child;
                if (sel === 'span' && child.nodeName === 'SPAN') return child;
                if (sel.startsWith('.') && child.className.includes(sel.slice(1))) return child;
                const found = child.querySelector(sel);
                if (found) return found;
            }
        }
        return null;
    }

    querySelectorAll(sel) {
        const results = [];
        for (const child of this.childNodes) {
            if (child.nodeType === 1) {
                if (sel.startsWith('.') && child.className.includes(sel.slice(1))) results.push(child);
                results.push(...child.querySelectorAll(sel));
            }
        }
        return results;
    }

    set textContent(text) {
        this._textContent = String(text);
        const tn = new MockNode(3, '#text');
        tn._textContent = String(text);
        this.childNodes = [tn];
        this._innerHTML = '';
    }

    get textContent() {
        if (this.nodeType === 3) return this._textContent;
        if (this.childNodes.length > 0) {
            return this.childNodes.map(c => c.textContent).join('');
        }
        return this._textContent;
    }

    set innerHTML(html) {
        this._innerHTML = String(html);
        if (html === '') {
            this.childNodes = [];
        }
    }

    get innerHTML() {
        if (this._innerHTML && this.childNodes.length === 0) return this._innerHTML;
        return (this._innerHTML || '') + this.serializeChildren();
    }

    serializeChildren() {
        function escapeAttr(val) {
            return String(val)
                .replace(/&/g, '&amp;')
                .replace(/"/g, '&quot;')
                .replace(/</g, '&lt;')
                .replace(/>/g, '&gt;');
        }

        return this.childNodes.map(c => {
            if (c.nodeType === 3) {
                // Text node: escape for HTML
                return c._textContent.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
            }
            let attrs = '';
            if (c.className) attrs += ` class="${escapeAttr(c.className)}"`;
            if (c.href) attrs += ` href="${escapeAttr(c.href)}"`;
            if (c.title) attrs += ` title="${escapeAttr(c.title)}"`;
            for (const [k, v] of Object.entries(c.attributes)) {
                attrs += ` ${k}="${escapeAttr(v)}"`;
            }
            const inner = (c._innerHTML || '') + c.serializeChildren();
            return `<${c.nodeName.toLowerCase()}${attrs}>${inner}</${c.nodeName.toLowerCase()}>`;
        }).join('');
    }
}

function createDOMContext(pageContent, fetchResponseData = null) {
    const elementsById = {};
    const root = new MockNode(1, 'BODY');

    function getElementById(id) {
        if (!elementsById[id]) {
            elementsById[id] = new MockNode(1, 'DIV');
        }
        return elementsById[id];
    }

    const documentMock = {
        getElementById,
        createElement: (tag) => new MockNode(1, tag),
        createTextNode: (text) => {
            const n = new MockNode(3, '#text');
            n._textContent = String(text);
            return n;
        },
        querySelectorAll: (sel) => [],
        addEventListener: () => {}
    };

    const windowMock = {
        location: { origin: 'https://factory-quote-frontend.vercel.app' },
        addEventListener: () => {}
    };

    const context = {
        window: windowMock,
        document: documentMock,
        CONFIG: { API_BASE_URL: 'https://factory-quote.runasp.net' },
        Auth: {
            getAuthHeader: () => ({ Authorization: 'Bearer test' }),
            verify: async () => true,
            login: async () => ({})
        },
        Swal: {
            fire: async (opts) => ({ isConfirmed: false, value: null }),
            showLoading: () => {},
            showValidationMessage: () => {}
        },
        console: { log() {}, warn() {}, error() {} },
        Date,
        parseFloat,
        encodeURIComponent,
        URL,
        fetch: async () => ({
            ok: true,
            status: 200,
            json: async () => fetchResponseData
        })
    };

    vm.createContext(context);
    return { context, elementsById };
}

function extractScript(html) {
    const scriptStart = html.lastIndexOf('<script>');
    const scriptEnd = html.lastIndexOf('</script>');
    assert(scriptStart >= 0 && scriptEnd > scriptStart);
    return html.substring(scriptStart + 8, scriptEnd);
}

function assertNoXSS(node, renderedHtml, file, contextName) {
    // 1. Check raw serialized HTML: no unescaped <script, <img, or javascript: URLs
    assert(!renderedHtml.includes('<script'), `${file}: ${contextName} rendered unescaped <script>`);
    assert(!renderedHtml.includes('<img'), `${file}: ${contextName} rendered unescaped <img>`);
    assert(!renderedHtml.includes('href="javascript:'), `${file}: ${contextName} allowed javascript: URL`);

    // 2. Walk DOM nodes to ensure no SCRIPT or IMG elements were created,
    // and no inline event handlers (onerror, onclick, etc.) were attached as attributes
    function walk(n) {
        if (!n) return;
        if (n.nodeType === 1) {
            const name = (n.nodeName || '').toUpperCase();
            assert(name !== 'SCRIPT', `${file}: ${contextName} contains SCRIPT element`);
            assert(name !== 'IMG', `${file}: ${contextName} contains IMG element`);

            for (const attr of Object.keys(n.attributes || {})) {
                assert(!attr.toLowerCase().startsWith('on'), `${file}: ${contextName} has inline ${attr} attribute on <${name}>`);
            }
            if (n.attributes && n.attributes.href) {
                assert(!n.attributes.href.toLowerCase().startsWith('javascript:'), `${file}: ${contextName} has javascript: href attribute`);
            }
            if (n.href) {
                assert(!String(n.href).toLowerCase().startsWith('javascript:'), `${file}: ${contextName} has javascript: href property`);
            }
        }
        for (const child of (n.childNodes || [])) {
            walk(child);
        }
    }
    walk(node);
}

// ------------------- TESTS -------------------
async function runTests() {
    console.log('--- Starting XSS & DOM Injection Regression Suite ---');
    const maliciousPayload = '<img src=x onerror="alert(\'XSS\')"><script>alert(1)</script>';

    for (const file of ['frontend/admin.html', 'backend/wwwroot/admin.html']) {
        const html = fs.readFileSync(file, 'utf8');
        const script = extractScript(html);

        // Test 1: loadQuotes with malicious fields
        {
            const maliciousQuote = {
                id: 99,
                quoteNumber: maliciousPayload,
                clientName: maliciousPayload,
                contactPerson: maliciousPayload,
                contactTitle: maliciousPayload,
                projectName: maliciousPayload,
                location: maliciousPayload,
                issuerName: maliciousPayload,
                issuerJobTitle: maliciousPayload,
                createdByFullName: maliciousPayload,
                itemCount: 2,
                totalAmount: 1250,
                createdAt: '2026-10-20T12:00:00Z',
                pdfUrl: 'javascript:alert(1)',
                docxUrl: 'javascript:alert(2)',
                telegramDeliveries: [
                    { displayName: maliciousPayload, chatId: '999', status: 'Sent' },
                    { displayName: maliciousPayload, chatId: '998', status: 'Failed', errorMessage: maliciousPayload }
                ]
            };

            const { context, elementsById } = createDOMContext(html, [maliciousQuote]);
            vm.runInContext(script, context);
            await vm.runInContext('loadQuotes()', context);

            const tbody = elementsById['quotesTableBody'];
            assert(tbody, 'quotesTableBody must exist');
            const renderedHtml = tbody.innerHTML;

            assertNoXSS(tbody, renderedHtml, file, 'loadQuotes');
            console.log(`PASS: ${file} loadQuotes safely renders malicious quotations`);
        }

        // Test 2: loadLogs with malicious audit fields
        {
            const maliciousLog = {
                id: 1,
                timestamp: '2026-10-20T12:00:00Z',
                action: maliciousPayload,
                entityId: maliciousPayload,
                user: { fullName: maliciousPayload, role: maliciousPayload }
            };

            const { context, elementsById } = createDOMContext(html, [maliciousLog]);
            vm.runInContext(script, context);
            await vm.runInContext('loadLogs()', context);

            const tbody = elementsById['logsTableBody'];
            assert(tbody, 'logsTableBody must exist');
            const renderedHtml = tbody.innerHTML;

            assertNoXSS(tbody, renderedHtml, file, 'loadLogs');
            console.log(`PASS: ${file} loadLogs safely renders malicious audit logs`);
        }

        // Test 3: loadTelegramRecipients with malicious recipient text
        if (html.includes('loadTelegramRecipients')) {
            const maliciousRecipient = {
                id: 1,
                displayName: maliciousPayload,
                chatId: '123456',
                isActive: true,
                totalSent: 1,
                totalFailed: 0,
                createdAt: '2026-10-20T12:00:00Z'
            };

            const { context, elementsById } = createDOMContext(html, [maliciousRecipient]);
            vm.runInContext(script, context);
            await vm.runInContext('loadTelegramRecipients()', context);

            const tbody = elementsById['telegramRecipientsTableBody'];
            assert(tbody, 'telegramRecipientsTableBody must exist');
            const renderedHtml = tbody.innerHTML;

            assertNoXSS(tbody, renderedHtml, file, 'loadTelegramRecipients');
            console.log(`PASS: ${file} loadTelegramRecipients safely renders malicious recipient data`);
        }

        // Test 4: loadTelegramBotInfo with malicious external Telegram username/firstname
        if (html.includes('loadTelegramBotInfo')) {
            const botInfo = {
                botUsername: 'TestBot',
                isConfigured: true,
                recentChats: [
                    { chatId: '123', firstName: maliciousPayload, username: maliciousPayload }
                ]
            };

            const { context, elementsById } = createDOMContext(html, botInfo);
            vm.runInContext(script, context);
            await vm.runInContext('loadTelegramBotInfo()', context);

            const detectedList = elementsById['detectedChatsList'];
            assert(detectedList, 'detectedChatsList must exist');
            const renderedHtml = detectedList.innerHTML;

            assertNoXSS(detectedList, renderedHtml, file, 'loadTelegramBotInfo');
            console.log(`PASS: ${file} loadTelegramBotInfo safely renders external Telegram chat names`);
        }
    }

    // Test 5: owner.html loadEmployees with malicious employee names
    for (const file of ['frontend/owner.html', 'backend/wwwroot/owner.html']) {
        const html = fs.readFileSync(file, 'utf8');
        const script = extractScript(html);

        const maliciousEmployee = {
            id: 101,
            fullName: maliciousPayload,
            email: 'hacker@example.com',
            quotesCount: 5,
            isActive: true,
            createdAt: '2026-10-20T12:00:00Z'
        };

        const { context, elementsById } = createDOMContext(html, [maliciousEmployee]);
        vm.runInContext(script, context);
        await vm.runInContext('loadEmployees()', context);

        const tbody = elementsById['employeesTableBody'];
        assert(tbody, 'employeesTableBody must exist in owner.html');
        const renderedHtml = tbody.innerHTML;

        assertNoXSS(tbody, renderedHtml, file, 'loadEmployees');
        console.log(`PASS: ${file} loadEmployees safely renders malicious employee data`);
    }

    console.log('--- ALL FRONTEND XSS REGRESSION CHECKS PASSED SUCCESSFULLY ---');
}

runTests().catch(err => {
    console.error('XSS Test Failure:', err);
    process.exit(1);
});
