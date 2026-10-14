const WebSocket = require('ws');
const EventEmitter = require('events');
const config = require('../config');
const { MessageTypes, Protocol } = require('./protocol');

class ButterFlowClient extends EventEmitter {
    constructor() {
        super();
        this.connections = new Map(); // peerId -> { ws, ip, port, isConnected, latency: 0, p95: 0, rttHistory: [] }
        this.pingInterval = null;
        this.reconnectInterval = null;
        this.services = {};
    }

    setServices(services) {
        this.services = services;
    }

    start() {
        // Start ping interval for latency measurement
        this.pingInterval = setInterval(() => this.pingConnectedPeers(), 1000);

        // Auto-reconnect to paired peers every 4 seconds if disconnected
        this.reconnectInterval = setInterval(() => this.autoReconnect(), 4000);
    }

    async initiatePairing(ip, port, code) {
        return new Promise((resolve, reject) => {
            const cleanIp = (ip || '').trim();
            const cleanPort = port || 38440;
            const url = `ws://${cleanIp}:${cleanPort}`;
            let ws;
            let finished = false;

            const cleanup = () => {
                finished = true;
                if (timeout) clearTimeout(timeout);
            };

            const timeout = setTimeout(() => {
                if (finished) return;
                cleanup();
                if (ws) {
                    try { ws.terminate(); } catch (e) {}
                }
                reject(new Error(`Connection to ${cleanIp}:${cleanPort} timed out. Ensure ButterFlow is running on that PC.`));
            }, 8000);

            try {
                ws = new WebSocket(url);
            } catch (err) {
                cleanup();
                return reject(err);
            }

            ws.on('open', () => {
                const pairReq = {
                    t: MessageTypes.PAIR_REQUEST,
                    code: (code || '').toString().trim(),
                    deviceId: config.get('deviceId'),
                    deviceName: config.get('deviceName'),
                    ip: cleanIp,
                    port: config.get('port')
                };
                try {
                    ws.send(JSON.stringify(pairReq));
                } catch (e) {
                    cleanup();
                    reject(e);
                }
            });

            ws.on('message', (data) => {
                if (finished) return;
                try {
                    const res = JSON.parse(data.toString('utf8'));
                    if (res.t === MessageTypes.PAIR_RESPONSE) {
                        cleanup();
                        if (res.success) {
                            config.addPairedDevice({
                                deviceId: res.myDeviceId,
                                deviceName: res.myDeviceName,
                                token: res.token,
                                ip: cleanIp,
                                port: cleanPort
                            });

                            // Add to screen layout if not present
                            const layout = config.get('screenLayout');
                            if (!layout.peers[res.myDeviceId]) {
                                layout.peers[res.myDeviceId] = {
                                    deviceId: res.myDeviceId,
                                    deviceName: res.myDeviceName,
                                    edge: 'right',
                                    x: 1920,
                                    y: 0,
                                    width: 1920,
                                    height: 1080
                                };
                                config.updateLayout(layout);
                            }

                            // Keep connection active
                            this.trackConnection(res.myDeviceId, ws, cleanIp, cleanPort);
                            resolve({ success: true, deviceId: res.myDeviceId, deviceName: res.myDeviceName });
                        } else {
                            try { ws.close(); } catch (e) {}
                            reject(new Error(res.error || 'Pairing rejected by remote PC'));
                        }
                    }
                } catch (e) {
                    cleanup();
                    reject(e);
                }
            });

            ws.on('error', (err) => {
                if (finished) return;
                cleanup();
                let msg = err.message || 'Connection error';
                if (err.code === 'ECONNREFUSED') {
                    msg = `Could not connect to ${cleanIp}:${cleanPort}. Ensure ButterFlow is open on that PC.`;
                } else if (err.code === 'EHOSTUNREACH') {
                    msg = `Cannot reach ${cleanIp}. Ensure both PCs are connected to the same Wi-Fi network.`;
                }
                reject(new Error(msg));
            });

            ws.on('close', () => {
                if (!finished) {
                    cleanup();
                    reject(new Error('Connection closed by remote PC before pairing completed'));
                }
            });
        });
    }

    connectToPeer(peerId) {
        const paired = config.getPairedDevice(peerId);
        if (!paired || !paired.ip) return;

        const existing = this.connections.get(peerId);
        if (existing && existing.ws && (existing.ws.readyState === WebSocket.OPEN || existing.ws.readyState === WebSocket.CONNECTING)) {
            return; // already connected or connecting
        }

        const url = `ws://${paired.ip}:${paired.port || 38440}`;
        let ws;
        try {
            ws = new WebSocket(url, { timeout: 4000 });
        } catch (e) {
            return;
        }

        ws.on('open', () => {
            // Handshake
            const handshake = {
                t: MessageTypes.HANDSHAKE,
                deviceId: config.get('deviceId'),
                deviceName: config.get('deviceName'),
                token: paired.token
            };
            ws.send(JSON.stringify(handshake));
        });

        ws.on('message', (data) => {
            this.handlePeerMessage(peerId, data, ws);
        });

        ws.on('close', () => {
            const conn = this.connections.get(peerId);
            if (conn) conn.isConnected = false;
            this.emit('peer_disconnected', peerId);
        });

        ws.on('error', (err) => {
            // connection error handled gracefully on close
        });

        this.trackConnection(peerId, ws, paired.ip, paired.port);
    }

