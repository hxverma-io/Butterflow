const dgram = require('dgram');
const EventEmitter = require('events');
const os = require('os');
const config = require('../config');

class DiscoveryService extends EventEmitter {
    constructor() {
        super();
        this.socket = null;
        this.broadcastInterval = null;
        this.sweepInterval = null;
        this.peers = new Map();
        this.port = config.get('discoveryPort', 38445);
        this.appPort = config.get('port', 38440);
        this.isRunning = false;
    }

    start() {
        if (this.isRunning) return;

        try {
            this.socket = dgram.createSocket({ type: 'udp4', reuseAddr: true });

            this.socket.on('error', (err) => {
                console.error('[Discovery] Socket error:', err.message);
                // Try to recover
            });

            this.socket.on('message', (msg, rinfo) => {
                this.handleMessage(msg, rinfo);
            });

            this.socket.on('listening', () => {
                try {
                    this.socket.setBroadcast(true);
                    this.isRunning = true;
                    console.log(`[Discovery] Listening on UDP port ${this.port}`);
                    this.startBeacon();
                } catch (e) {
                    console.error('[Discovery] Failed to set broadcast:', e.message);
                }
            });

            this.socket.bind(this.port, '0.0.0.0');

            // Periodic sweep for offline peers
            this.sweepInterval = setInterval(() => this.sweepOfflinePeers(), 4000);
        } catch (err) {
            console.error('[Discovery] Failed to start discovery:', err);
        }
    }

    startBeacon() {
        this.sendBeacon();
        this.broadcastInterval = setInterval(() => this.sendBeacon(), 3000);
    }

    sendBeacon() {
        if (!this.socket || !this.isRunning) return;

        const beacon = {
            type: 'BUTTERFLOW_BEACON',
            deviceId: config.get('deviceId'),
            deviceName: config.get('deviceName'),
            port: this.appPort,
            os: os.platform() === 'win32' ? 'Windows' : os.platform(),
            version: '1.0.0',
            screenWidth: config.get('screenLayout').local.width || 1920,
            screenHeight: config.get('screenLayout').local.height || 1080
        };

        const payload = Buffer.from(JSON.stringify(beacon));
        
        // Broadcast on standard broadcast address
        this.socket.send(payload, 0, payload.length, this.port, '255.255.255.255', (err) => {
            if (err && err.code !== 'ENETUNREACH') {
                // Ignore transient unreachable errors if Wi-Fi has no broadcast route
            }
        });
    }

    handleMessage(msgBuffer, rinfo) {
        try {
            const data = JSON.parse(msgBuffer.toString('utf8'));
            if (data.type !== 'BUTTERFLOW_BEACON') return;

            const myId = config.get('deviceId');
            if (data.deviceId === myId) return; // ignore self

            const peerId = data.deviceId;
            const isNew = !this.peers.has(peerId);
            const isPaired = config.isPaired(peerId);

            const peer = {
                deviceId: peerId,
                deviceName: data.deviceName || 'ButterFlow PC',
                ip: rinfo.address,
                port: data.port || 38440,
                os: data.os || 'Windows',
                version: data.version || '1.0.0',
                screenWidth: data.screenWidth || 1920,
                screenHeight: data.screenHeight || 1080,
                lastSeen: Date.now(),
                isPaired,
                status: 'online'
            };

            this.peers.set(peerId, peer);

            if (isNew) {
                console.log(`[Discovery] New peer discovered: ${peer.deviceName} (${peer.ip}:${peer.port})`);
                this.emit('peer_discovered', peer);
            } else {
                this.emit('peer_updated', peer);
            }
        } catch (e) {
            // ignore malformed beacon
        }
    }

    sweepOfflinePeers() {
        const now = Date.now();
        for (const [id, peer] of this.peers.entries()) {
            if (now - peer.lastSeen > 10000 && peer.status === 'online') {
                peer.status = 'offline';
                this.emit('peer_offline', peer);
            }
        }
    }

    getDiscoveredPeers() {
        return Array.from(this.peers.values()).map(p => ({
            ...p,
            isPaired: config.isPaired(p.deviceId)
        }));
    }

    stop() {
        if (this.broadcastInterval) clearInterval(this.broadcastInterval);
        if (this.sweepInterval) clearInterval(this.sweepInterval);
        if (this.socket) {
            try { this.socket.close(); } catch (e) {}
            this.socket = null;
        }
        this.isRunning = false;
    }
}

module.exports = new DiscoveryService();
