const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const config = require('../config');
const security = require('../network/security');
const { MessageTypes } = require('../network/protocol');

const CHUNK_SIZE = 64 * 1024; // 64 KB per chunk

class FileTransferManager {
    constructor() {
        this.services = {};
        this.inboundTransfers = new Map();  // transferId -> state
        this.outboundTransfers = new Map(); // transferId -> state
        this.transferHistory = [];
        this.queue = [];
        this.activeOutbound = null;
    }

    setServices(services) {
        this.services = services;
    }

    async sendFile(peerId, filePath, options = {}) {
        if (!fs.existsSync(filePath)) {
            throw new Error(`File does not exist: ${filePath}`);
        }

        const stats = fs.statSync(filePath);
        if (stats.isDirectory()) {
            return this.sendDirectory(peerId, filePath, options);
        }

        const transferId = crypto.randomUUID ? crypto.randomUUID() : crypto.randomBytes(16).toString('hex');
        const filename = path.basename(filePath);
        const totalSize = stats.size;
        const totalChunks = Math.ceil(totalSize / CHUNK_SIZE) || 1;

        console.log(`[FileTransfer] Calculating SHA-256 for ${filename}...`);
        const fileHash = await security.calculateFileHash(filePath);

        const transfer = {
            id: transferId,
            peerId,
            filename,
            filePath,
            totalSize,
            sentBytes: 0,
            totalChunks,
            currentChunk: 0,
            hash: fileHash,
            status: 'queued',
            startTime: 0,
            speed: 0,
            progress: 0,
            direction: 'outbound'
        };

        this.outboundTransfers.set(transferId, transfer);
        this.queue.push(transfer);
        this.notifyUI('TRANSFER_QUEUED', transfer);

        this.processQueue();
        return transferId;
    }

    async sendDirectory(peerId, dirPath, options = {}) {
        const entries = fs.readdirSync(dirPath);
        const results = [];
        for (const entry of entries) {
            const fullPath = path.join(dirPath, entry);
            const id = await this.sendFile(peerId, fullPath, options);
            results.push(id);
        }
        return results;
    }

    async processQueue() {
        if (this.activeOutbound) return;
        if (this.queue.length === 0) return;

        const transfer = this.queue.shift();
        this.activeOutbound = transfer;
        transfer.status = 'transferring';
        transfer.startTime = Date.now();

        console.log(`[FileTransfer] Starting outbound transfer ${transfer.filename} (${transfer.totalSize} bytes)`);

        // Send FILE_START
        const startPacket = {
            t: MessageTypes.FILE_START,
            transferId: transfer.id,
            name: transfer.filename,
            size: transfer.totalSize,
            totalChunks: transfer.totalChunks,
            hash: transfer.hash,
            conflictStrategy: 'rename'
        };

        this.services.client.send(transfer.peerId, startPacket);

        // Stream chunks using read stream
        const stream = fs.createReadStream(transfer.filePath, { highWaterMark: CHUNK_SIZE });
        let chunkIndex = 0;

        stream.on('data', (chunk) => {
            transfer.sentBytes += chunk.length;
            transfer.currentChunk = chunkIndex;
            transfer.progress = Math.min(100, Math.round((transfer.sentBytes / transfer.totalSize) * 100));

            const elapsedSec = (Date.now() - transfer.startTime) / 1000;
            transfer.speed = elapsedSec > 0 ? (transfer.sentBytes / elapsedSec / (1024 * 1024)) : 0; // MB/s

            const chunkPacket = {
                t: MessageTypes.FILE_CHUNK,
                transferId: transfer.id,
                chunkIndex,
                data: chunk.toString('base64')
            };

            this.services.client.send(transfer.peerId, chunkPacket);
            chunkIndex++;

            if (chunkIndex % 5 === 0 || transfer.progress === 100) {
                this.notifyUI('TRANSFER_PROGRESS', transfer);
            }
        });

        stream.on('end', () => {
            const finishPacket = {
                t: MessageTypes.FILE_FINISH,
                transferId: transfer.id,
                hash: transfer.hash
            };
            this.services.client.send(transfer.peerId, finishPacket);

            transfer.status = 'completed';
            transfer.progress = 100;
            this.addToHistory(transfer);
            this.notifyUI('TRANSFER_COMPLETED', transfer);
            console.log(`[FileTransfer] Outbound transfer completed: ${transfer.filename}`);

            this.activeOutbound = null;
            this.processQueue();
        });

        stream.on('error', (err) => {
            console.error(`[FileTransfer] Outbound transfer error:`, err);
            transfer.status = 'failed';
            transfer.error = err.message;
            this.addToHistory(transfer);
            this.notifyUI('TRANSFER_FAILED', transfer);

            this.activeOutbound = null;
            this.processQueue();
        });
    }

    handleFileMessage(peerId, msg, ws) {
        switch (msg.t) {
            case MessageTypes.FILE_START:
                this.handleFileStart(peerId, msg);
                break;
            case MessageTypes.FILE_CHUNK:
                this.handleFileChunk(peerId, msg);
                break;
            case MessageTypes.FILE_FINISH:
                this.handleFileFinish(peerId, msg);
                break;
            case MessageTypes.FILE_CANCEL:
                this.handleFileCancel(peerId, msg);
                break;
        }
    }

