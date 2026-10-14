using System;
using System.IO;
using System.Text;

namespace ButterFlow.Core
{
    public enum ScreenEdge
    {
        Right = 0,
        Left = 1,
        Top = 2,
        Bottom = 3
    }

    public enum AppMode
    {
        Idle = 0,
        Host = 1,
        Client = 2
    }

    public enum PacketType : byte
    {
        DiscoveryBeacon = 1,
        HandshakeRequest = 2,
        HandshakeResponse = 3,
        MouseMove = 4,
        MouseButton = 5,
        MouseWheel = 6,
        KeyEvent = 7,
        SwitchBack = 8,
        ClipboardSync = 9,
        Ping = 10,
        Pong = 11,
        ImageClipboardSync = 12,
        FileTransferHeader = 13,
        FileTransferChunk = 14,
        FileTransferComplete = 15,
        DragStart = 16,
        DragMotion = 17,
        DragDrop = 18,
        DragCancel = 19,
        RequestReverseDrag = 20
    }

    public enum MouseButtonType : byte
    {
        Left = 1,
        Right = 2,
        Middle = 3
    }

    public enum ButtonState : byte
    {
        Down = 1,
        Up = 2
    }

    public static class Protocol
    {
        public const int DISCOVERY_PORT = 42042;
        public const int TCP_CONTROL_PORT = 42043;
        public const int UDP_MOTION_PORT = 42044;

        public const byte MAGIC_BYTE = 0xBF;
        public const int MOTION_PACKET_SIZE = 37;
        public const int LEGACY_MOTION_PACKET_SIZE = 21;

        // High-Performance Zero-Allocation Redundant Motion Packet (37 bytes):
        // [0] Magic: 0xBF
        // [1] Type: PacketType.MouseMove (0x04)
        // [2..5] Sequence: uint (4 bytes)
        // [6..13] QpcTimestamp: long (8 bytes)
        // [14..17] NormX: float (4 bytes)
        // [18..21] NormY: float (4 bytes)
        // [22..23] CurDx: short (2 bytes)
        // [24..25] CurDy: short (2 bytes)
        // [26..27] Hist1Dx: short (2 bytes)
        // [28..29] Hist1Dy: short (2 bytes)
        // [30..31] Hist2Dx: short (2 bytes)
        // [32..33] Hist2Dy: short (2 bytes)
        // [34..35] WheelDelta: short (2 bytes)
        // [36] Buttons: byte (1 byte)
        public static void SerializeMotionPacketDirect(byte[] buffer, uint seq, long qpc, float normX, float normY, short curDx, short curDy, short hist1Dx, short hist1Dy, short hist2Dx, short hist2Dy, short wheel = 0, byte buttons = 0)
        {
            buffer[0] = MAGIC_BYTE;
            buffer[1] = (byte)PacketType.MouseMove;

            buffer[2] = (byte)(seq);
            buffer[3] = (byte)(seq >> 8);
            buffer[4] = (byte)(seq >> 16);
            buffer[5] = (byte)(seq >> 24);

            for (int i = 0; i < 8; i++)
            {
                buffer[6 + i] = (byte)(qpc >> (i * 8));
            }

            byte[] nx = BitConverter.GetBytes(normX);
            buffer[14] = nx[0]; buffer[15] = nx[1]; buffer[16] = nx[2]; buffer[17] = nx[3];
            byte[] ny = BitConverter.GetBytes(normY);
            buffer[18] = ny[0]; buffer[19] = ny[1]; buffer[20] = ny[2]; buffer[21] = ny[3];

            buffer[22] = (byte)(curDx);
            buffer[23] = (byte)(curDx >> 8);
            buffer[24] = (byte)(curDy);
            buffer[25] = (byte)(curDy >> 8);

            buffer[26] = (byte)(hist1Dx);
            buffer[27] = (byte)(hist1Dx >> 8);
            buffer[28] = (byte)(hist1Dy);
            buffer[29] = (byte)(hist1Dy >> 8);

            buffer[30] = (byte)(hist2Dx);
            buffer[31] = (byte)(hist2Dx >> 8);
            buffer[32] = (byte)(hist2Dy);
            buffer[33] = (byte)(hist2Dy >> 8);

            buffer[34] = (byte)(wheel);
            buffer[35] = (byte)(wheel >> 8);

            buffer[36] = buttons;
        }

