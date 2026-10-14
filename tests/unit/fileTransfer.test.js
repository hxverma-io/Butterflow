const assert = require('assert');
const fs = require('fs');
const path = require('path');
const os = require('os');
const crypto = require('crypto');
const security = require('../../src/main/network/security');

console.log('[Test] Running File Transfer Chunking & SHA-256 Integrity Unit Tests...');

async function run() {
    const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'bf_ft_test_'));
    const srcFile = path.join(tmpDir, 'sample_large.bin');
    
    // Create a 256KB random binary test file
    const randomData = crypto.randomBytes(256 * 1024);
    fs.writeFileSync(srcFile, randomData);

    const originalHash = await security.calculateFileHash(srcFile);

    // Simulate chunked streaming (64KB chunks)
    const CHUNK_SIZE = 64 * 1024;
    const destTemp = path.join(tmpDir, 'received.bin.butterflow.tmp');
    const destFinal = path.join(tmpDir, 'received.bin');

    const writeStream = fs.createWriteStream(destTemp);
    const hashStream = crypto.createHash('sha256');

    for (let offset = 0; offset < randomData.length; offset += CHUNK_SIZE) {
        const chunk = randomData.slice(offset, offset + CHUNK_SIZE);
        writeStream.write(chunk);
        hashStream.update(chunk);
    }

    await new Promise(resolve => writeStream.end(resolve));

    const receivedHash = hashStream.digest('hex');
    assert.strictEqual(receivedHash, originalHash, 'SHA-256 integrity hash must match');

    // Simulate atomic finalization
    fs.renameSync(destTemp, destFinal);
    assert(fs.existsSync(destFinal));
    assert(!fs.existsSync(destTemp));

    // Simulate corruption test: alter 1 byte
    const corruptedHashStream = crypto.createHash('sha256');
    const corruptedData = Buffer.from(randomData);
    corruptedData[100] ^= 0xFF; // flip bits
    corruptedHashStream.update(corruptedData);
    const badHash = corruptedHashStream.digest('hex');
    assert.notStrictEqual(badHash, originalHash, 'Bit flip must be caught by SHA-256');

    // Cleanup
    fs.rmSync(tmpDir, { recursive: true, force: true });

    console.log('✓ File transfer integrity & chunking unit tests PASSED successfully.');
}

run().catch(err => {
    console.error('Test failed:', err);
    process.exit(1);
});
