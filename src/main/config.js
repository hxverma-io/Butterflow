const fs = require('fs');
const path = require('path');
const os = require('os');
const crypto = require('crypto');

class ConfigManager {
    constructor() {
        this.configDir = process.env.APPDATA ? path.join(process.env.APPDATA, 'ButterFlow') : path.join(process.cwd(), '.butterflow');
        this.configFile = path.join(this.configDir, 'config.json');
        this.data = this.getDefaults();
        this.load();
    }

    getDefaults() {
        return {
            deviceId: crypto.randomUUID ? crypto.randomUUID() : crypto.randomBytes(16).toString('hex'),
            deviceName: os.hostname() || 'ButterFlow-PC',
            port: 38440,
            discoveryPort: 38445,
            theme: 'dark',
            edgeThreshold: 2,
            edgeSwitchEnabled: true,
            emergencyHotkey: 'Ctrl+Alt+Escape',
            autoReconnect: true,
            clipboardSync: true,
            incomingFileApproval: false, // auto-approve from paired devices or prompt
            downloadDir: path.join(os.homedir(), 'Downloads', 'ButterFlow'),
            pairedDevices: {},
            screenLayout: {
                local: { x: 0, y: 0, width: 1920, height: 1080, name: os.hostname() },
                peers: {}
            },
            securityLogs: []
        };
    }

    load() {
        try {
            if (fs.existsSync(this.configFile)) {
                const raw = fs.readFileSync(this.configFile, 'utf8');
                const parsed = JSON.parse(raw);
                this.data = { ...this.getDefaults(), ...parsed };
            } else {
                this.save();
            }
        } catch (err) {
            console.error('[Config] Failed to load config, using defaults:', err);
            this.data = this.getDefaults();
        }

        // Ensure download directory exists
        if (!fs.existsSync(this.data.downloadDir)) {
            try {
                fs.mkdirSync(this.data.downloadDir, { recursive: true });
            } catch (e) {
                // fallback
            }
        }
    }

    save() {
        try {
            if (!fs.existsSync(this.configDir)) {
                fs.mkdirSync(this.configDir, { recursive: true });
            }
            fs.writeFileSync(this.configFile, JSON.stringify(this.data, null, 2), 'utf8');
        } catch (err) {
            console.error('[Config] Failed to save config:', err);
        }
    }

    get(key, defaultValue) {
        return this.data[key] !== undefined ? this.data[key] : defaultValue;
    }

    set(key, value) {
        this.data[key] = value;
        this.save();
    }

    addPairedDevice(device) {
        const id = device.deviceId;
        if (!id) return;
        this.data.pairedDevices[id] = {
            deviceId: id,
            deviceName: device.deviceName || 'Remote PC',
            token: device.token || crypto.randomBytes(32).toString('hex'),
            ip: device.ip,
            port: device.port || 38440,
            pairedAt: Date.now(),
            permissions: {
                input: true,
                clipboard: true,
                fileTransfer: true,
                screenShare: true,
                remoteControl: true,
                ...(device.permissions || {})
            }
        };
        this.addLog(`Paired with device ${device.deviceName} (${id})`);
        this.save();
    }

    removePairedDevice(deviceId) {
        if (this.data.pairedDevices[deviceId]) {
            const name = this.data.pairedDevices[deviceId].deviceName;
            delete this.data.pairedDevices[deviceId];
            if (this.data.screenLayout.peers[deviceId]) {
                delete this.data.screenLayout.peers[deviceId];
            }
            this.addLog(`Unpaired device ${name} (${deviceId})`);
            this.save();
        }
    }

    isPaired(deviceId) {
        return !!this.data.pairedDevices[deviceId];
    }

    getPairedDevice(deviceId) {
        return this.data.pairedDevices[deviceId];
    }

    updatePermissions(deviceId, permissions) {
        if (this.data.pairedDevices[deviceId]) {
            this.data.pairedDevices[deviceId].permissions = {
                ...this.data.pairedDevices[deviceId].permissions,
                ...permissions
            };
            this.save();
        }
    }

    updateLayout(layout) {
        this.data.screenLayout = { ...this.data.screenLayout, ...layout };
        this.save();
    }

    addLog(action, level = 'info') {
        const entry = {
            id: crypto.randomBytes(6).toString('hex'),
            timestamp: new Date().toISOString(),
            action,
            level
        };
        this.data.securityLogs.unshift(entry);
        if (this.data.securityLogs.length > 100) {
            this.data.securityLogs = this.data.securityLogs.slice(0, 100);
        }
        this.save();
        return entry;
    }
}

module.exports = new ConfigManager();
