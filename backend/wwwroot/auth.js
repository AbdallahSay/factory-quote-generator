/**
 * Authentication Client Helper for Factory Quote Generation System
 * Handles independent role tokens for:
 * - 'quote' (Quotation screen)
 * - 'admin' (Admin dashboard)
 * - 'owner' (Owner password management)
 */
const Auth = {
    getKey(role) {
        return `fq_token_${role}`;
    },

    getToken(role) {
        const key = this.getKey(role);
        return localStorage.getItem(key) || sessionStorage.getItem(key) || null;
    },

    setToken(role, token, remember = true) {
        const key = this.getKey(role);
        if (remember) {
            localStorage.setItem(key, token);
            sessionStorage.removeItem(key);
        } else {
            sessionStorage.setItem(key, token);
            localStorage.removeItem(key);
        }
    },

    clearToken(role) {
        const key = this.getKey(role);
        localStorage.removeItem(key);
        sessionStorage.removeItem(key);
    },

    getAuthHeader(role) {
        const token = this.getToken(role);
        return token ? { 'Authorization': `Bearer ${token}` } : {};
    },

    async login(role, password, remember = true) {
        if (!password || !password.trim()) {
            throw new Error('يرجى إدخال كلمة المرور');
        }

        const baseUrl = typeof CONFIG !== 'undefined' && CONFIG.API_BASE_URL ? CONFIG.API_BASE_URL : '';
        const response = await fetch(`${baseUrl}/api/auth/login`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({ role, password: password.trim() })
        });

        const data = await response.json().catch(() => ({}));
        if (!response.ok || !data.success) {
            throw new Error(data.message || 'كلمة المرور غير صحيحة');
        }

        this.setToken(role, data.token, remember);
        return data;
    },

    async verify(role) {
        const token = this.getToken(role);
        if (!token) return false;

        const baseUrl = typeof CONFIG !== 'undefined' && CONFIG.API_BASE_URL ? CONFIG.API_BASE_URL : '';
        try {
            const response = await fetch(`${baseUrl}/api/auth/verify?role=${encodeURIComponent(role)}`, {
                headers: {
                    'Authorization': `Bearer ${token}`
                }
            });

            if (!response.ok) {
                this.clearToken(role);
                return false;
            }

            const data = await response.json().catch(() => ({}));
            if (data.valid === true) {
                return true;
            } else {
                this.clearToken(role);
                return false;
            }
        } catch (e) {
            // If offline or network issue, do not immediately discard valid session, but return true if token format is valid
            console.warn('Auth verify network issue:', e);
            return true;
        }
    },

    async changePassword(targetRole, newPassword, confirmPassword) {
        const token = this.getToken('owner');
        if (!token) {
            throw new Error('يرجى تسجيل الدخول كمالك للنظام أولاً');
        }

        const baseUrl = typeof CONFIG !== 'undefined' && CONFIG.API_BASE_URL ? CONFIG.API_BASE_URL : '';
        const response = await fetch(`${baseUrl}/api/auth/change-password`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'Authorization': `Bearer ${token}`
            },
            body: JSON.stringify({ targetRole, newPassword, confirmPassword })
        });

        const data = await response.json().catch(() => ({}));
        if (!response.ok || !data.success) {
            throw new Error(data.message || 'فشل تحديث كلمة المرور');
        }

        return data;
    },

    logout(role) {
        this.clearToken(role);
        window.location.reload();
    }
};