        public static void DeserializeMotionPacket(byte[] data, out uint seq, out long qpc, out float normX, out float normY, out short curDx, out short curDy, out short hist1Dx, out short hist1Dy, out short hist2Dx, out short hist2Dy, out short wheel, out byte buttons)
        {
            seq = (uint)(data[2] | (data[3] << 8) | (data[4] << 16) | (data[5] << 24));
            
            long timestamp = 0;
            for (int i = 0; i < 8; i++)
            {
                timestamp |= ((long)data[6 + i]) << (i * 8);
            }
            qpc = timestamp;

            normX = BitConverter.ToSingle(data, 14);
            normY = BitConverter.ToSingle(data, 18);

            curDx = (short)(data[22] | (data[23] << 8));
            curDy = (short)(data[24] | (data[25] << 8));

            hist1Dx = (short)(data[26] | (data[27] << 8));
            hist1Dy = (short)(data[28] | (data[29] << 8));

            hist2Dx = (short)(data[30] | (data[31] << 8));
            hist2Dy = (short)(data[32] | (data[33] << 8));

            wheel = (short)(data[34] | (data[35] << 8));
            buttons = data[36];
        }

        // Legacy mouse motion packet: compact 21 bytes (Type:1, Seq:4, normX:4, normY:4, deltaX:4, deltaY:4)
        public static byte[] SerializeMouseMove(uint seq, float normX, float normY, int deltaX, int deltaY)
        {
            byte[] buffer = new byte[LEGACY_MOTION_PACKET_SIZE];
            buffer[0] = (byte)PacketType.MouseMove;
            BitConverter.GetBytes(seq).CopyTo(buffer, 1);
            BitConverter.GetBytes(normX).CopyTo(buffer, 5);
            BitConverter.GetBytes(normY).CopyTo(buffer, 9);
            BitConverter.GetBytes(deltaX).CopyTo(buffer, 13);
            BitConverter.GetBytes(deltaY).CopyTo(buffer, 17);
            return buffer;
        }

        public static void DeserializeMouseMove(byte[] data, out uint seq, out float normX, out float normY, out int deltaX, out int deltaY)
        {
            seq = BitConverter.ToUInt32(data, 1);
            normX = BitConverter.ToSingle(data, 5);
            normY = BitConverter.ToSingle(data, 9);
            deltaX = BitConverter.ToInt32(data, 13);
            deltaY = BitConverter.ToInt32(data, 17);
        }

        // Mouse button packet: 3 bytes (Type:1, Button:1, State:1)
        public static byte[] SerializeMouseButton(MouseButtonType button, ButtonState state)
        {
            return new byte[] { (byte)PacketType.MouseButton, (byte)button, (byte)state };
        }

        public static void DeserializeMouseButton(byte[] data, out MouseButtonType button, out ButtonState state)
        {
            button = (MouseButtonType)data[1];
            state = (ButtonState)data[2];
        }

        // Mouse wheel: 5 bytes (Type:1, Delta:4)
        public static byte[] SerializeMouseWheel(int delta)
        {
            byte[] buffer = new byte[5];
            buffer[0] = (byte)PacketType.MouseWheel;
            BitConverter.GetBytes(delta).CopyTo(buffer, 1);
            return buffer;
        }

        public static int DeserializeMouseWheel(byte[] data)
        {
            return BitConverter.ToInt32(data, 1);
        }

        // Keyboard event: 10 bytes (Type:1, vkCode:2, scanCode:2, flags:4, isDown:1)
        public static byte[] SerializeKeyEvent(ushort vkCode, ushort scanCode, uint flags, bool isKeyDown)
        {
            byte[] buffer = new byte[10];
            buffer[0] = (byte)PacketType.KeyEvent;
            BitConverter.GetBytes(vkCode).CopyTo(buffer, 1);
            BitConverter.GetBytes(scanCode).CopyTo(buffer, 3);
            BitConverter.GetBytes(flags).CopyTo(buffer, 5);
            buffer[9] = (byte)(isKeyDown ? 1 : 0);
            return buffer;
        }

