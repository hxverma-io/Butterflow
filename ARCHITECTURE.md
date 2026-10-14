# ButterFlow Architecture Document

This document provides a detailed technical breakdown of ButterFlow's architecture, subsystems, event lifecycles, and performance engineering design.

---

## 1. Core Principles

1. **Bidirectional Symmetry**: Any machine running ButterFlow has the exact same capabilities. Roles (host vs client) are ephemeral and connection-specific.
2. **Sub-10ms Input Latency**: The input delivery path avoids all disk I/O, heavy frameworks, synchronous rendering, and database queries.
3. **Defense in Depth**: Zero unauthenticated control injection. All actions require prior mutual pairing, cryptographic token validation, and explicit per-device capability permissions.
4. **Resilience & Safe Recovery**: Network disconnects, emergency hotkeys, or peer crashes must never leave the local or remote operating system in an unusable state (e.g., stuck mouse buttons, held keys, or trapped pointers).

---

## 2. Subsystem Architecture

### 2.1 Native Windows Systems Engine (`ButterFlowNative.exe`)
Built in C# and compiled via Microsoft Visual C# Compiler (`csc.exe`):
- **Mouse Hook (`WH_MOUSE_LL` = 14)**:
  - Hooks `WM_MOUSEMOVE`, `WM_LBUTTONDOWN/UP`, `WM_RBUTTONDOWN/UP`, `WM_MBUTTONDOWN/UP`, `WM_MOUSEWHEEL`, `WM_MOUSEHWHEEL`.
  - When input sharing is active: intercepts local events (`return (IntPtr)1`), prevents local cursor movement, and outputs deltas (`dx, dy, x, y, button, wheel`) over standard output.
  - Border transition detection: observes cursor position against configured display geometry and edge thresholds (default 2px) to trigger transitions.
- **Keyboard Hook (`WH_KEYBOARD_LL` = 13)**:
  - Hooks `WM_KEYDOWN`, `WM_KEYUP`, `WM_SYSKEYDOWN`, `WM_SYSKEYUP`.
  - Suppresses local keystrokes when remote input is active and streams `vkCode`, `scanCode`, `isExtended`, and `isDown`.
  - Emergency breakout detection: catches `Ctrl + Alt + Break` (VK 0x13) or `Ctrl + Alt + Escape` (VK 0x1B) at the hook level, releasing cursor lock and flushing keys instantly.
- **Hardware-Level Input Injection (`SendInput`)**:
  - Injects relative mouse movement (`MOUSEEVENTF_MOVE`), absolute positioning (`MOUSEEVENTF_ABSOLUTE`), and button events.
  - Injects keyboard events with exact virtual keys and scan codes.
  - Maintains `_activeDownKeys` hash set: when a session ends or disconnects, automatically sends `KEYEVENTF_KEYUP` for all held keys to prevent stuck keys.
- **Desktop Screen Capture**:
  - GDI `CopyFromScreen` from primary desktop DC to in-memory bitmap.
  - Encodes directly to JPEG memory stream with adjustable quality parameter (e.g. 60-80) and sends base64 image data to the Node layer.

### 2.2 Low-Latency Network Transport
- **UDP Discovery (`discovery.js`)**:
  - Listens on `0.0.0.0:38445`.
  - Broadcasts beacon every 3s to `255.255.255.255` containing device ID, device name, listening port, and screen metrics.
  - Sweeps peers unseen for >10s to maintain an accurate LAN device registry.
- **WebSocket Transport (`server.js` and `client.js`)**:
  - Persistent, binary/compact JSON transport for input events and control messages.
  - High-frequency PING/PONG keepalives with microsecond timestamps measuring RTT, median, and p95 latency.
  - Independent channels: high-priority input events (`M`, `B`, `W`, `K`) are prioritized over bulk file chunks.

### 2.3 Edge Transition Math & Coordinate Normalization
- Screen arrangements are stored as relative topologies (`left`, `right`, `top`, `bottom`).
- When a pointer reaches the edge of display A at coordinate `(x, y)`:
  - If exiting Right: entry on display B is Left edge `x = 0`, with vertical position scaled proportionally:
    $$\text{entryY} = \text{round}\left(\frac{y}{H_A} \times H_B\right)$$
  - If exiting Top: entry on display B is Bottom edge `y = H_B - 1`, with horizontal position scaled proportionally:
    $$\text{entryX} = \text{round}\left(\frac{x}{W_A} \times W_B\right)$$
- This ensures seamless pointer transition across monitors of differing resolutions and DPI scaling factors.

### 2.4 File Transfer Pipeline
- **Chunked Streaming**: Files are divided into 64KB blocks and streamed sequentially with flow control.
- **Cryptographic Verification**: Both sender and receiver stream bytes into a SHA-256 digest engine.
- **Atomic Safe Finalization**: Inbound files write to `<filename>.butterflow.tmp`. Once the computed SHA-256 hash matches the expected hash, the file is atomically renamed to its target path. If integrity fails, the temp file is purged.
- **Path Traversal Defense**: All filenames pass through `security.sanitizeFilename()` stripping directory traversal tokens (`..`), path delimiters (`/`, `\`), and Windows reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1..9`, `LPT1..9`).

### 2.5 Universal Clipboard Synchronization
- Background poller checks system clipboard every 800ms (or on-change via Electron clipboard events).
- Calculates SHA-256 hash of content.
- Maintains `lastSentHash` and `lastReceivedHash` to prevent retransmitting echo loops.
- Per-device clipboard permission verification prior to broadcasting.

---

## 3. Threat Model & Security Controls

| Threat Vector | Mitigation Strategy |
|---|---|
| Unauthorized network input injection | Mutual 6-digit PIN handshake + 32-byte cryptographic session token. Unpaired nodes rejected. |
| Malicious file path traversal | Aggressive filename sanitization preventing directory escapes. |
| Incomplete / tampered file deliveries | End-to-end SHA-256 integrity verification before finalizing file write. |
| Malicious screen snooping | Screen sharing requires explicit user permission toggle per paired device. |
| Denial of service / stuck remote control | Native emergency breakout hotkey resets input and restores local control immediately. |
