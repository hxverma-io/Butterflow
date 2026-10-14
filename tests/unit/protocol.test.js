const assert = require('assert');
const { MessageTypes, Protocol } = require('../../src/main/network/protocol');

console.log('[Test] Running Protocol Serialization Unit Tests...');

// 1. Mouse Move Packet
const move = Protocol.createMouseMove(15, -7, 1920, 540);
assert.strictEqual(move.t, MessageTypes.MOUSE_MOVE);
assert.strictEqual(move.dx, 15);
assert.strictEqual(move.dy, -7);
assert.strictEqual(move.x, 1920);
assert.strictEqual(move.y, 540);
assert(typeof move.ts === 'number');

// Serialization / Deserialization
const serialized = Protocol.serialize(move);
const deserialized = Protocol.deserialize(serialized);
assert.deepStrictEqual(deserialized, move);

// 2. Mouse Button Packet
const btn = Protocol.createMouseButton('left', true);
assert.strictEqual(btn.t, MessageTypes.MOUSE_BUTTON);
assert.strictEqual(btn.b, 'left');
assert.strictEqual(btn.d, 1);

// 3. Key Event Packet
const key = Protocol.createKeyEvent(0x41, 0x1E, false, true); // Key 'A' down
assert.strictEqual(key.t, MessageTypes.KEY_EVENT);
assert.strictEqual(key.vk, 0x41);
assert.strictEqual(key.sc, 0x1E);
assert.strictEqual(key.ext, 0);
assert.strictEqual(key.dn, 1);

// 4. Ping / Pong
const ping = Protocol.createPing();
const pong = Protocol.createPong(ping.ts);
assert.strictEqual(pong.origTs, ping.ts);
assert(pong.ts >= ping.ts);

console.log('✓ Protocol unit tests PASSED successfully.');
