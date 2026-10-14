# ButterFlow Real-Time Input Engine — Architecture Decision Document (ADD)

## 1. Executive Summary & Latency Budget
ButterFlow is a zero-latency software KVM for Windows. Moving a mouse across connected computers must feel indistinguishable from a hardware switch or physical connection.

### Latency Budget Allocation:
| Stage | Target Typical | Worst-Case Bound | Mechanism |
|---|---|---|---|
| **Host Capture** | < 0.35 ms | < 0.70 ms | Win32 Raw Input (`WM_INPUT`, `RIDEV_INPUTSINK`) on dedicated message-loop thread |
| **Local Processing** | < 0.05 ms | < 0.10 ms | Lock-free Single-Producer Single-Consumer (SPSC) ring buffer, zero heap allocations |
| **Network Transit (Wired/5GHz)**| < 0.80 ms | < 2.50 ms | Dedicated UDP socket, DSCP 46 (Expedited Forwarding), immediate zero-batch dispatch |
| **Client Ingestion & Unpack** | < 0.05 ms | < 0.10 ms | Out-of-order drop, multi-delta redundancy unpacking, no jitter buffer |
| **Client Injection** | < 0.35 ms | < 0.80 ms | `SendInput` (`MOUSEEVENTF_MOVE`), MMCSS `Games` profile, 0.5ms kernel timer |
| **Total End-to-End** | **< 1.60 ms** | **< 4.20 ms** | Well within the < 5.0 ms typical SLA |

---

## 2. Core Architectural Decisions

### Decision 1: Raw Input for Capture + Low-Level Hook Strictly for Swallowing
- **Raw Input (`RIDEV_INPUTSINK`)**: Captures true hardware relative deltas (`dx`, `dy`, high-resolution wheel, extra buttons) directly from the USB HID driver stack at up to 1000 Hz without acceleration distortion.
- **Low-Level Hooks (`WH_MOUSE_LL` / `WH_KEYBOARD_LL`)**: Low-level hooks are notorious for adding latency if used for processing. In ButterFlow, the hook callback does **ZERO** processing and **ZERO** heap allocation. When remote control is active, it returns `(IntPtr)1` in < 1 microsecond to swallow local cursor motion.

### Decision 2: Relative Movement Protocol (No Cursor Video Streaming)
- ButterFlow **never** streams cursor video. The client OS renders its own hardware cursor with local graphics hardware.
- The host transmits raw relative deltas `(dx, dy)`. The client executes `SendInput(MOUSEEVENTF_MOVE, dx, dy)`, allowing the client's native pointer speed, ballistic acceleration curves, and display scaling to apply naturally.

### Decision 3: Lock-Free SPSC Ring Buffer Thread Pipeline
To eliminate lock contention and garbage collection pauses on the hot path:
```
[Raw Input Message Loop]
       │
       ▼ (Lock-Free SPSC Ring Buffer - 1024 slots)
[Network Sender Thread] (Time-Critical, MMCSS "Games", DSCP 46)
       │
       ▼ (UDP Socket - Port 42044)
=========================== LAN Network ===========================
       │
       ▼ (UDP Socket - Port 42044)
[Network Receiver Thread] (Time-Critical, MMCSS "Games")
       │
       ▼ (Lock-Free SPSC Ring Buffer - 1024 slots)
[SendInput Injection Thread] (Time-Critical, 0.5ms Timer)
```

### Decision 4: Redundant Delta Datagrams over UDP (Zero-Jitter, Zero-Retransmit)
- **Problem**: Mouse motion cannot tolerate TCP retransmission delays (20-100 ms). But bare UDP drops packets on congested Wi-Fi.
- **Solution**: Every UDP mouse packet carries:
  - Sequence number (`uint32`)
  - Sub-microsecond QPC timestamp (`int64`)
  - Current delta: `dx_0, dy_0`
  - Historical delta 1: `dx_-1, dy_-1`
  - Historical delta 2: `dx_-2, dy_-2`
- **Result**: If packet $N$ is dropped by Wi-Fi interference, packet $N+1$ arrives containing the deltas of packet $N$. The client inspects the sequence gap and immediately applies the missing motion. Zero stutter, zero retransmission delay, zero jitter buffer.

### Decision 5: Reliable Key & Button State Machine with Heartbeat
- Keys and mouse button transitions are stateful and cannot be lost.
- Clicks and keystrokes travel over a reliable sequenced channel with ACK/retry.
- In addition, the host sends a periodic **Full Key-State Heartbeat** (256-bit bitmap of all virtual keys) every 50 ms.
- If a disconnect, timeout (>150ms), focus loss, or `Ctrl+Alt+Escape` emergency break occurs, the client automatically executes an **All-Keys-Up Flush** (`SendInput` for all currently pressed virtual keys and mouse buttons).

### Decision 6: Pre-Warmed, Zero-Hitch Edge Transitions
- Virtual desktop layout tracks coordinates in a normalized coordinate space `[0.0, 1.0]`.
- Network sockets remain permanently open and warmed (no handshake or TCP setup at crossing time).
- When host cursor reaches the crossing edge (with configurable 1-2px edge resistance barrier):
  1. Host swallows local input and clips cursor.
  2. Host sends `PacketType.Enter` with proportional entry coordinate (e.g. $Y = 0.42$).
  3. Client immediately places cursor on its entry edge via `SetCursorPos` and accepts incoming relative deltas.
  4. When client cursor returns past return edge, client emits `PacketType.Leave` and host restores local cursor.

### Decision 7: Multi-Adapter & Network Environment Guidance
- ButterFlow actively queries `NetworkInterface` to distinguish physical Ethernet (preferred, <1ms) from Wi-Fi (5GHz recommended).
- Emits real-time warnings if Wi-Fi power-saving or high jitter is detected.
