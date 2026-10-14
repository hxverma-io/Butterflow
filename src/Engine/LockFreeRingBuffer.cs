using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ButterFlow.Engine
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RawInputItem
    {
        public uint Sequence;
        public long QpcTimestamp;
        public short DeltaX;
        public short DeltaY;
        public short WheelDelta;
        public byte ButtonsState;
        public byte EventType; // 0 = MouseMove, 1 = MouseButton, 2 = Key, 3 = Enter, 4 = Leave
        public ushort KeyVk;
        public ushort KeyScan;
        public uint KeyFlags;
        public bool KeyIsDown;
    }

    /// <summary>
    /// Zero-allocation, lock-free Single-Producer Single-Consumer (SPSC) ring buffer.
    /// Thread-safe for 1 writer thread and 1 reader thread.
    /// Hot-path latency: < 50 nanoseconds.
    /// </summary>
    public sealed class LockFreeRingBuffer
    {
        private readonly RawInputItem[] _buffer;
        private readonly int _mask;
        private volatile int _head = 0; // Written by Producer
        private volatile int _tail = 0; // Written by Consumer

        public LockFreeRingBuffer(int capacityPowerOfTwo = 2048)
        {
            if ((capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
            {
                throw new ArgumentException("Capacity must be a power of two");
            }
            _buffer = new RawInputItem[capacityPowerOfTwo];
            _mask = capacityPowerOfTwo - 1;
        }

        public bool TryEnqueue(ref RawInputItem item)
        {
            int head = _head;
            int tail = _tail;

            // If buffer is full, cannot enqueue
            if ((head - tail) >= _buffer.Length)
            {
                return false;
            }

            _buffer[head & _mask] = item;
            Thread.MemoryBarrier();
            _head = head + 1;
            return true;
        }

        public bool TryDequeue(out RawInputItem item)
        {
            int head = _head;
            int tail = _tail;

            if (tail == head)
            {
                item = default(RawInputItem);
                return false;
            }

            item = _buffer[tail & _mask];
            Thread.MemoryBarrier();
            _tail = tail + 1;
            return true;
        }

        public int Count
        {
            get
            {
                int count = _head - _tail;
                return count >= 0 ? count : 0;
            }
        }

        public void Clear()
        {
            _head = 0;
            _tail = 0;
        }
    }
}