        public static void DeserializeKeyEvent(byte[] data, out ushort vkCode, out ushort scanCode, out uint flags, out bool isKeyDown)
        {
            vkCode = BitConverter.ToUInt16(data, 1);
            scanCode = BitConverter.ToUInt16(data, 3);
            flags = BitConverter.ToUInt32(data, 5);
            isKeyDown = data[9] == 1;
        }

        // Switch back packet: 5 bytes (Type:1, normY:4)
        public static byte[] SerializeSwitchBack(float normY)
        {
            byte[] buffer = new byte[5];
            buffer[0] = (byte)PacketType.SwitchBack;
            BitConverter.GetBytes(normY).CopyTo(buffer, 1);
            return buffer;
        }

        public static float DeserializeSwitchBack(byte[] data)
        {
            return BitConverter.ToSingle(data, 1);
        }

        // Clipboard sync packet: (Type:1, Length:4, UTF8 bytes)
        public static byte[] SerializeClipboard(string text)
        {
            byte[] textBytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
            byte[] buffer = new byte[5 + textBytes.Length];
            buffer[0] = (byte)PacketType.ClipboardSync;
            BitConverter.GetBytes(textBytes.Length).CopyTo(buffer, 1);
            Array.Copy(textBytes, 0, buffer, 5, textBytes.Length);
            return buffer;
        }

        public static string DeserializeClipboard(byte[] data)
        {
            if (data.Length < 5) return string.Empty;
            int length = BitConverter.ToInt32(data, 1);
            if (length <= 0 || data.Length < 5 + length) return string.Empty;
            return Encoding.UTF8.GetString(data, 5, length);
        }

        // Image / Screenshot Clipboard packet: (Type:1, Length:4, PNG bytes)
        public static byte[] SerializeImageClipboard(byte[] pngBytes)
        {
            int len = pngBytes != null ? pngBytes.Length : 0;
            byte[] buffer = new byte[5 + len];
            buffer[0] = (byte)PacketType.ImageClipboardSync;
            BitConverter.GetBytes(len).CopyTo(buffer, 1);
            if (len > 0) Array.Copy(pngBytes, 0, buffer, 5, len);
            return buffer;
        }

        public static byte[] DeserializeImageClipboard(byte[] data)
        {
            if (data.Length < 5) return null;
            int len = BitConverter.ToInt32(data, 1);
            if (len <= 0 || data.Length < 5 + len) return null;
            byte[] result = new byte[len];
            Array.Copy(data, 5, result, 0, len);
            return result;
        }

        // File Transfer: Header (Type:1, nameLen:4, nameBytes:N, fileSize:8)
        public static byte[] SerializeFileHeader(string fileName, long fileSize)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(fileName ?? "file.bin");
            byte[] buffer = new byte[1 + 4 + nameBytes.Length + 8];
            buffer[0] = (byte)PacketType.FileTransferHeader;
            BitConverter.GetBytes(nameBytes.Length).CopyTo(buffer, 1);
            Array.Copy(nameBytes, 0, buffer, 5, nameBytes.Length);
            BitConverter.GetBytes(fileSize).CopyTo(buffer, 5 + nameBytes.Length);
            return buffer;
        }

        public static void DeserializeFileHeader(byte[] data, out string fileName, out long fileSize)
        {
            int nameLen = BitConverter.ToInt32(data, 1);
            fileName = Encoding.UTF8.GetString(data, 5, nameLen);
            fileSize = BitConverter.ToInt64(data, 5 + nameLen);
        }

        // File Transfer: Chunk (Type:1, count:4, chunkBytes:N)
        public static byte[] SerializeFileChunk(byte[] chunk, int offset, int count)
        {
            byte[] buffer = new byte[1 + 4 + count];
            buffer[0] = (byte)PacketType.FileTransferChunk;
            BitConverter.GetBytes(count).CopyTo(buffer, 1);
            Array.Copy(chunk, offset, buffer, 5, count);
            return buffer;
        }

