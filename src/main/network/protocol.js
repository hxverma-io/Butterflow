// ButterFlow Network Protocol Specification

const MessageTypes = {
    // Ultra-low latency input
    MOUSE_MOVE: 'M',
    MOUSE_BUTTON: 'B',
    MOUSE_WHEEL: 'W',
    KEY_EVENT: 'K',

    // Connection & Lifecycle
    PING: 'PING',
    PONG: 'PONG',
    HANDSHAKE: 'HANDSHAKE',
    HANDSHAKE_ACK: 'HANDSHAKE_ACK',
    PAIR_REQUEST: 'PAIR_REQUEST',
    PAIR_RESPONSE: 'PAIR_RESPONSE',
    DISCONNECT: 'DISCONNECT',

    // Input Control & Edge Switching
    INPUT_ACTIVATE: 'INPUT_ACTIVATE',
    INPUT_RETURN: 'INPUT_RETURN',
    EMERGENCY_RELEASE: 'EMERGENCY_RELEASE',

    // Clipboard
    CLIPBOARD_SYNC: 'CLIPBOARD_SYNC',

    // File Transfer
    FILE_START: 'FILE_START',
    FILE_CHUNK: 'FILE_CHUNK',
    FILE_FINISH: 'FILE_FINISH',
    FILE_CANCEL: 'FILE_CANCEL',
    FILE_ACK: 'FILE_ACK',

    // Screen Sharing
    SCREEN_START: 'SCREEN_START',
    SCREEN_STOP: 'SCREEN_STOP',
    SCREEN_FRAME: 'SCREEN_FRAME',
    REMOTE_CONTROL_REQUEST: 'REMOTE_CONTROL_REQUEST',
    REMOTE_CONTROL_ACK: 'REMOTE_CONTROL_ACK'
};

class Protocol {
    static serialize(obj) {
        return JSON.stringify(obj);
    }

    static deserialize(str) {
        try {
            return JSON.parse(str);
        } catch (e) {
            return null;
        }
    }

    // Fast Input Packet Creators
    static createMouseMove(dx, dy, x, y) {
        return { t: MessageTypes.MOUSE_MOVE, dx, dy, x, y, ts: Date.now() };
    }

    static createMouseButton(button, down) {
        return { t: MessageTypes.MOUSE_BUTTON, b: button, d: down ? 1 : 0, ts: Date.now() };
    }

    static createMouseWheel(delta, horizontal = false) {
        return { t: MessageTypes.MOUSE_WHEEL, w: delta, h: horizontal ? 1 : 0, ts: Date.now() };
    }

    static createKeyEvent(vk, scan, extended, down) {
        return { t: MessageTypes.KEY_EVENT, vk, sc: scan, ext: extended ? 1 : 0, dn: down ? 1 : 0, ts: Date.now() };
    }

    // Ping / Pong for RTT measurement
    static createPing() {
        return { t: MessageTypes.PING, ts: Date.now() };
    }

    static createPong(origTs) {
        return { t: MessageTypes.PONG, origTs, ts: Date.now() };
    }
}

module.exports = {
    MessageTypes,
    Protocol
};
