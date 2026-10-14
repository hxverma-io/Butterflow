const assert = require('assert');
const fs = require('fs');
const path = require('path');
const os = require('os');
const security = require('../../src/main/network/security');

console.log('[Test] Running Security Unit Tests...');

// 1. Pairing Code Generation & Verification
const pair = security.generatePairingCode();
assert.strictEqual(pair.code.length, 6);
assert(/^\d{6}$/.test(pair.code), 'Pairing code must be 6 digits');
assert.strictEqual(security.verifyPairingCode(pair.code), true);
assert.strictEqual(security.verifyPairingCode('000000'), false);

// 2. Path Traversal & Filename Sanitization
const malicious1 = '../../../../Windows/System32/calc.exe';
const sanitized1 = security.sanitizeFilename(malicious1);
assert(!sanitized1.includes('..'), 'Path traversal dots must be eliminated');
assert(!sanitized1.includes('/'), 'Slashes must be eliminated');
assert(!sanitized1.includes('\\'), 'Backslashes must be eliminated');
assert.strictEqual(sanitized1, 'calc.exe');

const reservedWin = 'CON.txt';
const sanitizedWin = security.sanitizeFilename(reservedWin);
assert(!sanitizedWin.toLowerCase().startsWith('con.'), 'Windows reserved device names must be prefixed/escaped');

const emptyName = '   ';
const sanitizedEmpty = security.sanitizeFilename(emptyName);
assert(sanitizedEmpty.startsWith('butterflow_file_'), 'Empty names must have safe fallback');

// 3. Duplicate Conflict Resolution
const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'bf_sec_test_'));
const testFile = path.join(tmpDir, 'document.pdf');
fs.writeFileSync(testFile, 'test content');

// Strategy: Rename
const resRename = security.resolveConflict(tmpDir, 'document.pdf', 'rename');
assert.strictEqual(resRename.action, 'write');
assert.strictEqual(resRename.filename, 'document (1).pdf');

// Strategy: Replace
const resReplace = security.resolveConflict(tmpDir, 'document.pdf', 'replace');
assert.strictEqual(resReplace.action, 'write');
assert.strictEqual(resReplace.filename, 'document.pdf');

// Strategy: Skip
const resSkip = security.resolveConflict(tmpDir, 'document.pdf', 'skip');
assert.strictEqual(resSkip.action, 'skip');

// Cleanup
fs.rmSync(tmpDir, { recursive: true, force: true });

console.log('✓ Security unit tests PASSED successfully.');