        // File Transfer: Complete (Type:1, nameLen:4, nameBytes:N)
        public static byte[] SerializeFileComplete(string fileName)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(fileName ?? "");
            byte[] buffer = new byte[1 + 4 + nameBytes.Length];
            buffer[0] = (byte)PacketType.FileTransferComplete;
            BitConverter.GetBytes(nameBytes.Length).CopyTo(buffer, 1);
            Array.Copy(nameBytes, 0, buffer, 5, nameBytes.Length);
            return buffer;
        }

        public static string DeserializeFileComplete(byte[] data)
        {
            if (data.Length < 5) return string.Empty;
            int nameLen = BitConverter.ToInt32(data, 1);
            return Encoding.UTF8.GetString(data, 5, nameLen);
        }

        // Ping / Pong packets: 9 bytes (Type:1, Timestamp:8)
        public static byte[] SerializePing(long timestamp)
        {
            byte[] buffer = new byte[9];
            buffer[0] = (byte)PacketType.Ping;
            BitConverter.GetBytes(timestamp).CopyTo(buffer, 1);
            return buffer;
        }

        public static byte[] SerializePong(long timestamp)
        {
            byte[] buffer = new byte[9];
            buffer[0] = (byte)PacketType.Pong;
            BitConverter.GetBytes(timestamp).CopyTo(buffer, 1);
            return buffer;
        }

        public static long DeserializeTimestamp(byte[] data)
        {
            return BitConverter.ToInt64(data, 1);
        }

        // Cross-Screen Drag & Drop: DragStart (Type:1, nameLen:4, nameBytes:N, fileSize:8)
        public static byte[] SerializeDragStart(string fileName, long fileSize)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(fileName ?? "file.bin");
            byte[] buffer = new byte[1 + 4 + nameBytes.Length + 8];
            buffer[0] = (byte)PacketType.DragStart;
            BitConverter.GetBytes(nameBytes.Length).CopyTo(buffer, 1);
            Array.Copy(nameBytes, 0, buffer, 5, nameBytes.Length);
            BitConverter.GetBytes(fileSize).CopyTo(buffer, 5 + nameBytes.Length);
            return buffer;
        }

        public static void DeserializeDragStart(byte[] data, out string fileName, out long fileSize)
        {
            int nameLen = BitConverter.ToInt32(data, 1);
            fileName = Encoding.UTF8.GetString(data, 5, nameLen);
            fileSize = BitConverter.ToInt64(data, 5 + nameLen);
        }

        // Cross-Screen Drag & Drop: DragMotion (Type:1, normX:4, normY:4)
        public static byte[] SerializeDragMotion(float normX, float normY)
        {
            byte[] buffer = new byte[9];
            buffer[0] = (byte)PacketType.DragMotion;
            BitConverter.GetBytes(normX).CopyTo(buffer, 1);
            BitConverter.GetBytes(normY).CopyTo(buffer, 5);
            return buffer;
        }

        public static void DeserializeDragMotion(byte[] data, out float normX, out float normY)
        {
            normX = BitConverter.ToSingle(data, 1);
            normY = BitConverter.ToSingle(data, 5);
        }

        // Cross-Screen Drag & Drop: DragDrop (Type:1, normX:4, normY:4)
        public static byte[] SerializeDragDrop(float dropX, float dropY)
        {
            byte[] buffer = new byte[9];
            buffer[0] = (byte)PacketType.DragDrop;
            BitConverter.GetBytes(dropX).CopyTo(buffer, 1);
            BitConverter.GetBytes(dropY).CopyTo(buffer, 5);
            return buffer;
        }

        public static void DeserializeDragDrop(byte[] data, out float dropX, out float dropY)
        {
            dropX = BitConverter.ToSingle(data, 1);
            dropY = BitConverter.ToSingle(data, 5);
        }

        // Cross-Screen Drag & Drop: DragCancel (Type:1)
        public static byte[] SerializeDragCancel()
        {
            return new byte[] { (byte)PacketType.DragCancel };
        }

        // Reverse Drag & Drop: Request Reverse Drag from Client (Type:1)
        public static byte[] SerializeRequestReverseDrag()
        {
            return new byte[] { (byte)PacketType.RequestReverseDrag };
        }
    }
}
