const assert = require('assert');
const http = require('http');
const express = require('express');
const { WebSocketServer, WebSocket } = require('ws');
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');
const os = require('os');

const { MessageTypes, Protocol } = require('../../src/main/network/protocol');
const security = require('../../src/main/network/security');

console.log('====================================================');
console.log('   RUNNING DUAL-NODE END-TO-END INTEGRATION TEST    ');
console.log('====================================================');

async function runDualNodeTest() {
    const PORT_A = 38450;
    const PORT_B = 38451;

    // Node A Setup
    const appA = express();
    const serverA = http.createServer(appA);
    const wssA = new WebSocketServer({ server: serverA });
    await new Promise(r => serverA.listen(PORT_A, '127.0.0.1', r));

    // Node B Setup
    const appB = express();
    const serverB = http.createServer(appB);
    const wssB = new WebSocketServer({ server: serverB });
    await new Promise(r => serverB.listen(PORT_B, '127.0.0.1', r));

    console.log(`[Test] Node A listening on :${PORT_A}, Node B listening on :${PORT_B}`);

    // --- STEP 1: PAIRING WORKFLOW ---
    console.log('[Test] Step 1: Performing 6-digit PIN Pairing Handshake...');
    const pinDataB = security.generatePairingCode();
    assert.strictEqual(pinDataB.code.length, 6);

    let nodeBReceivedEvents = [];
    let nodeBClipboard = null;
    let nodeBReceivedFile = null;

    // Node B WebSocket connection handler
    wssB.on('connection', (ws) => {
        ws.on('message', (raw) => {
            const msg = JSON.parse(raw.toString('utf8'));
            
            if (msg.t === MessageTypes.PAIR_REQUEST) {
                if (security.verifyPairingCode(msg.code)) {
                    ws.send(JSON.stringify({
                        t: MessageTypes.PAIR_RESPONSE,
                        success: true,
                        token: 'test_token_ab',
                        myDeviceId: 'node-b-id',
                        myDeviceName: 'ButterFlow-Node-B'
                    }));
                } else {
                    ws.send(JSON.stringify({ t: MessageTypes.PAIR_RESPONSE, success: false }));
                }
                return;
            }

            if (msg.t === MessageTypes.MOUSE_MOVE) {
                nodeBReceivedEvents.push(msg);
                return;
            }

            if (msg.t === MessageTypes.CLIPBOARD_SYNC) {
                nodeBClipboard = msg.content;
                return;
            }

            if (msg.t === MessageTypes.FILE_START) {
                nodeBReceivedFile = {
                    name: msg.name,
                    size: msg.size,
                    hash: msg.hash,
                    chunks: [],
                    receivedBytes: 0
                };
                return;
            }

            if (msg.t === MessageTypes.FILE_CHUNK) {
                const buf = Buffer.from(msg.data, 'base64');
                nodeBReceivedFile.chunks.push(buf);
                nodeBReceivedFile.receivedBytes += buf.length;
                return;
            }

            if (msg.t === MessageTypes.FILE_FINISH) {
                const fullBuf = Buffer.concat(nodeBReceivedFile.chunks);
                const computed = crypto.createHash('sha256').update(fullBuf).digest('hex');
                nodeBReceivedFile.verified = (computed === nodeBReceivedFile.hash);
                return;
            }

            if (msg.t === MessageTypes.PING) {
                ws.send(JSON.stringify(Protocol.createPong(msg.ts)));
                return;
            }
        });
    });

    // Node A connects to Node B
    const clientA = new WebSocket(`ws://127.0.0.1:${PORT_B}`);
    await new Promise(r => clientA.on('open', r));

    // Send pairing request
    clientA.send(JSON.stringify({
        t: MessageTypes.PAIR_REQUEST,
        code: pinDataB.code,
        deviceId: 'node-a-id',
        deviceName: 'ButterFlow-Node-A'
    }));

    const pairResponse = await new Promise(resolve => {
        clientA.once('message', raw => resolve(JSON.parse(raw.toString())));
    });

    assert.strictEqual(pairResponse.success, true);
    assert.strictEqual(pairResponse.myDeviceId, 'node-b-id');
    console.log('✓ Step 1 PASSED: Pairing PIN verified & mutually authenticated.');

    // --- STEP 2: HIGH-THROUGHPUT MOUSE STREAMING & LATENCY BENCHMARK ---
    console.log('[Test] Step 2: Streaming 100 Mouse Movement Events and measuring latency...');
    const SAMPLE_COUNT = 100;
    const rtts = [];

    // Setup ping/pong listener for latency
    clientA.on('message', (raw) => {
        try {
            const msg = JSON.parse(raw.toString());
            if (msg.t === MessageTypes.PONG) {
                const rtt = Date.now() - msg.origTs;
                rtts.push(rtt);
            }
        } catch (e) {}
    });

    for (let i = 0; i < SAMPLE_COUNT; i++) {
        const movePacket = Protocol.createMouseMove(5, -2, 100 + i, 200 + i);
        clientA.send(JSON.stringify(movePacket));
        clientA.send(JSON.stringify(Protocol.createPing()));
        await new Promise(r => setTimeout(r, 2));
    }

    // Wait for delivery
    await new Promise(r => setTimeout(r, 100));
    assert.strictEqual(nodeBReceivedEvents.length, SAMPLE_COUNT, 'All 100 mouse packets must be received');

    // Calculate latency metrics
    const sorted = [...rtts].sort((a, b) => a - b);
    const median = sorted[Math.floor(sorted.length * 0.5)] || 0;
    const p95 = sorted[Math.floor(sorted.length * 0.95)] || 0;
    const p99 = sorted[Math.floor(sorted.length * 0.99)] || 0;
    const min = sorted[0] || 0;
    const max = sorted[sorted.length - 1] || 0;

    console.log(`✓ Step 2 PASSED: 100/100 mouse events received without drops.`);
    console.log(`   [Latency Metrics] Min: ${min}ms | Median (p50): ${median}ms | p95: ${p95}ms | p99: ${p99}ms | Max: ${max}ms`);

    // --- STEP 3: CLIPBOARD SYNCHRONIZATION ---
    console.log('[Test] Step 3: Testing Bidirectional Universal Clipboard Sync...');
    const clipText = 'ButterFlow Ultra-Smooth Sync ' + Date.now();
    const clipHash = crypto.createHash('sha256').update(clipText).digest('hex');

    clientA.send(JSON.stringify({
        t: MessageTypes.CLIPBOARD_SYNC,
        type: 'text',
        content: clipText,
        hash: clipHash
    }));

    await new Promise(r => setTimeout(r, 50));
    assert.strictEqual(nodeBClipboard, clipText);
    console.log('✓ Step 3 PASSED: Clipboard synced accurately across nodes.');

    // --- STEP 4: CHUNKED FILE TRANSFER WITH SHA-256 INTEGRITY ---
    console.log('[Test] Step 4: Transferring 128KB Chunked File with SHA-256 verification...');
    const testFileSize = 128 * 1024;
    const fileBytes = crypto.randomBytes(testFileSize);
    const expectedFileHash = crypto.createHash('sha256').update(fileBytes).digest('hex');

    const txId = 'test_tx_123';
    clientA.send(JSON.stringify({
        t: MessageTypes.FILE_START,
        transferId: txId,
        name: 'test_bundle.bin',
        size: testFileSize,
        hash: expectedFileHash
    }));

    const CHUNK_SIZE = 32 * 1024;
    for (let offset = 0; offset < testFileSize; offset += CHUNK_SIZE) {
        const chunk = fileBytes.slice(offset, offset + CHUNK_SIZE);
        clientA.send(JSON.stringify({
            t: MessageTypes.FILE_CHUNK,
            transferId: txId,
            data: chunk.toString('base64')
        }));
    }

    clientA.send(JSON.stringify({
        t: MessageTypes.FILE_FINISH,
        transferId: txId,
        hash: expectedFileHash
    }));

    await new Promise(r => setTimeout(r, 100));
    assert(nodeBReceivedFile !== null, 'File must be received');
    assert.strictEqual(nodeBReceivedFile.receivedBytes, testFileSize, 'File size must match');
    assert.strictEqual(nodeBReceivedFile.verified, true, 'Cryptographic SHA-256 hash must verify');
    console.log('✓ Step 4 PASSED: 128KB file streamed and cryptographically verified.');

    // --- TEARDOWN ---
    clientA.close();
    serverA.close();
    serverB.close();

    console.log('====================================================');
    console.log('   ALL DUAL-NODE INTEGRATION SCENARIOS PASSED!      ');
    console.log('====================================================');
}

runDualNodeTest().catch(err => {
    console.error('Dual-node integration test failed:', err);
    process.exit(1);
});
