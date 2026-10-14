const { spawn } = require('child_process');
const path = require('path');
const EventEmitter = require('events');
const fs = require('fs');

class NativeInputService extends EventEmitter {
    constructor() {
        super();
        this.process = null;
        this.isReady = false;
        this.screenWidth = 1920;
        this.screenHeight = 1080;
        this.remoteActive = false;
        this.monitors = [];
        this.buffer = '';
    }

    start() {
        const exePath = path.join(__dirname, 'native', 'ButterFlowNative.exe');
        
        if (!fs.existsSync(exePath)) {
            console.warn(`[NativeInput] ButterFlowNative.exe not found at ${exePath}. Running in simulated mode.`);
            this.isReady = true;
            this.emit('ready', { screenWidth: this.screenWidth, screenHeight: this.screenHeight });
            return;
        }

        try {
            this.process = spawn(exePath, [], {
                stdio: ['pipe', 'pipe', 'inherit'],
                windowsHide: true
            });

            this.process.stdout.on('data', (data) => {
                this.buffer += data.toString('utf8');
                const lines = this.buffer.split('\n');
                this.buffer = lines.pop(); // keep remainder

                for (const line of lines) {
                    const trimmed = line.trim();
                    if (!trimmed) continue;
                    try {
                        const msg = JSON.parse(trimmed);
                        this.handleNativeMessage(msg);
                    } catch (e) {
                        // ignore malformed line
                    }
                }
            });

            this.process.on('error', (err) => {
                console.error('[NativeInput] Native process error:', err);
                this.emit('error', err);
            });

            this.process.on('exit', (code) => {
                console.log(`[NativeInput] Native process exited with code ${code}`);
                this.isReady = false;
                this.process = null;
            });
        } catch (err) {
            console.error('[NativeInput] Failed to spawn native input hook:', err);
            this.isReady = true;
            this.emit('ready', { screenWidth: this.screenWidth, screenHeight: this.screenHeight });
        }
    }

    handleNativeMessage(msg) {
        switch (msg.type) {
            case 'READY':
                this.isReady = true;
                this.screenWidth = msg.screenWidth || 1920;
                this.screenHeight = msg.screenHeight || 1080;
                this.emit('ready', { screenWidth: this.screenWidth, screenHeight: this.screenHeight });
                this.getMonitors();
                break;
            case 'EDGE_HIT':
                this.emit('edge_hit', { edge: msg.edge, x: msg.x, y: msg.y });
                break;
            case 'MOUSE_MOVE':
                this.emit('mouse_move', { dx: msg.dx, dy: msg.dy, x: msg.x, y: msg.y });
                break;
            case 'MOUSE_BUTTON':
                this.emit('mouse_button', { button: msg.button, down: msg.down });
                break;
            case 'MOUSE_WHEEL':
                this.emit('mouse_wheel', { delta: msg.delta, horizontal: msg.horizontal });
                break;
            case 'KEY_EVENT':
                this.emit('key_event', { vk: msg.vk, scan: msg.scan, extended: msg.extended, down: msg.down });
                break;
            case 'EMERGENCY_RELEASE':
                this.remoteActive = false;
                this.emit('emergency_release');
                break;
            case 'SCREEN_FRAME':
                this.emit('screen_frame', { width: msg.width, height: msg.height, data: msg.data });
                break;
            case 'MONITORS':
                this.monitors = msg.monitors || [];
                this.emit('monitors', this.monitors);
                break;
            case 'REMOTE_STATE':
                this.remoteActive = !!msg.active;
                this.emit('remote_state', this.remoteActive);
                break;
            case 'ERROR':
                console.error('[NativeInput] Hook error:', msg.message);
                break;
        }
    }

    sendCommand(cmdObj) {
        if (!this.process || !this.process.stdin.writable) return;
        try {
            this.process.stdin.write(JSON.stringify(cmdObj) + '\n');
        } catch (err) {
            console.error('[NativeInput] Error sending command:', err);
        }
    }

    setRemote(active) {
        this.remoteActive = active;
        this.sendCommand({ command: 'SET_REMOTE', active });
    }

    setBounds(width, height, threshold = 2, enable = true) {
        this.screenWidth = width;
        this.screenHeight = height;
        this.sendCommand({ command: 'SET_BOUNDS', width, height, threshold, enable });
    }

    injectMouseMove(dx, dy, x = 0, y = 0, absolute = false) {
        this.sendCommand({ command: 'INJECT_MOUSE', action: 'move', dx, dy, x, y, absolute });
    }

    injectMouseButton(button, down) {
        this.sendCommand({ command: 'INJECT_MOUSE', action: 'button', button, down });
    }

    injectMouseWheel(delta, horizontal = false) {
        this.sendCommand({ command: 'INJECT_MOUSE', action: 'wheel', delta, horizontal });
    }

    injectKey(vk, scan = 0, extended = false, down = true) {
        this.sendCommand({ command: 'INJECT_KEY', vk, scan, extended, down });
    }

    resetKeys() {
        this.sendCommand({ command: 'RESET_KEYS' });
    }

    captureScreen(quality = 70, maxWidth = 1280) {
        this.sendCommand({ command: 'CAPTURE_SCREEN', quality, maxWidth });
    }

    getMonitors() {
        this.sendCommand({ command: 'GET_MONITORS' });
    }

    setCursorPos(x, y) {
        this.sendCommand({ command: 'SET_CURSOR_POS', x, y });
    }

    stop() {
        if (this.process) {
            this.resetKeys();
            this.setRemote(false);
            this.process.kill();
            this.process = null;
        }
    }
}

module.exports = new NativeInputService();
