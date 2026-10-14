// ButterFlow File Transfer UI Controller

class FileTransferUI {
    constructor() {
        this.dropZone = document.getElementById('fileDropZone');
        this.fileInput = document.getElementById('fileInput');
        this.targetSelect = document.getElementById('dropTargetPeerSelect');
        this.activeList = document.getElementById('activeTransfersList');
        this.historyTableBody = document.querySelector('#transferHistoryTable tbody');
        this.badgeCount = document.getElementById('activeTransferCount');

        this.transfers = new Map();
        this.history = [];

        this.setupDropZone();
    }

    setupDropZone() {
        if (!this.dropZone) return;

        ['dragenter', 'dragover'].forEach(name => {
            this.dropZone.addEventListener(name, (e) => {
                e.preventDefault();
                this.dropZone.classList.add('dragover');
            });
        });

        ['dragleave', 'drop'].forEach(name => {
            this.dropZone.addEventListener(name, (e) => {
                e.preventDefault();
                this.dropZone.classList.remove('dragover');
            });
        });

        this.dropZone.addEventListener('drop', (e) => {
            const files = e.dataTransfer.files;
            if (files && files.length > 0) {
                this.handleSelectedFiles(files);
            }
        });

        if (this.fileInput) {
            this.fileInput.addEventListener('change', (e) => {
                if (e.target.files && e.target.files.length > 0) {
                    this.handleSelectedFiles(e.target.files);
                }
            });
        }
    }

    updatePeerSelect(pairedDevices) {
        if (!this.targetSelect) return;
        const currentVal = this.targetSelect.value;
        this.targetSelect.innerHTML = '<option value="">Select Target PC...</option>';

        for (const [id, dev] of Object.entries(pairedDevices || {})) {
            const opt = document.createElement('option');
            opt.value = id;
            opt.textContent = `${dev.deviceName} (${dev.ip})`;
            this.targetSelect.appendChild(opt);
        }

        if (currentVal) this.targetSelect.value = currentVal;
    }

    handleSelectedFiles(files) {
        const targetPeerId = this.targetSelect ? this.targetSelect.value : null;
        if (!targetPeerId) {
            alert('Please select a target PC to send files to.');
            return;
        }

        for (let i = 0; i < files.length; i++) {
            const file = files[i];
            this.sendFileOverSocket(targetPeerId, file);
        }
    }

    async sendFileOverSocket(peerId, file) {
        const CHUNK_SIZE = 64 * 1024;
        const totalChunks = Math.ceil(file.size / CHUNK_SIZE) || 1;
        const transferId = 'tx_' + Math.random().toString(36).substring(2, 9);

        // Read and calculate SHA-256 in browser
        const arrayBuffer = await file.arrayBuffer();
        const hashBuffer = await crypto.subtle.digest('SHA-256', arrayBuffer);
        const hashArray = Array.from(new Uint8Array(hashBuffer));
        const hashHex = hashArray.map(b => b.toString(16).padStart(2, '0')).join('');

        const transfer = {
            id: transferId,
            filename: file.name,
            totalSize: file.size,
            progress: 0,
            speed: '0',
            status: 'transferring',
            direction: 'outbound'
        };

        this.handleTransferProgress(transfer);

        // Send FILE_START
        window.App.sendWs({
            t: 'FILE_START',
            transferId,
            name: file.name,
            size: file.size,
            totalChunks,
            hash: hashHex
        });

        // Send chunks
        let offset = 0;
        let chunkIndex = 0;
        const startTime = Date.now();

        const sendNextChunk = () => {
            if (offset >= file.size) {
                // FILE_FINISH
                window.App.sendWs({
                    t: 'FILE_FINISH',
                    transferId,
                    hash: hashHex
                });
                transfer.status = 'completed';
                transfer.progress = 100;
                this.handleTransferProgress(transfer);
                return;
            }

            const chunk = arrayBuffer.slice(offset, offset + CHUNK_SIZE);
            const base64Data = this.arrayBufferToBase64(chunk);

            window.App.sendWs({
                t: 'FILE_CHUNK',
                transferId,
                chunkIndex,
                data: base64Data
            });

            offset += chunk.byteLength;
            chunkIndex++;

            const elapsedSec = (Date.now() - startTime) / 1000;
            transfer.progress = Math.min(100, Math.round((offset / file.size) * 100));
            transfer.speed = elapsedSec > 0 ? (offset / elapsedSec / (1024 * 1024)).toFixed(2) : '0';

            this.handleTransferProgress(transfer);
            setTimeout(sendNextChunk, 5); // avoid browser queue block
        };

        sendNextChunk();
    }

    arrayBufferToBase64(buffer) {
        let binary = '';
        const bytes = new Uint8Array(buffer);
        const len = bytes.byteLength;
        for (let i = 0; i < len; i++) {
            binary += String.fromCharCode(bytes[i]);
        }
        return window.btoa(binary);
    }

    handleTransferProgress(transfer) {
        this.transfers.set(transfer.id, transfer);
        this.renderActiveTransfers();

        if (transfer.status === 'completed' || transfer.status === 'failed') {
            this.addToHistory(transfer);
            setTimeout(() => {
                this.transfers.delete(transfer.id);
                this.renderActiveTransfers();
            }, 3000);
        }
    }

    renderActiveTransfers() {
        if (!this.activeList) return;
        const active = Array.from(this.transfers.values()).filter(t => t.status === 'transferring');

        if (this.badgeCount) {
            if (active.length > 0) {
                this.badgeCount.textContent = active.length;
                this.badgeCount.style.display = 'inline-block';
            } else {
                this.badgeCount.style.display = 'none';
            }
        }

        if (this.transfers.size === 0) {
            this.activeList.innerHTML = '<div class="empty-state">No files currently transferring</div>';
            return;
        }

        this.activeList.innerHTML = '';
        this.transfers.forEach(t => {
            const card = document.createElement('div');
            card.className = 'transfer-card';
            card.style.cssText = 'background:var(--bg-sidebar); border:1px solid var(--border-color); border-radius:8px; padding:12px; margin-bottom:8px;';
            card.innerHTML = `
                <div style="display:flex; justify-content:space-between; margin-bottom:6px;">
                    <strong>${t.filename}</strong>
                    <span>${t.direction === 'inbound' ? '📥 Receiving' : '📤 Sending'} · ${t.speed} MB/s (${t.progress}%)</span>
                </div>
                <div style="width:100%; height:6px; background:var(--border-color); border-radius:3px; overflow:hidden;">
                    <div style="width:${t.progress}%; height:100%; background:var(--accent-amber); transition:width 0.2s;"></div>
                </div>
            `;
            this.activeList.appendChild(card);
        });
    }

    addToHistory(transfer) {
        if (!this.historyTableBody) return;
        const tr = document.createElement('tr');
        tr.innerHTML = `
            <td><strong>${transfer.filename}</strong></td>
            <td>${transfer.direction === 'inbound' ? '📥 Inbound' : '📤 Outbound'}</td>
            <td>${(transfer.totalSize / (1024 * 1024)).toFixed(2)} MB</td>
            <td>${transfer.speed} MB/s</td>
            <td><span style="color:${transfer.status === 'completed' ? 'var(--accent-green)' : 'var(--accent-red)'}">${transfer.status}</span></td>
            <td>${new Date().toLocaleTimeString()}</td>
        `;
        this.historyTableBody.insertBefore(tr, this.historyTableBody.firstChild);
    }
}

window.FileTransferUI = new FileTransferUI();