    trackConnection(peerId, ws, ip, port) {
        const conn = {
            ws,
            ip,
            port,
            isConnected: true,
            latency: 0,
            p95: 0,
            rttHistory: []
        };
        this.connections.set(peerId, conn);
    }

    handlePeerMessage(peerId, data, ws) {
        let msg;
        try {
            msg = JSON.parse(data.toString('utf8'));
        } catch (e) {
            return;
        }

        if (msg.t === MessageTypes.PING) {
            ws.send(JSON.stringify(Protocol.createPong(msg.ts)));
            return;
        }

        if (msg.t === MessageTypes.PONG) {
            const rtt = Date.now() - msg.origTs;
            const conn = this.connections.get(peerId);
            if (conn) {
                conn.rttHistory.push(rtt);
                if (conn.rttHistory.length > 50) conn.rttHistory.shift();
                conn.latency = rtt;
                const sorted = [...conn.rttHistory].sort((a, b) => a - b);
                const idx95 = Math.floor(sorted.length * 0.95);
                conn.p95 = sorted[idx95] || rtt;
            }
            return;
        }

        if (msg.t === MessageTypes.HANDSHAKE_ACK) {
            if (msg.success) {
                const conn = this.connections.get(peerId);
                if (conn) conn.isConnected = true;
                this.emit('peer_connected', peerId);
            }
            return;
        }

        // Delegate to server router if incoming through client socket
        if (this.services.server) {
            // Route message through same handlers
            if (msg.t === MessageTypes.MOUSE_MOVE && this.services.inputManager) {
                this.services.inputManager.handleRemoteMouseMove(peerId, msg);
            } else if (msg.t === MessageTypes.MOUSE_BUTTON && this.services.inputManager) {
                this.services.inputManager.handleRemoteMouseButton(peerId, msg);
            } else if (msg.t === MessageTypes.MOUSE_WHEEL && this.services.inputManager) {
                this.services.inputManager.handleRemoteMouseWheel(peerId, msg);
            } else if (msg.t === MessageTypes.KEY_EVENT && this.services.inputManager) {
                this.services.inputManager.handleRemoteKeyEvent(peerId, msg);
            } else if (msg.t === MessageTypes.INPUT_RETURN && this.services.inputManager) {
                this.services.inputManager.handleInputReturn(peerId, msg);
            } else if (msg.t === MessageTypes.CLIPBOARD_SYNC && this.services.clipboardManager) {
                this.services.clipboardManager.handleRemoteClipboard(peerId, msg);
            } else if (msg.t.startsWith('FILE_') && this.services.fileTransferManager) {
                this.services.fileTransferManager.handleFileMessage(peerId, msg, ws);
            } else if ((msg.t.startsWith('SCREEN_') || msg.t.startsWith('REMOTE_CONTROL_')) && this.services.screenStreamManager) {
                this.services.screenStreamManager.handleScreenMessage(peerId, msg, ws);
            }
        }
    }

    send(peerId, messageObj) {
        // Try outbound client socket first
        const conn = this.connections.get(peerId);
        if (conn && conn.ws && conn.ws.readyState === WebSocket.OPEN) {
            conn.ws.send(JSON.stringify(messageObj));
            return true;
        }

        // Fallback to inbound server socket
        if (this.services.server && this.services.server.sendToPeer(peerId, messageObj)) {
            return true;
        }

        return false;
    }

    pingConnectedPeers() {
        const pingMsg = JSON.stringify(Protocol.createPing());
        for (const [peerId, conn] of this.connections.entries()) {
            if (conn.ws && conn.ws.readyState === WebSocket.OPEN) {
                conn.ws.send(pingMsg);
            }
        }
    }

    autoReconnect() {
        if (!config.get('autoReconnect', true)) return;
        const paired = config.get('pairedDevices', {});
        for (const peerId of Object.keys(paired)) {
            const conn = this.connections.get(peerId);
            if (!conn || !conn.ws || conn.ws.readyState === WebSocket.CLOSED) {
                this.connectToPeer(peerId);
            }
        }
    }

    disconnect(peerId) {
        const conn = this.connections.get(peerId);
        if (conn && conn.ws) {
            try { conn.ws.close(); } catch (e) {}
            this.connections.delete(peerId);
        }
    }

    getConnectionMetrics(peerId) {
        const conn = this.connections.get(peerId);
        if (conn) {
            return {
                latency: conn.latency,
                p95: conn.p95,
                isConnected: conn.isConnected
            };
        }
        return { latency: 0, p95: 0, isConnected: false };
    }

    stop() {
        if (this.pingInterval) clearInterval(this.pingInterval);
        if (this.reconnectInterval) clearInterval(this.reconnectInterval);
        for (const conn of this.connections.values()) {
            if (conn.ws) {
                try { conn.ws.close(); } catch (e) {}
            }
        }
        this.connections.clear();
    }
}

module.exports = new ButterFlowClient();
