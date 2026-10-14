# ButterFlow Security & Privacy Policy

Security is an essential requirement in ButterFlow. A cross-PC collaboration and KVM tool has access to input devices, the clipboard, and the local file system. This document details the security controls, authentication mechanisms, and safeguards implemented in ButterFlow.

---

## 1. Authentication & Pairing Model

- **Mutual Pairing Handshake**:
  - Devices on the local network cannot interact or inject events without prior explicit pairing.
  - Device B generates a cryptographically random 6-digit numeric PIN with a 60-second time-to-live (TTL).
  - Device A must present this exact PIN over a direct connection.
  - Upon successful verification, both devices exchange a 32-byte cryptographic session token.
- **Session Token Validation**:
  - Every subsequent connection (WebSocket handshake) requires the stored session token.
  - Unpaired devices or expired/invalid tokens are immediately rejected.
- **Per-Device Capability Permissions**:
  - Each paired device record contains individual permission toggles:
    - `input`: Mouse and keyboard event injection
    - `clipboard`: Universal clipboard synchronization
    - `fileTransfer`: Incoming and outbound file transfers
    - `screenShare`: Desktop frame capture viewing
    - `remoteControl`: Interactive input control over shared screen
  - Permissions can be revoked or updated in the UI at any time without unpairing the device.

---

## 2. File Transfer Protection & Sanitization

- **Path Traversal Defense**:
  - Filenames received from remote peers are strictly sanitized by `security.sanitizeFilename()`.
  - Directory traversal sequences (`..`), forward slashes (`/`), backslashes (`\`), control characters, and reserved Windows device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1..9`, `LPT1..9`) are sanitized or prefixed.
- **Atomic Staging & Cryptographic Verification**:
  - Inbound files are written to a `.butterflow.tmp` temporary file during transmission.
  - Transferred bytes are concurrently hashed using SHA-256.
  - The destination file is atomically renamed to its final name **only** if the computed SHA-256 hash matches the expected hash sent by the source.
  - In the event of a mismatch or interrupted transfer, the `.tmp` file is safely deleted and marked as corrupted in the audit log.
- **Duplicate Conflict Management**:
  - Configurable conflict strategy: `rename` (appends `(1)`, `(2)`), `replace`, or `skip`.

---

## 3. Input Safety & Stuck-Key Prevention

- **Emergency Breakout Hotkey**:
  - The native keyboard hook intercepts `Ctrl + Alt + Escape` or `Ctrl + Alt + Break` at the lowest operating system level.
  - Upon activation, the hook instantly releases cursor trapping, sets remote mode to inactive, and transmits an explicit `KEYEVENTF_KEYUP` for all registered down keys.
- **Connection Loss Protection**:
  - When a peer connection drops unexpectedly, the input manager immediately restores local mouse control and clears all held keys to avoid stuck modifier or character keys.

---

## 4. Clipboard Privacy

- **Loop Prevention**:
  - SHA-256 hashes of outgoing and incoming clipboard contents are tracked to prevent echo loops.
- **Selective Sharing**:
  - Clipboard synchronization is disabled if the user toggles off clipboard permissions in the device dashboard.

---

## 5. Security Audit Log

All significant security actions (pairing code generations, device pairings, session authorizations, emergency releases, file completions, and corruption alerts) are recorded in an in-memory and persistent security log (`securityLogs`) accessible via the UI dashboard and `GET /api/logs`.
