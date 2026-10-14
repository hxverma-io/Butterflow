# ButterFlow Input Protocol Specification (v2.0)

## 1. Port Allocation
- **UDP Port 42044**: High-Priority Motion Streaming (UDP, DSCP 46 EF, 1000 Hz)
- **TCP Port 42043**: Reliable Control Channel (Keys, Clicks, Clipboard, File transfers)
- **UDP Port 42042**: Subnet Broadcast Discovery & Probing

---

## 2. Real-Time UDP Motion Datagram (25 Bytes, Fixed Length)

Sent on every mouse motion event. Zero heap allocations.

```
+---------------+---------------+--------------------+---------------+---------------+
| Magic (1B)    | PacketType(1B)| Sequence Number(4B)| QPC Timestamp(8B)             |
+---------------+---------------+--------------------+---------------+---------------+
| CurDx (2B)    | CurDy (2B)    | Hist1Dx (2B)       | Hist1Dy (2B)  | Hist2Dx (2B)  |
+---------------+---------------+--------------------+---------------+---------------+
| Hist2Dy (2B)  | WheelDelta(2B)| ButtonsState (1B)  |
+---------------+---------------+--------------------+
```

### Field Definitions:
1. **Magic** (1 byte): `0xBF` (`'B'`)
2. **PacketType** (1 byte): `0x04` (`PacketType.MouseMove`)
3. **Sequence Number** (4 bytes, uint32 little-endian): Monotonically increasing counter per stream.
4. **QPC Timestamp** (8 bytes, int64 little-endian): `QueryPerformanceCounter` timestamp from the host hardware capture.
5. **CurDx, CurDy** (2 bytes each, int16 little-endian): Current relative hardware movement delta.
6. **Hist1Dx, Hist1Dy** (2 bytes each, int16 little-endian): Delta from sequence $N-1$.
7. **Hist2Dx, Hist2Dy** (2 bytes each, int16 little-endian): Delta from sequence $N-2$.
8. **WheelDelta** (2 bytes, int16 little-endian): High-resolution vertical/horizontal wheel delta (multiples of 120 or finer).
9. **ButtonsState** (1 byte, bitmask):
   - Bit 0: Left Button Down
   - Bit 1: Right Button Down
   - Bit 2: Middle Button Down
   - Bit 3: XButton 1 (Back)
   - Bit 4: XButton 2 (Forward)

### Redundancy & Packet Loss Recovery Rule:
- When packet $S$ is received:
  - If $S == S_{\text{last}} + 1$: Apply `(CurDx, CurDy)` directly.
  - If $S == S_{\text{last}} + 2$: Packet $S-1$ was lost! Apply `(Hist1Dx, Hist1Dy)` THEN apply `(CurDx, CurDy)`. Recovery achieved with 0ms delay!
  - If $S == S_{\text{last}} + 3$: Packets $S-2$ and $S-1$ were lost! Apply `(Hist2Dx, Hist2Dy)`, then `(Hist1Dx, Hist1Dy)`, then `(CurDx, CurDy)`.
  - If $S \le S_{\text{last}}$: Drop stale/out-of-order datagram immediately.

---

## 3. Reliable Key Event Packet (15 Bytes)

Sent over TCP control channel with sequence acknowledgment:

```
+---------------+--------------------+---------------+---------------+---------------+
| PacketType(1B)| Sequence Number(4B)| VkCode (2B)   | ScanCode (2B) | Flags (4B)    |
+---------------+--------------------+---------------+---------------+---------------+
| IsKeyDown (1B)| ExtendedKey (1B)   |
+---------------+--------------------+
```

---

## 4. Key State Heartbeat Packet (37 Bytes)

Broadcast every 50 ms to ensure client key states never get stuck:

```
+---------------+--------------------+---------------------------------------+
| PacketType(1B)| Sequence Number(4B)| KeyBitmap (32 Bytes / 256 bits)       |
+---------------+--------------------+---------------------------------------+
```
- Each bit represents virtual key $0$ to $255$.
- If client detects a key is held locally that is not marked in the host's KeyBitmap, client injects an immediate `KEYEVENTF_KEYUP`.

---

## 5. Screen Transition Flow (ENTER / LEAVE)

```
[Host PC]                                       [Client PC]
   │                                                 │
   ├── (Cursor reaches Right Edge)                   │
   ├── Swallows local mouse hook                     │
   ├── Locks local cursor at edge                    │
   ├── PacketType.Enter (Y_norm = 0.45) ────────────>│
   │                                                 ├── SetCursorPos(0, ScreenHeight * 0.45)
   │                                                 ├── Active receiver mode
   │                                                 │
   ├── UDP Motion Streaming (dx, dy) ───────────────>│
   │   (At 1000 Hz, sub-1.5ms latency)               ├── SendInput(MOUSEEVENTF_MOVE, dx, dy)
   │                                                 │
   │                                                 ├── (Cursor reaches Left Edge)
   │<──────────────── PacketType.Leave (Y_norm=0.46) ─┤
   ├── Restores local cursor at (ScreenWidth-2, Y)   │
   └── Releases local mouse hook                     │
```
No round-trip negotiation required. Zero edge stutter.
