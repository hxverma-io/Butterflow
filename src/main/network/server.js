const http = require('http');
const express = require('express');
const { WebSocketServer } = require('ws');
const path = require('path');
const os = require('os');
const config = require('../config');
const security = require('./security');
const { MessageTypes, Protocol } = require('./protocol');
const nativeInput = require('../nativeInput');

function getLocalIpAddresses() {
    const interfaces = os.networkInterfaces();
    const candidateIps = [];

    for (const name of Object.keys(interfaces)) {
        const lowerName = name.toLowerCase();
        for (const iface of interfaces[name]) {
            const isVirtual = lowerName.includes('wsl') || lowerName.includes('vethernet') ||
                              lowerName.includes('virtual') || lowerName.includes('host-only') ||
                              lowerName.includes('pseudo') || lowerName.includes('bluetooth') ||
                              iface.address.startsWith('192.168.56.');

            if (iface.family === 'IPv4' && !iface.internal) {
                // Ignore link-local (169.254.x.x)
                if (iface.address.startsWith('169.254.')) continue;

                candidateIps.push({
                    address: iface.address,
                    name,
                    isVirtual,
                    isPreferred: (lowerName.includes('wi-fi') || lowerName.includes('ethernet') || lowerName.includes('wlan')) && !isVirtual
                });
            }
        }
    }

    // Sort: non-virtual preferred first (Wi-Fi / Ethernet), then others, then virtual
    candidateIps.sort((a, b) => {
        if (!a.isVirtual && b.isVirtual) return -1;
        if (a.isVirtual && !b.isVirtual) return 1;
        if (a.isPreferred && !b.isPreferred) return -1;
        if (!a.isPreferred && b.isPreferred) return 1;
        return 0;
    });

    const ips = candidateIps.map(c => c.address);
    return ips.length > 0 ? ips : ['127.0.0.1'];
}

class ButterFlowServer {
    constructor() {
        this.app = express();
        this.server = null;
        this.wss = null;
        this.port = config.get('port', 38440);
        this.connectedClients = new Map(); // peerId -> { ws, info, latency: 0, p95: 0, rttHistory: [] }
        this.services = {};
    }

    setServices(services) {
        this.services = services;
    }

    start() {
        this.app.use((req, res, next) => {
            res.header('Access-Control-Allow-Origin', '*');
            res.header('Access-Control-Allow-Methods', 'GET, POST, PUT, DELETE, OPTIONS');
            res.header('Access-Control-Allow-Headers', 'Content-Type, Authorization');
            if (req.method === 'OPTIONS') return res.sendStatus(200);
            next();
        });
        this.app.use(express.json({ limit: '50mb' }));
        this.app.use(express.static(path.join(__dirname, '..', '..', 'renderer')));

        this.setupRoutes();

        this.server = http.createServer(this.app);
        this.wss = new WebSocketServer({ server: this.server });

        this.wss.on('connection', (ws, req) => {
            this.handleConnection(ws, req);
        });

        this.server.on('error', (err) => {
            if (err.code === 'EADDRINUSE') {
                console.warn(`[Server] Port ${this.port} is already in use. Automatically trying next port ${this.port + 1}...`);
                this.port += 1;
                config.set('port', this.port);
                setTimeout(() => {
                    this.server.listen(this.port, '0.0.0.0');
                }, 300);
            } else {
                console.error('[Server] Server error:', err);
            }
        });

        this.server.listen(this.port, '0.0.0.0', () => {
            console.log(`[Server] ButterFlow server listening on http://0.0.0.0:${this.port}`);
            if (this.services && this.services.discovery) {
                this.services.discovery.appPort = this.port;
            }
        });
    }

