const path = require('path');
const config = require('./config');
const nativeInput = require('./nativeInput');
const discovery = require('./network/discovery');
const server = require('./network/server');
const client = require('./network/client');
const inputManager = require('./services/inputManager');
const clipboardManager = require('./services/clipboardManager');
const fileTransferManager = require('./services/fileTransferManager');
const screenStreamManager = require('./services/screenStreamManager');
const benchmarkEngine = require('./benchmark/benchmarkEngine');

// Check if running in Electron environment
let electron = null;
try {
    electron = require('electron');
} catch (e) {
    // running in pure Node.js
}

const isElectron = electron && electron.app && typeof electron.app.whenReady === 'function';

async function bootstrap() {
    console.log('====================================================');
    console.log('            BUTTERFLOW DESKTOP KVM ENGINE           ');
    console.log('====================================================');
    console.log(`Device ID:   ${config.get('deviceId')}`);
    console.log(`Device Name: ${config.get('deviceName')}`);
    console.log(`Port:        ${config.get('port')}`);
    console.log(`Mode:        ${isElectron ? 'Electron Desktop' : 'Node Native Desktop'}`);
    console.log('----------------------------------------------------');

    // 1. Dependency Injection across services
    const services = {
        config,
        nativeInput,
        discovery,
        server,
        client,
        inputManager,
        clipboardManager,
        fileTransferManager,
        screenStreamManager,
        benchmarkEngine
    };

    server.setServices(services);
    client.setServices(services);
    inputManager.setServices(services);
    clipboardManager.setServices(services);
    fileTransferManager.setServices(services);
    screenStreamManager.setServices(services);
    benchmarkEngine.setServices(services);

    // 2. Start Core Services
    server.start();
    discovery.start();
    client.start();
    clipboardManager.start();

    // 3. Start Native Hook
    nativeInput.start();
    nativeInput.setBounds(
        config.get('screenLayout').local.width || 1920,
        config.get('screenLayout').local.height || 1080,
        config.get('edgeThreshold', 2),
        config.get('edgeSwitchEnabled', true)
    );

    // 4. Electron Desktop Window (if running inside Electron)
    if (isElectron) {
        setupElectron(services);
    } else {
        console.log(`[ButterFlow] Running standalone. UI accessible at http://localhost:${config.get('port')}`);
    }
}

function setupElectron(services) {
    const { app, BrowserWindow, clipboard } = electron;
    clipboardManager.setElectronClipboard(clipboard);

    const gotTheLock = app.requestSingleInstanceLock();
    if (!gotTheLock) {
        console.log('[ButterFlow] Another instance is already running. Focusing existing window and exiting.');
        app.quit();
        return;
    }

    let mainWindow = null;

    app.on('second-instance', () => {
        if (mainWindow) {
            if (mainWindow.isMinimized()) mainWindow.restore();
            mainWindow.focus();
        }
    });

    app.whenReady().then(() => {
        mainWindow = new BrowserWindow({
            width: 1240,
            height: 820,
            minWidth: 980,
            minHeight: 680,
            frame: true,
            title: 'ButterFlow',
            backgroundColor: '#0f1117',
            webPreferences: {
                nodeIntegration: false,
                contextIsolation: true,
                webSecurity: false
            }
        });

        const activePort = server.port || config.get('port', 38440);
        mainWindow.loadURL(`http://localhost:${activePort}`);

        mainWindow.on('closed', () => {
            mainWindow = null;
        });
    });

    app.on('window-all-closed', () => {
        if (process.platform !== 'darwin') {
            app.quit();
        }
    });

    app.on('before-quit', () => {
        shutdown();
    });
}

function shutdown() {
    console.log('[ButterFlow] Shutting down services...');
    nativeInput.stop();
    discovery.stop();
    client.stop();
    clipboardManager.stop();
    screenStreamManager.stop();
    server.stop();
}

process.on('SIGINT', () => {
    shutdown();
    process.exit(0);
});

process.on('SIGTERM', () => {
    shutdown();
    process.exit(0);
});

process.on('uncaughtException', (err) => {
    console.error('[ButterFlow] Uncaught exception:', err.message);
});

process.on('unhandledRejection', (reason) => {
    console.error('[ButterFlow] Unhandled rejection:', reason);
});

bootstrap();

module.exports = {
    bootstrap,
    shutdown
};
