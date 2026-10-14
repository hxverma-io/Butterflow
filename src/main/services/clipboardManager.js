const crypto = require('crypto');
const { exec } = require('child_process');
const config = require('../config');
const { MessageTypes } = require('../network/protocol');

class ClipboardManager {
    constructor() {
        this.services = {};
        this.lastSentHash = '';
        this.lastReceivedHash = '';
        this.pollingInterval = null;
        this.lastText = '';
        this.electronClipboard = null;
    }

    setServices(services) {
        this.services = services;
    }

    setElectronClipboard(clipboard) {
        this.electronClipboard = clipboard;
    }

    start() {
        // Poll clipboard every 800ms
        this.pollingInterval = setInterval(() => this.checkLocalClipboard(), 800);
    }

    async checkLocalClipboard() {
        if (!config.get('clipboardSync', true)) return;

        try {
            let currentText = '';
            if (this.electronClipboard) {
                currentText = this.electronClipboard.readText();
            } else {
                currentText = await this.readClipboardFromPowershell();
            }

            if (!currentText || currentText === this.lastText) return;
            this.lastText = currentText;

            const hash = crypto.createHash('sha256').update(currentText).digest('hex');
            if (hash === this.lastReceivedHash || hash === this.lastSentHash) {
                return; // Loop prevention!
            }

            this.lastSentHash = hash;
            this.broadcastClipboard({
                type: 'text',
                content: currentText,
                hash
            });
        } catch (e) {
            // ignore clipboard lock errors
        }
    }

    broadcastClipboard(data) {
        const paired = config.get('pairedDevices', {});
        const packet = {
            t: MessageTypes.CLIPBOARD_SYNC,
            type: data.type,
            content: data.content,
            hash: data.hash,
            sourceDeviceId: config.get('deviceId'),
            sourceName: config.get('deviceName')
        };

        for (const [peerId, peer] of Object.entries(paired)) {
            if (peer.permissions && peer.permissions.clipboard === false) continue;
            this.services.client.send(peerId, packet);
        }
    }

    handleRemoteClipboard(peerId, msg) {
        const paired = config.getPairedDevice(peerId);
        if (paired && paired.permissions && paired.permissions.clipboard === false) return;

        if (!msg.content || !msg.hash) return;
        if (msg.hash === this.lastSentHash) return; // loop prevention

        this.lastReceivedHash = msg.hash;
        this.lastText = msg.content;

        if (this.electronClipboard) {
            if (msg.type === 'text') {
                this.electronClipboard.writeText(msg.content);
            }
        } else {
            this.writeClipboardViaPowershell(msg.content);
        }

        if (this.services.server) {
            this.services.server.broadcastToUI('CLIPBOARD_UPDATED', {
                from: paired ? paired.deviceName : 'Remote PC',
                type: msg.type,
                length: msg.content.length
            });
        }

        config.addLog(`Clipboard synchronized from ${paired ? paired.deviceName : peerId}`);
    }

    readClipboardFromPowershell() {
        return new Promise((resolve) => {
            exec('powershell -NoProfile -Command "Get-Clipboard"', (err, stdout) => {
                if (err) return resolve('');
                resolve(stdout.trim());
            });
        });
    }

    writeClipboardViaPowershell(text) {
        const escaped = text.replace(/`/g, '``').replace(/"/g, '`"').replace(/\$/g, '`$');
        exec(`powershell -NoProfile -Command "Set-Clipboard -Value \\"${escaped}\\""`, () => {});
    }

    stop() {
        if (this.pollingInterval) clearInterval(this.pollingInterval);
    }
}

module.exports = new ClipboardManager();