    setupRoutes() {
        // App Info & Status
        this.app.get('/api/info', (req, res) => {
            const ips = getLocalIpAddresses();
            res.json({
                deviceId: config.get('deviceId'),
                deviceName: config.get('deviceName'),
                ip: ips[0] || '127.0.0.1',
                allIps: ips,
                port: this.port,
                theme: config.get('theme'),
                edgeSwitchEnabled: config.get('edgeSwitchEnabled'),
                edgeThreshold: config.get('edgeThreshold'),
                screenLayout: config.get('screenLayout'),
                pairedDevices: config.get('pairedDevices'),
                monitors: nativeInput.monitors,
                downloadDir: config.get('downloadDir'),
                emergencyHotkey: config.get('emergencyHotkey')
            });
        });

        // Discovered Peers
        this.app.get('/api/peers', (req, res) => {
            if (this.services.discovery) {
                res.json(this.services.discovery.getDiscoveredPeers());
            } else {
                res.json([]);
            }
        });

        // Generate Pairing Code for another PC to enter
        this.app.post('/api/pair/generate-code', (req, res) => {
            const pairData = security.generatePairingCode();
            const ips = getLocalIpAddresses();
            pairData.ip = ips[0] || '127.0.0.1';
            pairData.allIps = ips;
            pairData.port = this.port;
            pairData.deviceName = config.get('deviceName');
            config.addLog(`Generated 6-digit pairing code: ${pairData.code} (IP: ${pairData.ip})`);
            res.json(pairData);
        });

        // Submit pairing code to connect to a target peer
        this.app.post('/api/pair/request', async (req, res) => {
            const { peerIp, peerPort, code } = req.body;
            if (!peerIp || !code) {
                return res.json({ success: false, error: 'Remote PC IP and 6-digit PIN are required' });
            }

            try {
                if (this.services.client) {
                    const result = await this.services.client.initiatePairing(peerIp, peerPort || 38440, code);
                    return res.json(result);
                }
                res.json({ success: false, error: 'Client service unavailable' });
            } catch (err) {
                console.warn('[Server] Pairing failed:', err.message);
                res.json({ success: false, error: err.message || 'Pairing failed' });
            }
        });

        // Update Screen Layout
        this.app.post('/api/layout', (req, res) => {
            const layout = req.body;
            config.updateLayout(layout);
            if (this.services.inputManager) {
                this.services.inputManager.updateLayout(layout);
            }
            res.json({ success: true, screenLayout: config.get('screenLayout') });
        });

        // Update General Settings
        this.app.post('/api/settings', (req, res) => {
            const { theme, edgeSwitchEnabled, edgeThreshold, emergencyHotkey, downloadDir } = req.body;
            if (theme) config.set('theme', theme);
            if (edgeSwitchEnabled !== undefined) {
                config.set('edgeSwitchEnabled', edgeSwitchEnabled);
                nativeInput.setBounds(nativeInput.screenWidth, nativeInput.screenHeight, edgeThreshold || 2, edgeSwitchEnabled);
            }
            if (edgeThreshold) config.set('edgeThreshold', edgeThreshold);
            if (emergencyHotkey) config.set('emergencyHotkey', emergencyHotkey);
            if (downloadDir) config.set('downloadDir', downloadDir);

            res.json({ success: true, settings: config.data });
        });

        // Device Permissions
        this.app.post('/api/devices/:id/permissions', (req, res) => {
            const deviceId = req.params.id;
            config.updatePermissions(deviceId, req.body);
            res.json({ success: true, device: config.getPairedDevice(deviceId) });
        });

        // Unpair Device
        this.app.delete('/api/devices/:id', (req, res) => {
            const deviceId = req.params.id;
            config.removePairedDevice(deviceId);
            // Disconnect if connected
            if (this.services.client) {
                this.services.client.disconnect(deviceId);
            }
            res.json({ success: true });
        });

        // Emergency Release Hotkey invocation from UI
        this.app.post('/api/input/emergency-release', (req, res) => {
            if (this.services.inputManager) {
                this.services.inputManager.emergencyRelease();
            }
            res.json({ success: true });
        });

        // Security Logs
        this.app.get('/api/logs', (req, res) => {
            res.json(config.get('securityLogs', []));
        });

        // Latency Benchmark Trigger
        this.app.post('/api/benchmark/start', (req, res) => {
            if (this.services.benchmarkEngine) {
                const results = this.services.benchmarkEngine.runQuickTest();
                res.json(results);
            } else {
                res.status(500).json({ error: 'Benchmark service unavailable' });
            }
        });
    }

