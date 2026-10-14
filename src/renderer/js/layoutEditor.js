// ButterFlow Multi-Monitor Virtual Layout Editor

class LayoutEditor {
    constructor() {
        this.canvas = document.getElementById('layoutCanvas');
        this.layout = { local: {}, peers: {} };
        this.screenElements = new Map();
        this.scale = 0.12; // visual scale factor
    }

    init(layout, pairedDevices) {
        this.layout = layout || { local: {}, peers: {} };
        this.pairedDevices = pairedDevices || {};
        this.render();
    }

    render() {
        if (!this.canvas) return;
        this.canvas.innerHTML = '';
        this.screenElements.clear();

        const canvasW = this.canvas.clientWidth || 900;
        const canvasH = this.canvas.clientHeight || 420;

        // 1. Render Local Primary Screen
        const local = this.layout.local || { width: 1920, height: 1080, name: 'This PC' };
        const localW = Math.round((local.width || 1920) * this.scale);
        const localH = Math.round((local.height || 1080) * this.scale);
        const localX = Math.round((canvasW - localW) / 2);
        const localY = Math.round((canvasH - localH) / 2);

        const localBox = this.createScreenBox({
            id: 'local',
            title: local.name || 'This PC',
            subtitle: `${local.width || 1920} × ${local.height || 1080}`,
            type: 'local',
            x: localX,
            y: localY,
            w: localW,
            h: localH,
            draggable: false
        });
        this.canvas.appendChild(localBox);
        this.screenElements.set('local', localBox);

        // 2. Render Paired Peer Screens
        const peers = this.layout.peers || {};
        for (const [peerId, peer] of Object.entries(peers)) {
            const peerDev = this.pairedDevices[peerId];
            const name = peerDev ? peerDev.deviceName : (peer.deviceName || 'Remote PC');
            const pw = Math.round((peer.width || 1920) * this.scale);
            const ph = Math.round((peer.height || 1080) * this.scale);

            // Calculate position based on configured edge
            let px = localX + localW + 20;
            let py = localY;

            if (peer.edge === 'left') {
                px = localX - pw - 20;
                py = localY;
            } else if (peer.edge === 'top') {
                px = localX;
                py = localY - ph - 20;
            } else if (peer.edge === 'bottom') {
                px = localX;
                py = localY + localH + 20;
            }

            const peerBox = this.createScreenBox({
                id: peerId,
                title: name,
                subtitle: `${peer.width || 1920} × ${peer.height || 1080}`,
                type: 'remote',
                edge: peer.edge || 'right',
                x: px,
                y: py,
                w: pw,
                h: ph,
                draggable: true
            });
            this.canvas.appendChild(peerBox);
            this.screenElements.set(peerId, peerBox);
        }
    }

    createScreenBox(info) {
        const box = document.createElement('div');
        box.className = `screen-box ${info.type}`;
        box.id = `screen-${info.id}`;
        box.style.width = `${info.w}px`;
        box.style.height = `${info.h}px`;
        box.style.left = `${info.x}px`;
        box.style.top = `${info.y}px`;

        box.innerHTML = `
            <span class="screen-box-header">${info.type === 'local' ? 'Primary' : 'Remote'}</span>
            <span class="screen-box-title">${info.title}</span>
            <span class="screen-box-res">${info.subtitle}</span>
        `;

        if (info.edge) {
            const ind = document.createElement('div');
            ind.className = `screen-edge-indicator ${info.edge}`;
            box.appendChild(ind);
        }

        if (info.draggable) {
            this.makeDraggable(box, info.id);
        }

        return box;
    }

    makeDraggable(element, peerId) {
        let isDragging = false;
        let startX, startY, initialLeft, initialTop;

        element.addEventListener('mousedown', (e) => {
            isDragging = true;
            startX = e.clientX;
            startY = e.clientY;
            initialLeft = parseInt(element.style.left, 10);
            initialTop = parseInt(element.style.top, 10);
            element.style.zIndex = '100';

            const onMouseMove = (moveEvent) => {
                if (!isDragging) return;
                const dx = moveEvent.clientX - startX;
                const dy = moveEvent.clientY - startY;
                element.style.left = `${initialLeft + dx}px`;
                element.style.top = `${initialTop + dy}px`;
            };

            const onMouseUp = () => {
                if (!isDragging) return;
                isDragging = false;
                element.style.zIndex = '1';
                document.removeEventListener('mousemove', onMouseMove);
                document.removeEventListener('mouseup', onMouseUp);

                // Snap to nearest edge relative to local box
                this.snapToNearestEdge(element, peerId);
            };

            document.addEventListener('mousemove', onMouseMove);
            document.addEventListener('mouseup', onMouseUp);
        });
    }

    snapToNearestEdge(element, peerId) {
        const localBox = this.screenElements.get('local');
        if (!localBox) return;

        const lx = parseInt(localBox.style.left, 10);
        const ly = parseInt(localBox.style.top, 10);
        const lw = parseInt(localBox.style.width, 10);
        const lh = parseInt(localBox.style.height, 10);

        const ex = parseInt(element.style.left, 10);
        const ey = parseInt(element.style.top, 10);
        const ew = parseInt(element.style.width, 10);
        const eh = parseInt(element.style.height, 10);

        // Calculate center distances
        const dx = (ex + ew / 2) - (lx + lw / 2);
        const dy = (ey + eh / 2) - (ly + lh / 2);

        let chosenEdge = 'right';
        if (Math.abs(dx) > Math.abs(dy)) {
            chosenEdge = dx > 0 ? 'right' : 'left';
        } else {
            chosenEdge = dy > 0 ? 'bottom' : 'top';
        }

        // Apply snapped position
        if (chosenEdge === 'right') {
            element.style.left = `${lx + lw + 20}px`;
            element.style.top = `${ly}px`;
        } else if (chosenEdge === 'left') {
            element.style.left = `${lx - ew - 20}px`;
            element.style.top = `${ly}px`;
        } else if (chosenEdge === 'top') {
            element.style.left = `${lx}px`;
            element.style.top = `${ly - eh - 20}px`;
        } else if (chosenEdge === 'bottom') {
            element.style.left = `${lx}px`;
            element.style.top = `${ly + lh + 20}px`;
        }

        // Update layout state
        if (this.layout.peers[peerId]) {
            this.layout.peers[peerId].edge = chosenEdge;
        }

        // Update indicator
        const oldInd = element.querySelector('.screen-edge-indicator');
        if (oldInd) oldInd.remove();
        const ind = document.createElement('div');
        ind.className = `screen-edge-indicator ${chosenEdge}`;
        element.appendChild(ind);
    }

    highlightActiveFocus(peerId) {
        this.screenElements.forEach((box, id) => {
            if (id === peerId) {
                box.classList.add('active-focus');
            } else {
                box.classList.remove('active-focus');
            }
        });
    }

    getLayout() {
        return this.layout;
    }
}

window.LayoutEditor = new LayoutEditor();
