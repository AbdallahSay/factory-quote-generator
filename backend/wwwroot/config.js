/**
 * Production & Development Configuration for Factory Quote Generation System Frontend
 */
const CONFIG = {
    // Priority:
    // 1. window.ENV_API_URL (if injected dynamically)
    // 2. Localhost development API (http://127.0.0.1:5000)
    // 3. MonsterASP.NET Production API URL
    API_BASE_URL: window.ENV_API_URL || (
        window.location.hostname === 'localhost' || window.location.hostname === '127.0.0.1'
            ? 'http://127.0.0.1:5000'
            : 'https://factory-quote.runasp.net'
    )
};
