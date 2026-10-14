const EventEmitter = require('events');
const nativeInput = require('../nativeInput');
const config = require('../config');
const { MessageTypes, Protocol } = require('../network/protocol');

class InputManager extends EventEmitter {
    constructor() {
        super();
        this.activeTargetPeerId = null;
        this.activeRemoteSourcePeerId = null;
        this.services = {};
        this.layout = config.get('screenLayout', { local: {}, peers: {} });
        this.heldKeys = new Set();
        this.isLocked = false;
    }

    setServices(services) {
        this.services = services;
        this.setupNativeListeners();
    }

    setupNativeListeners() {
        nativeInput.on('ready', (screen) => {
            console.log(`[InputManager] Native input ready: ${screen.screenWidth}x${screen.screenHeight}`);
            const currentLayout = config.get('screenLayout');
            currentLayout.local = {
                ...currentLayout.local,
                width: screen.screenWidth,
                height: screen.screenHeight,
                name: config.get('deviceName')
            };
            config.updateLayout(currentLayout);
        });

        nativeInput.on('edge_hit', (data) => {
            this.handleLocalEdgeHit(data.edge, data.x, data.y);
        });

        nativeInput.on('mouse_move', (data) => {
            if (this.activeTargetPeerId) {
                const packet = Protocol.createMouseMove(data.dx, data.dy, data.x, data.y);
                this.services.client.send(this.activeTargetPeerId, packet);
            }
        });

        nativeInput.on('mouse_button', (data) => {
            if (this.activeTargetPeerId) {
                const packet = Protocol.createMouseButton(data.button, data.down);
                this.services.client.send(this.activeTargetPeerId, packet);
            }
        });

        nativeInput.on('mouse_wheel', (data) => {
            if (this.activeTargetPeerId) {
                const packet = Protocol.createMouseWheel(data.delta, data.horizontal);
                this.services.client.send(this.activeTargetPeerId, packet);
            }
        });

        nativeInput.on('key_event', (data) => {
            if (this.activeTargetPeerId) {
                if (data.down) this.heldKeys.add(data.vk);
                else this.heldKeys.delete(data.vk);

                const packet = Protocol.createKeyEvent(data.vk, data.scan, data.extended, data.down);
                this.services.client.send(this.activeTargetPeerId, packet);
            }
        });

        nativeInput.on('emergency_release', () => {
            console.log('[InputManager] Emergency release triggered by native hotkey');
            this.emergencyRelease();
        });
    }

    handleLocalEdgeHit(edge, x, y) {
        if (!config.get('edgeSwitchEnabled', true)) return;
        if (this.activeTargetPeerId) return; // already remote
        if (this.isLocked) return;

        const layout = config.get('screenLayout');
        const peers = layout.peers || {};

        // Find peer configured at this edge
        let targetPeer = null;
        for (const peer of Object.values(peers)) {
            if (peer.edge === edge) {
                targetPeer = peer;
                break;
            }
        }

        if (!targetPeer) return;

        const paired = config.getPairedDevice(targetPeer.deviceId);
        if (!paired) return;
        if (paired.permissions && paired.permissions.input === false) return;

        // Switch to remote input
        this.activeTargetPeerId = targetPeer.deviceId;
        nativeInput.setRemote(true);

        // Calculate normalized entry position (0.0 - 1.0) along edge
        const local = layout.local || { width: 1920, height: 1080 };
        const ratio = (edge === 'left' || edge === 'right')
            ? (y / (local.height || 1080))
            : (x / (local.width || 1920));

        this.services.client.send(this.activeTargetPeerId, {
            t: MessageTypes.INPUT_ACTIVATE,
            sourceEdge: edge,
            ratio,
            sourceWidth: local.width,
            sourceHeight: local.height
        });

        if (this.services.server) {
            this.services.server.broadcastToUI('INPUT_FOCUS_CHANGED', {
                target: this.activeTargetPeerId,
                name: paired.deviceName
            });
        }

        config.addLog(`Switched input to ${paired.deviceName} via ${edge} edge`);
    }

    // Remote PC receiving input from original PC
    handleRemoteMouseMove(peerId, msg) {
        const paired = config.getPairedDevice(peerId);
        if (paired && paired.permissions && paired.permissions.input === false) return;

        this.activeRemoteSourcePeerId = peerId;
        nativeInput.injectMouseMove(msg.dx, msg.dy, msg.x, msg.y, false);
    }

    handleRemoteMouseButton(peerId, msg) {
        const paired = config.getPairedDevice(peerId);
        if (paired && paired.permissions && paired.permissions.input === false) return;

        nativeInput.injectMouseButton(msg.b, msg.d === 1);
    }

    handleRemoteMouseWheel(peerId, msg) {
        const paired = config.getPairedDevice(peerId);
        if (paired && paired.permissions && paired.permissions.input === false) return;

        nativeInput.injectMouseWheel(msg.w, msg.h === 1);
    }

    handleRemoteKeyEvent(peerId, msg) {
        const paired = config.getPairedDevice(peerId);
        if (paired && paired.permissions && paired.permissions.input === false) return;

        nativeInput.injectKey(msg.vk, msg.sc, msg.ext === 1, msg.dn === 1);
    }

    handleInputReturn(peerId, msg) {
        if (this.activeTargetPeerId === peerId) {
            this.restoreLocalControl();
        }
    }

    restoreLocalControl() {
        this.activeTargetPeerId = null;
        nativeInput.setRemote(false);
        this.clearStuckKeys();

        if (this.services.server) {
            this.services.server.broadcastToUI('INPUT_FOCUS_CHANGED', {
                target: 'local',
                name: config.get('deviceName')
            });
        }
    }

    clearStuckKeys() {
        this.heldKeys.clear();
        nativeInput.resetKeys();
    }

    emergencyRelease() {
        if (this.activeTargetPeerId) {
            this.services.client.send(this.activeTargetPeerId, {
                t: MessageTypes.EMERGENCY_RELEASE
            });
        }
        this.restoreLocalControl();
        config.addLog('Emergency release activated: local input restored', 'warn');
    }

    handlePeerDisconnect(peerId) {
        if (this.activeTargetPeerId === peerId) {
            console.log(`[InputManager] Active target peer ${peerId} disconnected, restoring local input`);
            this.restoreLocalControl();
        }
        if (this.activeRemoteSourcePeerId === peerId) {
            this.activeRemoteSourcePeerId = null;
            nativeInput.resetKeys();
        }
    }

    updateLayout(layout) {
        this.layout = layout;
    }
}

module.exports = new InputManager();