    handleConnection(ws, req) {
        let peerDeviceId = null;
        let isAuthenticated = false;

        ws.on('error', (err) => {
            console.warn(`[Server] Socket error from peer ${peerDeviceId || 'unpaired'}:`, err.message);
        });

        ws.on('message', async (data) => {
            let msg;
            try {
                msg = JSON.parse(data.toString('utf8'));
            } catch (e) {
                return;
            }

            // 1. High-priority latency PING / PONG
            if (msg.t === MessageTypes.PING) {
                ws.send(JSON.stringify(Protocol.createPong(msg.ts)));
                return;
            }

            if (msg.t === MessageTypes.PONG) {
                const rtt = Date.now() - msg.origTs;
                if (peerDeviceId && this.connectedClients.has(peerDeviceId)) {
                    const client = this.connectedClients.get(peerDeviceId);
                    client.rttHistory.push(rtt);
                    if (client.rttHistory.length > 50) client.rttHistory.shift();
                    client.latency = rtt;
                    // compute p95
                    const sorted = [...client.rttHistory].sort((a, b) => a - b);
                    const idx95 = Math.floor(sorted.length * 0.95);
                    client.p95 = sorted[idx95] || rtt;
                }
                return;
            }

            // 2. Pairing Request
            if (msg.t === MessageTypes.PAIR_REQUEST) {
                const { code, deviceId, deviceName, ip, port } = msg;
                const isValid = security.verifyPairingCode(code);
                if (isValid) {
                    const token = security.generateSessionToken();
                    config.addPairedDevice({
                        deviceId,
                        deviceName,
                        token,
                        ip: ip || req.socket.remoteAddress,
                        port: port || 38440
                    });

                    isAuthenticated = true;
                    peerDeviceId = deviceId;
                    this.connectedClients.set(deviceId, { ws, info: { deviceId, deviceName }, latency: 0, p95: 0, rttHistory: [] });

                    ws.send(JSON.stringify({
                        t: MessageTypes.PAIR_RESPONSE,
                        success: true,
                        token,
                        myDeviceId: config.get('deviceId'),
                        myDeviceName: config.get('deviceName')
                    }));
                } else {
                    ws.send(JSON.stringify({
                        t: MessageTypes.PAIR_RESPONSE,
                        success: false,
                        error: 'Invalid or expired pairing PIN'
                    }));
                }
                return;
            }

            // 3. Handshake Authentication for Paired Devices
            if (msg.t === MessageTypes.HANDSHAKE) {
                const { deviceId, deviceName, token } = msg;
                const paired = config.getPairedDevice(deviceId);

                if (paired && paired.token === token) {
                    isAuthenticated = true;
                    peerDeviceId = deviceId;
                    this.connectedClients.set(deviceId, { ws, info: { deviceId, deviceName }, latency: 0, p95: 0, rttHistory: [] });

                    ws.send(JSON.stringify({
                        t: MessageTypes.HANDSHAKE_ACK,
                        success: true,
                        myDeviceId: config.get('deviceId'),
                        myDeviceName: config.get('deviceName'),
                        permissions: paired.permissions
                    }));
                    config.addLog(`Authenticated connection from ${deviceName} (${deviceId})`);
                } else {
                    ws.send(JSON.stringify({
                        t: MessageTypes.HANDSHAKE_ACK,
                        success: false,
                        error: 'Device not paired or invalid token'
                    }));
                }
                return;
            }

            // Require authentication for all subsequent operations
            if (!isAuthenticated) return;

            // 4. Input injection handling
            if (msg.t === MessageTypes.MOUSE_MOVE) {
                if (this.services.inputManager) {
                    this.services.inputManager.handleRemoteMouseMove(peerDeviceId, msg);
                }
                return;
            }

            if (msg.t === MessageTypes.MOUSE_BUTTON) {
                if (this.services.inputManager) {
                    this.services.inputManager.handleRemoteMouseButton(peerDeviceId, msg);
                }
                return;
            }

            if (msg.t === MessageTypes.MOUSE_WHEEL) {
                if (this.services.inputManager) {
                    this.services.inputManager.handleRemoteMouseWheel(peerDeviceId, msg);
                }
                return;
            }

            if (msg.t === MessageTypes.KEY_EVENT) {
                if (this.services.inputManager) {
                    this.services.inputManager.handleRemoteKeyEvent(peerDeviceId, msg);
                }
                return;
            }

            if (msg.t === MessageTypes.INPUT_RETURN) {
                if (this.services.inputManager) {
                    this.services.inputManager.handleInputReturn(peerDeviceId, msg);
                }
                return;
            }

            // 5. Clipboard Sync
            if (msg.t === MessageTypes.CLIPBOARD_SYNC) {
                if (this.services.clipboardManager) {
                    this.services.clipboardManager.handleRemoteClipboard(peerDeviceId, msg);
                }
                return;
            }

            // 6. File Transfer
            if (msg.t.startsWith('FILE_')) {
                if (this.services.fileTransferManager) {
                    this.services.fileTransferManager.handleFileMessage(peerDeviceId, msg, ws);
                }
                return;
            }

            // 7. Screen Sharing & Remote Control
            if (msg.t.startsWith('SCREEN_') || msg.t.startsWith('REMOTE_CONTROL_')) {
                if (this.services.screenStreamManager) {
                    this.services.screenStreamManager.handleScreenMessage(peerDeviceId, msg, ws);
                }
                return;
            }
        });

        ws.on('close', () => {
            if (peerDeviceId) {
                this.connectedClients.delete(peerDeviceId);
                if (this.services.inputManager) {
                    this.services.inputManager.handlePeerDisconnect(peerDeviceId);
                }
                config.addLog(`Peer disconnected: ${peerDeviceId}`);
            }
        });
    }

    broadcastToUI(event, data) {
        // Broadcasts state changes to open web/Electron frontends
        if (!this.wss) return;
        const msg = JSON.stringify({ type: 'UI_EVENT', event, data });
        this.wss.clients.forEach(client => {
            if (client.readyState === 1) client.send(msg);
        });
    }

    sendToPeer(peerId, messageObj) {
        const client = this.connectedClients.get(peerId);
        if (client && client.ws && client.ws.readyState === 1) {
            client.ws.send(JSON.stringify(messageObj));
            return true;
        }
        return false;
    }

    stop() {
        if (this.wss) this.wss.close();
        if (this.server) this.server.close();
    }
}

module.exports = new ButterFlowServer();