    handleFileStart(peerId, msg) {
        const paired = config.getPairedDevice(peerId);
        if (paired && paired.permissions && paired.permissions.fileTransfer === false) {
            console.warn(`[FileTransfer] Rejected file transfer from ${peerId}: permission denied`);
            return;
        }

        const downloadDir = config.get('downloadDir');
        if (!fs.existsSync(downloadDir)) {
            fs.mkdirSync(downloadDir, { recursive: true });
        }

        const safeFilename = security.sanitizeFilename(msg.name);
        const conflictRes = security.resolveConflict(downloadDir, safeFilename, msg.conflictStrategy || 'rename');

        if (conflictRes.action === 'skip') {
            console.log(`[FileTransfer] File ${safeFilename} skipped due to conflict rule`);
            return;
        }

        const finalPath = conflictRes.path;
        const tempPath = finalPath + '.butterflow.tmp';

        const writeStream = fs.createWriteStream(tempPath);
        const hashStream = crypto.createHash('sha256');

        const transfer = {
            id: msg.transferId,
            peerId,
            filename: conflictRes.filename,
            finalPath,
            tempPath,
            totalSize: msg.size,
            receivedBytes: 0,
            expectedHash: msg.hash,
            totalChunks: msg.totalChunks,
            writeStream,
            hashStream,
            startTime: Date.now(),
            speed: 0,
            progress: 0,
            status: 'transferring',
            direction: 'inbound'
        };

        this.inboundTransfers.set(msg.transferId, transfer);
        this.notifyUI('TRANSFER_STARTED', transfer);
        config.addLog(`Receiving file "${transfer.filename}" (${(msg.size / (1024*1024)).toFixed(2)} MB) from ${paired ? paired.deviceName : peerId}`);
    }

    handleFileChunk(peerId, msg) {
        const transfer = this.inboundTransfers.get(msg.transferId);
        if (!transfer || transfer.status !== 'transferring') return;

        const buffer = Buffer.from(msg.data, 'base64');
        transfer.writeStream.write(buffer);
        transfer.hashStream.update(buffer);

        transfer.receivedBytes += buffer.length;
        transfer.progress = Math.min(100, Math.round((transfer.receivedBytes / transfer.totalSize) * 100));

        const elapsedSec = (Date.now() - transfer.startTime) / 1000;
        transfer.speed = elapsedSec > 0 ? (transfer.receivedBytes / elapsedSec / (1024 * 1024)) : 0;

        if (msg.chunkIndex % 5 === 0 || transfer.progress === 100) {
            this.notifyUI('TRANSFER_PROGRESS', transfer);
        }
    }

    handleFileFinish(peerId, msg) {
        const transfer = this.inboundTransfers.get(msg.transferId);
        if (!transfer) return;

        transfer.writeStream.end(() => {
            const calculatedHash = transfer.hashStream.digest('hex');

            if (calculatedHash === transfer.expectedHash) {
                // Atomic rename from .tmp to final path
                try {
                    if (fs.existsSync(transfer.finalPath)) {
                        fs.unlinkSync(transfer.finalPath);
                    }
                    fs.renameSync(transfer.tempPath, transfer.finalPath);
                    transfer.status = 'completed';
                    transfer.progress = 100;
                    console.log(`[FileTransfer] Successfully verified & saved: ${transfer.finalPath}`);
                    config.addLog(`File transfer complete and verified: "${transfer.filename}"`);
                    this.addToHistory(transfer);
                    this.notifyUI('TRANSFER_COMPLETED', transfer);
                } catch (err) {
                    console.error('[FileTransfer] Final rename error:', err);
                    transfer.status = 'failed';
                    transfer.error = err.message;
                    this.notifyUI('TRANSFER_FAILED', transfer);
                }
            } else {
                console.error(`[FileTransfer] Integrity check FAILED for ${transfer.filename}! Expected ${transfer.expectedHash}, got ${calculatedHash}`);
                try { fs.unlinkSync(transfer.tempPath); } catch (e) {}
                transfer.status = 'failed';
                transfer.error = 'SHA-256 integrity verification failed';
                config.addLog(`File transfer corrupted: "${transfer.filename}"`, 'error');
                this.addToHistory(transfer);
                this.notifyUI('TRANSFER_FAILED', transfer);
            }

            this.inboundTransfers.delete(msg.transferId);
        });
    }

    handleFileCancel(peerId, msg) {
        const transfer = this.inboundTransfers.get(msg.transferId);
        if (transfer) {
            try {
                transfer.writeStream.end();
                if (fs.existsSync(transfer.tempPath)) {
                    fs.unlinkSync(transfer.tempPath);
                }
            } catch (e) {}
            transfer.status = 'cancelled';
            this.addToHistory(transfer);
            this.notifyUI('TRANSFER_CANCELLED', transfer);
            this.inboundTransfers.delete(msg.transferId);
        }
    }

    addToHistory(transfer) {
        const entry = {
            id: transfer.id,
            filename: transfer.filename,
            totalSize: transfer.totalSize,
            direction: transfer.direction,
            status: transfer.status,
            completedAt: new Date().toISOString(),
            speed: transfer.speed.toFixed(2),
            error: transfer.error || null
        };
        this.transferHistory.unshift(entry);
        if (this.transferHistory.length > 50) this.transferHistory.pop();
    }

    notifyUI(event, transfer) {
        if (this.services.server) {
            this.services.server.broadcastToUI(event, {
                id: transfer.id,
                filename: transfer.filename,
                progress: transfer.progress,
                speed: transfer.speed ? transfer.speed.toFixed(2) : '0',
                status: transfer.status,
                direction: transfer.direction,
                totalSize: transfer.totalSize,
                error: transfer.error
            });
        }
    }

    getHistory() {
        return this.transferHistory;
    }
}

module.exports = new FileTransferManager();
