// ButterFlow Main UI Application Controller

class ButterFlowApp {
    constructor() {
        this.ws = null;
        this.appInfo = {};
        this.pairedDevices = {};
        this.discoveredPeers = [];
        this.selectedDeviceForPerms = null;
        this.pinTimer = null;

        this.init();
    }

    async init() {
        this.setupNavigation();
        this.setupTheme();
        this.setupModals();
        this.setupEmergencyRelease();
        this.setupSettings();

        await this.loadAppInfo();
        this.connectWebSocket();

        // Refresh discovered peers periodically
        setInterval(() => this.loadDiscoveredPeers(), 3000);
    }

    setupNavigation() {
        const navItems = document.querySelectorAll('.nav-item');
        navItems.forEach(item => {
            item.addEventListener('click', () => {
                const tab = item.getAttribute('data-tab');
                navItems.forEach(i => i.classList.remove('active'));
                item.classList.add('active');

                document.querySelectorAll('.tab-pane').forEach(pane => {
                    pane.classList.remove('active');
                });
                const activePane = document.getElementById(`pane-${tab}`);
                if (activePane) activePane.classList.add('active');

                if (tab === 'layout') {
                    window.LayoutEditor.render();
                } else if (tab === 'security') {
                    this.loadSecurityLogs();
                }
            });
        });

        // Save Layout Button
        const btnSaveLayout = document.getElementById('btnSaveLayout');
        if (btnSaveLayout) {
            btnSaveLayout.addEventListener('click', async () => {
                const layout = window.LayoutEditor.getLayout();
                await fetch('/api/layout', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(layout)
                });
                alert('Screen arrangement saved successfully!');
            });
        }
    }

    setupTheme() {
        const btn = document.getElementById('themeToggle');
        const icon = document.getElementById('themeIcon');
        const currentTheme = localStorage.getItem('butterflow-theme') || 'dark';
        document.documentElement.setAttribute('data-theme', currentTheme);
        if (icon) icon.textContent = currentTheme === 'dark' ? '☀️' : '🌙';

        if (btn) {
            btn.addEventListener('click', () => {
                const current = document.documentElement.getAttribute('data-theme');
                const next = current === 'dark' ? 'light' : 'dark';
                document.documentElement.setAttribute('data-theme', next);
                localStorage.setItem('butterflow-theme', next);
                if (icon) icon.textContent = next === 'dark' ? '☀️' : '🌙';
            });
        }
    }

    setupEmergencyRelease() {
        const btn = document.getElementById('btnEmergencyRelease');
        if (btn) {
            btn.addEventListener('click', async () => {
                await fetch('/api/input/emergency-release', { method: 'POST' });
                this.updateInputFocus('local', 'This PC (Local)');
            });
        }

        // Global hotkey backup listener in browser: Ctrl+Alt+Escape
        window.addEventListener('keydown', (e) => {
            if (e.ctrlKey && e.altKey && (e.key === 'Escape' || e.code === 'Pause')) {
                fetch('/api/input/emergency-release', { method: 'POST' });
                this.updateInputFocus('local', 'This PC (Local)');
            }
        });
    }

    setupModals() {
        // Show My Code & IP
        const btnShowMyCode = document.getElementById('btnShowMyCode');
        const btnCopyIp = document.getElementById('btnCopyMyIp');

        if (btnCopyIp) {
            btnCopyIp.addEventListener('click', () => {
                const ipText = document.getElementById('myPairingIp').textContent;
                navigator.clipboard.writeText(ipText);
                const originalText = btnCopyIp.textContent;
                btnCopyIp.textContent = '✓ Copied!';
                btnCopyIp.style.color = 'var(--accent-green)';
                setTimeout(() => {
                    btnCopyIp.textContent = originalText;
                    btnCopyIp.style.color = '';
                }, 1800);
            });
        }

        if (btnShowMyCode) {
            btnShowMyCode.addEventListener('click', async () => {
                const res = await fetch('/api/pair/generate-code', { method: 'POST' });
                const data = await res.json();
                
                // Set PIN and IP
                document.getElementById('myPairingPin').textContent = data.code;
                const ipEl = document.getElementById('myPairingIp');
                if (ipEl) ipEl.textContent = data.ip;

                // Alternate interface pills
                const pillsContainer = document.getElementById('ipInterfacePills');
                if (pillsContainer) {
                    pillsContainer.innerHTML = '';
                    if (data.allIps && data.allIps.length > 1) {
                        pillsContainer.innerHTML = '<span style="margin-right:4px;">Available IPs:</span>';
                        data.allIps.forEach(ip => {
                            const pill = document.createElement('span');
                            pill.className = 'ip-pill';
                            pill.textContent = ip;
                            pill.title = 'Click to select this IP';
                            pill.onclick = () => {
                                ipEl.textContent = ip;
                            };
                            pillsContainer.appendChild(pill);
                        });
                    }
                }

                openModal('modalMyCode');

                let left = 60;
                const countdown = document.getElementById('pinCountdown');
                if (countdown) countdown.textContent = left;

                if (this.pinTimer) clearInterval(this.pinTimer);
                this.pinTimer = setInterval(() => {
                    left--;
                    if (countdown) countdown.textContent = left;
                    if (left <= 0) {
                        clearInterval(this.pinTimer);
                        document.getElementById('myPairingPin').textContent = 'EXPIRED';
                    }
                }, 1000);
            });
        }

        // Pair Remote PC Modal
        const btnPairNew = document.getElementById('btnPairNew');
        const quickGroup = document.getElementById('quickPickGroup');
        const quickSelect = document.getElementById('quickPickPeerSelect');

        if (btnPairNew) {
            btnPairNew.addEventListener('click', () => {
                document.getElementById('pairPeerIp').value = '';
                document.getElementById('pairPeerPin').value = '';
                document.getElementById('pairStatusMessage').textContent = '';

                // Populate discovered quick-pick dropdown if any
                if (quickSelect && quickGroup) {
                    if (this.discoveredPeers && this.discoveredPeers.length > 0) {
                        quickGroup.style.display = 'block';
                        quickSelect.innerHTML = '<option value="">-- Select discovered PC to auto-fill IP --</option>';
                        this.discoveredPeers.forEach(p => {
                            const opt = document.createElement('option');
                            opt.value = p.ip;
                            opt.textContent = `${p.deviceName} (${p.ip})`;
                            quickSelect.appendChild(opt);
                        });
                        quickSelect.onchange = (e) => {
                            if (e.target.value) {
                                document.getElementById('pairPeerIp').value = e.target.value;
                            }
                        };
                    } else {
                        quickGroup.style.display = 'none';
                    }
                }

                openModal('modalPairNew');
            });
        }

        const btnSubmitPair = document.getElementById('btnSubmitPair');
        if (btnSubmitPair) {
            btnSubmitPair.addEventListener('click', async () => {
                const ip = document.getElementById('pairPeerIp').value.trim();
                const pin = document.getElementById('pairPeerPin').value.trim();
                const statusDiv = document.getElementById('pairStatusMessage');

                if (!ip || !pin) {
                    statusDiv.textContent = 'Please enter both IP and 6-digit PIN.';
                    statusDiv.style.color = 'var(--accent-red)';
                    return;
                }

                statusDiv.textContent = 'Connecting and verifying PIN...';
                statusDiv.style.color = 'var(--accent-amber)';

                try {
                    const res = await fetch('/api/pair/request', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ peerIp: ip, code: pin })
                    });
                    const data = await res.json();
                    if (data.success) {
                        statusDiv.textContent = `Paired successfully with ${data.deviceName}!`;
                        statusDiv.style.color = 'var(--accent-green)';
                        setTimeout(() => {
                            closeModal('modalPairNew');
                            this.loadAppInfo();
                        }, 1200);
                    } else {
                        statusDiv.textContent = data.error || 'Pairing failed';
                        statusDiv.style.color = 'var(--accent-red)';
                    }
                } catch (e) {
                    statusDiv.textContent = 'Connection error: ' + e.message;
                    statusDiv.style.color = 'var(--accent-red)';
                }
            });
        }

        // Save Permissions
        const btnSavePerms = document.getElementById('btnSavePermissions');
        if (btnSavePerms) {
            btnSavePerms.addEventListener('click', async () => {
                if (!this.selectedDeviceForPerms) return;
                const permissions = {
                    input: document.getElementById('permInput').checked,
                    clipboard: document.getElementById('permClipboard').checked,
                    fileTransfer: document.getElementById('permFile').checked,
                    screenShare: document.getElementById('permScreen').checked,
                    remoteControl: document.getElementById('permRemoteControl').checked
                };

                await fetch(`/api/devices/${this.selectedDeviceForPerms}/permissions`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(permissions)
                });

                closeModal('modalPermissions');
                this.loadAppInfo();
            });
        }

        // Screen share controls
        const btnStartView = document.getElementById('btnStartScreenView');
        const btnStopView = document.getElementById('btnStopScreenView');
        const peerSelect = document.getElementById('screenPeerSelect');

        if (btnStartView) {
            btnStartView.addEventListener('click', () => {
                const peerId = peerSelect ? peerSelect.value : null;
                if (!peerId) {
                    alert('Please select a PC to view.');
                    return;
                }
                window.RemoteViewer.start(peerId);
                btnStartView.style.display = 'none';
                if (btnStopView) btnStopView.style.display = 'inline-flex';
            });
        }

        if (btnStopView) {
            btnStopView.addEventListener('click', () => {
                window.RemoteViewer.stop();
                btnStopView.style.display = 'none';
                if (btnStartView) btnStartView.style.display = 'inline-flex';
            });
        }
    }

    setupSettings() {
        const btn = document.getElementById('btnSaveSettings');
        if (btn) {
            btn.addEventListener('click', async () => {
                const threshold = parseInt(document.getElementById('settingThreshold').value, 10);
                const downloadDir = document.getElementById('settingDownloadDir').value;

                await fetch('/api/settings', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ edgeThreshold: threshold, downloadDir })
                });

                alert('Settings saved successfully!');
            });
        }

        const chkEdge = document.getElementById('chkEdgeSwitch');
        if (chkEdge) {
            chkEdge.addEventListener('change', async (e) => {
                await fetch('/api/settings', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ edgeSwitchEnabled: e.target.checked })
                });
            });
        }
    }

    async loadAppInfo() {
        try {
            const res = await fetch('/api/info');
            const data = await res.json();
            this.appInfo = data;
            this.pairedDevices = data.pairedDevices || {};

            const myName = document.getElementById('myDeviceName');
            if (myName) myName.textContent = data.deviceName;

            const myHeaderIp = document.getElementById('myHeaderIp');
            if (myHeaderIp && data.ip) myHeaderIp.textContent = `(${data.ip})`;

            const settingName = document.getElementById('settingDeviceName');
            if (settingName) settingName.value = data.deviceName;

            const settingThresh = document.getElementById('settingThreshold');
            if (settingThresh) settingThresh.value = data.edgeThreshold || 2;

            const settingDl = document.getElementById('settingDownloadDir');
            if (settingDl) settingDl.value = data.downloadDir || '';

            const chkEdge = document.getElementById('chkEdgeSwitch');
            if (chkEdge) chkEdge.checked = data.edgeSwitchEnabled !== false;

            // Stats
            const count = Object.keys(this.pairedDevices).length;
            const statPaired = document.getElementById('statPairedCount');
            if (statPaired) statPaired.textContent = `${count} PC${count === 1 ? '' : 's'}`;

            // Initialize layout editor
            window.LayoutEditor.init(data.screenLayout, this.pairedDevices);

            // Update selectors
            window.FileTransferUI.updatePeerSelect(this.pairedDevices);
            this.updateScreenPeerSelect();

            this.renderPairedDevices();
            this.loadDiscoveredPeers();
        } catch (err) {
            console.error('Failed to load app info:', err);
        }
    }

    updateScreenPeerSelect() {
        const select = document.getElementById('screenPeerSelect');
        if (!select) return;
        const current = select.value;
        select.innerHTML = '<option value="">Select PC to view...</option>';
        for (const [id, dev] of Object.entries(this.pairedDevices)) {
            const opt = document.createElement('option');
            opt.value = id;
            opt.textContent = `${dev.deviceName} (${dev.ip})`;
            select.appendChild(opt);
        }
        if (current) select.value = current;
    }

    renderPairedDevices() {
        const grid = document.getElementById('pairedDevicesGrid');
        if (!grid) return;

        const entries = Object.entries(this.pairedDevices);
        if (entries.length === 0) {
            grid.innerHTML = '<div class="empty-state" style="grid-column: 1/-1;">No paired computers yet. Click "Pair Remote PC" or enter PIN on another device.</div>';
            return;
        }

        grid.innerHTML = '';
        entries.forEach(([id, dev]) => {
            const card = document.createElement('div');
            card.className = 'device-card';
            card.innerHTML = `
                <div class="device-card-header">
                    <div class="device-card-title">
                        <span class="device-os-icon">💻</span>
                        <div>
                            <div class="device-name">${dev.deviceName}</div>
                            <div class="device-ip">${dev.ip}:${dev.port || 38440}</div>
                        </div>
                    </div>
                    <span class="device-status-badge connected">Paired</span>
                </div>
                <div class="device-metrics-row">
                    <span>Latency: <strong>&lt; 1 ms</strong></span>
                    <span>KVM: <strong>Active</strong></span>
                    <span>Clipboard: <strong>Sync</strong></span>
                </div>
                <div class="device-actions">
                    <button class="btn-sm" onclick="window.App.openPermissionsModal('${id}')">Permissions</button>
                    <button class="btn-sm" onclick="window.App.unpairDevice('${id}')" style="color:var(--accent-red);">Unpair</button>
                </div>
            `;
            grid.appendChild(card);
        });
    }

    async loadDiscoveredPeers() {
        try {
            const res = await fetch('/api/peers');
            const peers = await res.json();
            this.discoveredPeers = peers;

            const statDisc = document.getElementById('statDiscoveredCount');
            if (statDisc) statDisc.textContent = `${peers.length} Discovered on LAN`;

            this.renderDiscoveredPeers();
        } catch (e) {}
    }

    renderDiscoveredPeers() {
        const list = document.getElementById('discoveredList');
        if (!list) return;

        if (this.discoveredPeers.length === 0) {
            list.innerHTML = '<div class="empty-state">Searching for other ButterFlow computers on local network...</div>';
            return;
        }

        list.innerHTML = '';
        this.discoveredPeers.forEach(peer => {
            const row = document.createElement('div');
            row.className = 'discovery-row';
            row.innerHTML = `
                <div class="discovery-info">
                    <span class="pill-dot online"></span>
                    <div>
                        <strong>${peer.deviceName}</strong>
                        <div style="font-size:11px; color:var(--text-muted); font-family:var(--font-mono);">${peer.ip}:${peer.port} · ${peer.os} (${peer.screenWidth}×${peer.screenHeight})</div>
                    </div>
                </div>
                <div>
                    ${peer.isPaired 
                        ? '<span class="device-status-badge connected">Paired</span>'
                        : `<button class="btn-sm" onclick="window.App.quickPair('${peer.ip}')">Pair With PIN</button>`}
                </div>
            `;
            list.appendChild(row);
        });
    }

    quickPair(ip) {
        document.getElementById('pairPeerIp').value = ip;
        openModal('modalPairNew');
    }

    openPermissionsModal(deviceId) {
        const dev = this.pairedDevices[deviceId];
        if (!dev) return;
        this.selectedDeviceForPerms = deviceId;
        document.getElementById('permDeviceName').textContent = dev.deviceName;

        const p = dev.permissions || {};
        document.getElementById('permInput').checked = p.input !== false;
        document.getElementById('permClipboard').checked = p.clipboard !== false;
        document.getElementById('permFile').checked = p.fileTransfer !== false;
        document.getElementById('permScreen').checked = p.screenShare !== false;
        document.getElementById('permRemoteControl').checked = p.remoteControl !== false;

        openModal('modalPermissions');
    }

    async unpairDevice(deviceId) {
        if (!confirm('Are you sure you want to unpair this computer?')) return;
        await fetch(`/api/devices/${deviceId}`, { method: 'DELETE' });
        this.loadAppInfo();
    }

    async loadSecurityLogs() {
        const container = document.getElementById('securityLogContainer');
        if (!container) return;
        try {
            const res = await fetch('/api/logs');
            const logs = await res.json();
            if (logs.length === 0) {
                container.innerHTML = '<div class="empty-state">No audit logs recorded yet</div>';
                return;
            }
            container.innerHTML = '';
            logs.forEach(l => {
                const div = document.createElement('div');
                div.className = 'log-entry';
                div.innerHTML = `
                    <span class="log-time">${new Date(l.timestamp).toLocaleTimeString()}</span>
                    <span class="log-action" style="${l.level === 'warn' ? 'color:var(--accent-amber);' : (l.level === 'error' ? 'color:var(--accent-red);' : '')}">${l.action}</span>
                `;
                container.appendChild(div);
            });
        } catch (e) {}
    }

    connectWebSocket() {
        const proto = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
        const url = `${proto}//${window.location.host}`;

        try {
            this.ws = new WebSocket(url);

            this.ws.onmessage = (event) => {
                try {
                    const data = JSON.parse(event.data);
                    this.handleWsEvent(data);
                } catch (e) {}
            };

            this.ws.onclose = () => {
                setTimeout(() => this.connectWebSocket(), 2000);
            };
        } catch (e) {}
    }

    sendWs(obj) {
        if (this.ws && this.ws.readyState === WebSocket.OPEN) {
            this.ws.send(JSON.stringify(obj));
        }
    }

    handleWsEvent(data) {
        if (data.type === 'UI_EVENT') {
            const { event, data: payload } = data;

            if (event === 'INPUT_FOCUS_CHANGED') {
                this.updateInputFocus(payload.target, payload.name);
                window.LayoutEditor.highlightActiveFocus(payload.target);
            } else if (event.startsWith('TRANSFER_')) {
                window.FileTransferUI.handleTransferProgress(payload);
            } else if (event === 'REMOTE_SCREEN_FRAME') {
                window.RemoteViewer.handleFrame(payload);
            } else if (event === 'CLIPBOARD_UPDATED') {
                // Subtle toast
                console.log('Clipboard synchronized from:', payload.from);
            }
        }
    }

    updateInputFocus(targetId, targetName) {
        const nameEl = document.getElementById('focusTargetName');
        const btnEmergency = document.getElementById('btnEmergencyRelease');
        if (nameEl) nameEl.textContent = targetName || (targetId === 'local' ? 'This PC (Local)' : 'Remote PC');
        if (btnEmergency) {
            btnEmergency.style.display = targetId === 'local' ? 'none' : 'inline-block';
        }
    }
}

// Global modal helpers
function openModal(id) {
    const el = document.getElementById(id);
    if (el) el.style.display = 'flex';
}

function closeModal(id) {
    const el = document.getElementById(id);
    if (el) el.style.display = 'none';
}

window.addEventListener('DOMContentLoaded', () => {
    window.App = new ButterFlowApp();
});
