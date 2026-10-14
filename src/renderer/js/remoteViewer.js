// ButterFlow Remote Screen Viewer and Interactive Control

class RemoteViewer {
    constructor() {
        this.canvas = document.getElementById('remoteScreenCanvas');
        this.ctx = this.canvas ? this.canvas.getContext('2d') : null;
        this.placeholder = document.getElementById('viewerPlaceholder');
        this.toolbar = document.getElementById('viewerToolbar');
        this.fpsLabel = document.getElementById('viewerFps');
        this.resLabel = document.getElementById('viewerRes');
        this.activePeerId = null;
        this.frameCount = 0;
        this.lastFpsCheck = Date.now();
        this.remoteWidth = 1920;
        this.remoteHeight = 1080;
        this.remoteControlEnabled = false;

        this.setupEventListeners();
    }

    setupEventListeners() {
        const chk = document.getElementById('chkRemoteControl');
        if (chk) {
            chk.addEventListener('change', (e) => {
                this.remoteControlEnabled = e.target.checked;
            });
        }

        const btnFs = document.getElementById('btnFullscreen');
        if (btnFs) {
            btnFs.addEventListener('click', () => {
                const container = document.getElementById('viewerContainer');
                if (container) {
                    if (!document.fullscreenElement) {
                        container.requestFullscreen();
                    } else {
                        document.exitFullscreen();
                    }
                }
            });
        }

        // Interactive Input Injection for Remote Control Mode
        if (this.canvas) {
            this.canvas.addEventListener('mousemove', (e) => {
                if (!this.remoteControlEnabled || !this.activePeerId) return;
                const coords = this.getRemoteCoordinates(e);
                window.App.sendWs({
                    t: 'REMOTE_CONTROL_REQUEST',
                    action: 'move',
                    x: coords.x,
                    y: coords.y
                });
            });

            this.canvas.addEventListener('mousedown', (e) => {
                if (!this.remoteControlEnabled || !this.activePeerId) return;
                const btn = e.button === 0 ? 'left' : (e.button === 2 ? 'right' : 'middle');
                window.App.sendWs({
                    t: 'REMOTE_CONTROL_REQUEST',
                    action: 'button',
                    button: btn,
                    down: true
                });
            });

            this.canvas.addEventListener('mouseup', (e) => {
                if (!this.remoteControlEnabled || !this.activePeerId) return;
                const btn = e.button === 0 ? 'left' : (e.button === 2 ? 'right' : 'middle');
                window.App.sendWs({
                    t: 'REMOTE_CONTROL_REQUEST',
                    action: 'button',
                    button: btn,
                    down: false
                });
            });

            this.canvas.addEventListener('contextmenu', (e) => e.preventDefault());
        }
    }

    getRemoteCoordinates(event) {
        const rect = this.canvas.getBoundingClientRect();
        const clientX = event.clientX - rect.left;
        const clientY = event.clientY - rect.top;

        const xRatio = clientX / rect.width;
        const yRatio = clientY / rect.height;

        return {
            x: Math.round(xRatio * this.remoteWidth),
            y: Math.round(yRatio * this.remoteHeight)
        };
    }

    start(peerId) {
        this.activePeerId = peerId;
        if (this.placeholder) this.placeholder.style.display = 'none';
        if (this.toolbar) this.toolbar.style.display = 'flex';
        if (this.canvas) this.canvas.style.display = 'block';

        window.App.sendWs({
            t: 'SCREEN_START',
            fps: 30,
            quality: 70
        });
    }

    stop() {
        if (this.activePeerId) {
            window.App.sendWs({ t: 'SCREEN_STOP' });
            this.activePeerId = null;
        }

        if (this.placeholder) this.placeholder.style.display = 'block';
        if (this.toolbar) this.toolbar.style.display = 'none';
        if (this.canvas) this.canvas.style.display = 'none';
    }

    handleFrame(frame) {
        if (!this.ctx || !this.canvas) return;

        this.remoteWidth = frame.width || 1920;
        this.remoteHeight = frame.height || 1080;

        if (this.canvas.width !== frame.width || this.canvas.height !== frame.height) {
            this.canvas.width = frame.width;
            this.canvas.height = frame.height;
            if (this.resLabel) {
                this.resLabel.textContent = `${frame.width} × ${frame.height}`;
            }
        }

        const img = new Image();
        img.onload = () => {
            this.ctx.drawImage(img, 0, 0, this.canvas.width, this.canvas.height);
            this.frameCount++;

            const now = Date.now();
            if (now - this.lastFpsCheck >= 1000) {
                const fps = Math.round((this.frameCount * 1000) / (now - this.lastFpsCheck));
                if (this.fpsLabel) {
                    this.fpsLabel.textContent = `FPS: ${fps}`;
                }
                this.frameCount = 0;
                this.lastFpsCheck = now;
            }
        };
        img.src = 'data:image/jpeg;base64,' + frame.data;
    }
}

window.RemoteViewer = new RemoteViewer();
