const nativeInput = require('../nativeInput');
const config = require('../config');
const { MessageTypes } = require('../network/protocol');

class ScreenStreamManager {
    constructor() {
        this.services = {};
        this.activeSessions = new Map(); // peerId -> { interval, fps, quality, maxWidth }
        this.currentViewerPeerId = null;
    }

    setServices(services) {
        this.services = services;

        nativeInput.on('screen_frame', (frame) => {
            // Send frame to all peers currently streaming from this host
            for (const [peerId, session] of this.activeSessions.entries()) {
                const packet = {
                    t: MessageTypes.SCREEN_FRAME,
                    width: frame.width,
                    height: frame.height,
                    data: frame.data,
                    ts: Date.now()
                };
                this.services.client.send(peerId, packet);
            }
        });
    }

    startSharingToPeer(peerId, fps = 30, quality = 65, maxWidth = 1280) {
        if (this.activeSessions.has(peerId)) {
            this.stopSharingToPeer(peerId);
        }

        console.log(`[ScreenStream] Starting screen stream to peer ${peerId} at ${fps} FPS`);
        const frameIntervalMs = Math.max(16, Math.floor(1000 / fps));

        const interval = setInterval(() => {
            nativeInput.captureScreen(quality, maxWidth);
        }, frameIntervalMs);

        this.activeSessions.set(peerId, { interval, fps, quality, maxWidth });
        config.addLog(`Started screen sharing to ${peerId}`);
    }

    stopSharingToPeer(peerId) {
        const session = this.activeSessions.get(peerId);
        if (session) {
            clearInterval(session.interval);
            this.activeSessions.delete(peerId);
            this.services.client.send(peerId, { t: MessageTypes.SCREEN_STOP });
            console.log(`[ScreenStream] Stopped screen stream to peer ${peerId}`);
            config.addLog(`Stopped screen sharing to ${peerId}`);
        }
    }

    requestScreenStream(peerId, fps = 30, quality = 65) {
        this.currentViewerPeerId = peerId;
        this.services.client.send(peerId, {
            t: MessageTypes.SCREEN_START,
            fps,
            quality
        });
    }

    stopViewingStream(peerId) {
        if (this.currentViewerPeerId === peerId) {
            this.services.client.send(peerId, {
                t: MessageTypes.SCREEN_STOP
            });
            this.currentViewerPeerId = null;
        }
    }

    handleScreenMessage(peerId, msg, ws) {
        switch (msg.t) {
            case MessageTypes.SCREEN_START: {
                const paired = config.getPairedDevice(peerId);
                if (paired && paired.permissions && paired.permissions.screenShare === false) {
                    console.warn(`[ScreenStream] Denied screen share request from ${peerId}: permission denied`);
                    return;
                }
                const fps = msg.fps || 30;
                const quality = msg.quality || 65;
                this.startSharingToPeer(peerId, fps, quality);
                break;
            }
            case MessageTypes.SCREEN_STOP: {
                this.stopSharingToPeer(peerId);
                break;
            }
            case MessageTypes.SCREEN_FRAME: {
                // Forward frame to local UI
                if (this.services.server) {
                    this.services.server.broadcastToUI('REMOTE_SCREEN_FRAME', {
                        peerId,
                        width: msg.width,
                        height: msg.height,
                        data: msg.data
                    });
                }
                break;
            }
            case MessageTypes.REMOTE_CONTROL_REQUEST: {
                const paired = config.getPairedDevice(peerId);
                if (paired && paired.permissions && paired.permissions.remoteControl === false) {
                    console.warn(`[ScreenStream] Remote control denied for ${peerId}`);
                    return;
                }
                // Handle injected remote click / key from viewer
                if (msg.action === 'move') {
                    nativeInput.injectMouseMove(0, 0, msg.x, msg.y, true);
                } else if (msg.action === 'button') {
                    nativeInput.injectMouseButton(msg.button, msg.down);
                } else if (msg.action === 'key') {
                    nativeInput.injectKey(msg.vk, msg.scan, msg.extended, msg.down);
                }
                break;
            }
        }
    }

    stop() {
        for (const peerId of this.activeSessions.keys()) {
            this.stopSharingToPeer(peerId);
        }
    }
}

module.exports = new ScreenStreamManager();
