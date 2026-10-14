const crypto = require('crypto');
const path = require('path');
const fs = require('fs');

class SecurityManager {
    constructor() {
        this.activePairingCode = null;
        this.pairingCodeExpiresAt = 0;
        this.pendingPairRequests = new Map();
    }

    generatePairingCode() {
        // Generate secure 6-digit numeric code
        const code = crypto.randomInt(100000, 999999).toString();
        this.activePairingCode = code;
        this.pairingCodeExpiresAt = Date.now() + 180 * 1000; // 180 seconds (3 minutes) TTL
        return {
            code,
            expiresAt: this.pairingCodeExpiresAt,
            ttlSeconds: 180
        };
    }

    verifyPairingCode(code) {
        if (!this.activePairingCode || Date.now() > this.pairingCodeExpiresAt) {
            return false;
        }
        return this.activePairingCode === code.toString().trim();
    }

    generateSessionToken() {
        return crypto.randomBytes(32).toString('hex');
    }

    calculateSha256(data) {
        return crypto.createHash('sha256').update(data).digest('hex');
    }

    calculateFileHash(filePath) {
        return new Promise((resolve, reject) => {
            const hash = crypto.createHash('sha256');
            const stream = fs.createReadStream(filePath);
            stream.on('data', chunk => hash.update(chunk));
            stream.on('end', () => resolve(hash.digest('hex')));
            stream.on('error', err => reject(err));
        });
    }

    /**
     * Sanitizes untrusted filenames to prevent directory traversal and Windows reserved names.
     */
    sanitizeFilename(name) {
        if (!name || typeof name !== 'string') return 'unnamed_file';

        // Strip path traversal characters (/, \, .., control chars)
        let safe = path.basename(name).replace(/[<>:"/\\|?*\x00-\x1F]/g, '_');
        safe = safe.trim();

        // Windows reserved filenames (CON, PRN, AUX, NUL, COM1..9, LPT1..9)
        const reserved = /^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\..*)?$/i;
        if (reserved.test(safe)) {
            safe = '_' + safe;
        }

        if (safe.length === 0 || safe === '.' || safe === '..') {
            safe = 'butterflow_file_' + Date.now();
        }

        return safe;
    }

    /**
     * Resolves duplicate file conflicts in target directory.
     * Strategies: 'rename', 'replace', 'skip'
     */
    resolveConflict(targetDir, originalFilename, strategy = 'rename') {
        const safeName = this.sanitizeFilename(originalFilename);
        const targetPath = path.join(targetDir, safeName);

        if (!fs.existsSync(targetPath)) {
            return { action: 'write', path: targetPath, filename: safeName };
        }

        if (strategy === 'replace') {
            return { action: 'write', path: targetPath, filename: safeName };
        }

        if (strategy === 'skip') {
            return { action: 'skip', path: targetPath, filename: safeName };
        }

        // 'rename' strategy: append (1), (2), etc.
        const ext = path.extname(safeName);
        const base = path.basename(safeName, ext);
        let counter = 1;
        let newName = `${base} (${counter})${ext}`;
        let newPath = path.join(targetDir, newName);

        while (fs.existsSync(newPath)) {
            counter++;
            newName = `${base} (${counter})${ext}`;
            newPath = path.join(targetDir, newName);
        }

        return { action: 'write', path: newPath, filename: newName };
    }
}

module.exports = new SecurityManager();
