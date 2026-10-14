using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ButterFlow.Core;

namespace ButterFlow.Network
{
    public class InputReceiver : IDisposable
    {
        private TcpListener _tcpListener;
        private TcpClient _activeClient;
        private NetworkStream _activeStream;
        private UdpClient _udpReceiver;
        private Thread _tcpListenThread;
        private Thread _clientWorkerThread;
        private Thread _udpMotionThread;
        private bool _isRunning;
        private readonly object _streamLock = new object();

        private readonly int _inputSize = Marshal.SizeOf(typeof(NativeMethods.INPUT));

        public int ScreenWidth { get; set; }
        public int ScreenHeight { get; set; }
        public ScreenEdge HostPositionRelativeToClient { get; set; } // Default: Host is to the Left of Client

        private DateTime _enterTime = DateTime.MinValue;
        private int _returnPushCount = 0;

        // Sequence deduplication and precision 0ms-latency hardware cursor tracking
        private uint _lastProcessedSeq = 0;
        private int _clientCursorX = -1;
        private int _clientCursorY = -1;
        private readonly object _cursorSyncLock = new object();

        // File transfer receiving state
        private FileStream _receivingFileStream = null;
        private string _receivingFileName = null;
        private long _receivingFileSize = 0;
        private long _receivingBytesReceived = 0;
        private bool _isLeftButtonDown = false;
        private List<string> _lastClientSelectedFiles = null;

        public event Action<bool> OnConnectionStatusChanged;
        public event Action<string> OnClipboardReceived;
        public event Action<byte[]> OnImageClipboardReceived;
        public event Action<string, string> OnFileReceived;
        public event Action<string, long> OnDragStarted;
        public event Action<float, float> OnDragMoved;
        public event Action<float, float> OnDragDropped;
        public event Action OnDragCancelled;
        public event Action<string> OnHandshakeReceived;
        public event Action<string> OnLogMessage;

        private bool _isCrossScreenDragActive = false;
        private string _stagedReceivedFilePath = null;
        private string _stagedReceivedFileName = null;
        private readonly NativeMethods.INPUT[] _moveInputs = new NativeMethods.INPUT[1];

        // Adaptive Sub-Millisecond Micro-EMA Filter (Display Refresh Sync & Jitter Elimination)
        private float _filteredDx = 0f;
        private float _filteredDy = 0f;
        private float _subPixelRemX = 0f;
        private float _subPixelRemY = 0f;

        public bool IsConnected { get { return _activeClient != null && _activeClient.Connected; } }

        public InputReceiver(int screenW, int screenH)
        {
            ScreenWidth = screenW;
            ScreenHeight = screenH;
            HostPositionRelativeToClient = ScreenEdge.Left;
            _moveInputs[0].type = NativeMethods.INPUT_MOUSE;
            _moveInputs[0].u.mi.dwFlags = NativeMethods.MOUSEEVENTF_MOVE;
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;

            // 1. Force Windows Kernel to sub-millisecond 0.5ms scheduling precision!
            NativeMethods.TimeBeginPeriod(1);
            try
            {
                uint curRes;
                NativeMethods.NtSetTimerResolution(5000, true, out curRes); // 0.5ms!
            }
            catch { }

            try
            {
                _tcpListener = new TcpListener(IPAddress.Any, Protocol.TCP_CONTROL_PORT);
                _tcpListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _tcpListener.Start();

                _tcpListenThread = new Thread(TcpListenLoop) { IsBackground = true, Name = "ReceiverTcpListener" };
                _tcpListenThread.Start();

                _udpReceiver = new UdpClient();
                _udpReceiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpReceiver.Client.ReceiveBufferSize = 524288; // 512KB buffer for zero drop
                try
                {
                    _udpReceiver.Client.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)3, 0xB8); // DSCP 46 VIP priority
                }
                catch { }

                // Disable WSAECONNRESET (10054) on UDP socket so ICMP errors never kill the listener
                try
                {
                    const int SIO_UDP_CONNRESET = -1744830452;
                    _udpReceiver.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, new byte[] { 0 });
                }
                catch { }

                _udpReceiver.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.UDP_MOTION_PORT));

                _udpMotionThread = new Thread(UdpMotionLoop) { IsBackground = true, Name = "ReceiverUdpMotion" };
                _udpMotionThread.Priority = ThreadPriority.Highest; // Highest priority for zero latency
                _udpMotionThread.Start();

                Log("Listening for Host on ports " + Protocol.TCP_CONTROL_PORT + " (TCP) / " + Protocol.UDP_MOTION_PORT + " (UDP) [0.5ms Timer Active]");
            }
            catch (Exception ex)
            {
                Log("Receiver start error: " + ex.Message);
            }
        }

        private void TcpListenLoop()
        {
            while (_isRunning)
            {
                try
                {
                    TcpClient client = _tcpListener.AcceptTcpClient();
                    client.NoDelay = true;

                    lock (_streamLock)
                    {
                        if (_activeClient != null)
                        {
                            try { _activeClient.Close(); } catch { }
                        }
                        _activeClient = client;
                        _activeStream = client.GetStream();
                    }

                    _enterTime = DateTime.Now;
                    Log("Host connected from: " + ((IPEndPoint)client.Client.RemoteEndPoint).Address);
                    if (OnConnectionStatusChanged != null) OnConnectionStatusChanged(true);

                    _clientWorkerThread = new Thread(ClientWorkerLoop) { IsBackground = true, Name = "ReceiverWorker" };
                    _clientWorkerThread.Priority = ThreadPriority.Highest;
                    _clientWorkerThread.Start();
                }
                catch
                {
                    break;
                }
            }
        }

        private void ClientWorkerLoop()
        {
            byte[] header = new byte[1];
            while (_isRunning && _activeClient != null && _activeClient.Connected)
            {
                try
                {
                    int read = _activeStream.Read(header, 0, 1);
                    if (read <= 0) break;

                    PacketType type = (PacketType)header[0];

                    switch (type)
                    {
                        case PacketType.HandshakeRequest:
                            byte[] hsLenBuf = new byte[4];
                            ReadExact(_activeStream, hsLenBuf, 0, 4);
                            int hsLen = BitConverter.ToInt32(hsLenBuf, 0);
                            if (hsLen > 0 && hsLen < 256)
                            {
                                byte[] hsNameBuf = new byte[hsLen];
                                ReadExact(_activeStream, hsNameBuf, 0, hsLen);
                                string hostMachine = Encoding.UTF8.GetString(hsNameBuf);
                                Log("Host identified: " + hostMachine);
                                if (OnHandshakeReceived != null) OnHandshakeReceived(hostMachine);
                            }
                            break;

                        case PacketType.MouseMove:
                            byte[] moveBody = new byte[20];
                            ReadExact(_activeStream, moveBody, 0, 20);
                            // Mouse movement is handled strictly over dedicated high-speed UDP to prevent latency/flutter
                            break;

                        case PacketType.Ping:
                            byte[] pingBody = new byte[8];
                            ReadExact(_activeStream, pingBody, 0, 8);
                            long timestamp = BitConverter.ToInt64(pingBody, 0);
                            byte[] pong = Protocol.SerializePong(timestamp);
                            lock (_streamLock)
                            {
                                if (_activeStream != null) _activeStream.Write(pong, 0, pong.Length);
                            }
                            break;

                        case PacketType.MouseButton:
                            byte[] btnBody = new byte[2];
                            ReadExact(_activeStream, btnBody, 0, 2);
                            MouseButtonType button = (MouseButtonType)btnBody[0];
                            ButtonState state = (ButtonState)btnBody[1];
                            InjectMouseButton(button, state);
                            break;

                        case PacketType.MouseWheel:
                            byte[] wheelBody = new byte[4];
                            ReadExact(_activeStream, wheelBody, 0, 4);
                            int delta = BitConverter.ToInt32(wheelBody, 0);
                            InjectMouseWheel(delta);
                            break;

                        case PacketType.KeyEvent:
                            byte[] keyBody = new byte[9];
                            ReadExact(_activeStream, keyBody, 0, 9);
                            ushort vkCode = BitConverter.ToUInt16(keyBody, 0);
                            ushort scanCode = BitConverter.ToUInt16(keyBody, 2);
                            uint flags = BitConverter.ToUInt32(keyBody, 4);
                            bool isDown = keyBody[8] == 1;
                            InjectKeyEvent(vkCode, scanCode, flags, isDown);
                            break;

                        case PacketType.ClipboardSync:
                            byte[] lenBuf = new byte[4];
                            ReadExact(_activeStream, lenBuf, 0, 4);
                            int len = BitConverter.ToInt32(lenBuf, 0);
                            if (len > 0 && len < 10000000)
                            {
                                byte[] strBuf = new byte[len];
                                ReadExact(_activeStream, strBuf, 0, len);
                                byte[] full = new byte[5 + len];
                                full[0] = (byte)PacketType.ClipboardSync;
                                Array.Copy(lenBuf, 0, full, 1, 4);
                                Array.Copy(strBuf, 0, full, 5, len);
                                string clipText = Protocol.DeserializeClipboard(full);
                                if (OnClipboardReceived != null) OnClipboardReceived(clipText);
                            }
                            break;

                        case PacketType.ImageClipboardSync:
                            byte[] imgLenBuf = new byte[4];
                            ReadExact(_activeStream, imgLenBuf, 0, 4);
                            int imgLen = BitConverter.ToInt32(imgLenBuf, 0);
                            if (imgLen > 0 && imgLen < 50000000) // Up to 50MB image
                            {
                                byte[] pngBytes = new byte[imgLen];
                                ReadExact(_activeStream, pngBytes, 0, imgLen);
                                if (OnImageClipboardReceived != null) OnImageClipboardReceived(pngBytes);
                            }
                            break;

                        case PacketType.FileTransferHeader:
                            byte[] fNameLenBuf = new byte[4];
                            ReadExact(_activeStream, fNameLenBuf, 0, 4);
                            int fNameLen = BitConverter.ToInt32(fNameLenBuf, 0);
                            byte[] fNameBuf = new byte[fNameLen];
                            ReadExact(_activeStream, fNameBuf, 0, fNameLen);
                            string incomingName = System.Text.Encoding.UTF8.GetString(fNameBuf);

                            byte[] fSizeBuf = new byte[8];
                            ReadExact(_activeStream, fSizeBuf, 0, 8);
                            long incomingSize = BitConverter.ToInt64(fSizeBuf, 0);

                            StartReceivingFile(incomingName, incomingSize);
                            break;

                        case PacketType.FileTransferChunk:
                            byte[] chunkLenBuf = new byte[4];
                            ReadExact(_activeStream, chunkLenBuf, 0, 4);
                            int chunkCount = BitConverter.ToInt32(chunkLenBuf, 0);
                            byte[] chunkBytes = new byte[chunkCount];
                            ReadExact(_activeStream, chunkBytes, 0, chunkCount);

                            WriteReceivingFileChunk(chunkBytes, chunkCount);
                            break;

                        case PacketType.FileTransferComplete:
                            byte[] compLenBuf = new byte[4];
                            ReadExact(_activeStream, compLenBuf, 0, 4);
                            int compNameLen = BitConverter.ToInt32(compLenBuf, 0);
                            byte[] compNameBuf = new byte[compNameLen];
                            ReadExact(_activeStream, compNameBuf, 0, compNameLen);
                            string compName = System.Text.Encoding.UTF8.GetString(compNameBuf);

                            FinishReceivingFile(compName);
                            break;

                        case PacketType.DragStart:
                            byte[] dsNameLenBuf = new byte[4];
                            ReadExact(_activeStream, dsNameLenBuf, 0, 4);
                            int dsNameLen = BitConverter.ToInt32(dsNameLenBuf, 0);
                            byte[] dsNameBuf = new byte[dsNameLen];
                            ReadExact(_activeStream, dsNameBuf, 0, dsNameLen);
                            string dsName = System.Text.Encoding.UTF8.GetString(dsNameBuf);

                            byte[] dsSizeBuf = new byte[8];
                            ReadExact(_activeStream, dsSizeBuf, 0, 8);
                            long dsSize = BitConverter.ToInt64(dsSizeBuf, 0);

                            _isCrossScreenDragActive = true;
                            Log("🚀 Cross-Screen Drag incoming: " + dsName);
                            if (OnDragStarted != null) OnDragStarted(dsName, dsSize);
                            break;

                        case PacketType.DragMotion:
                            byte[] dmMotionBuf = new byte[8];
                            ReadExact(_activeStream, dmMotionBuf, 0, 8);
                            float dmNormX = BitConverter.ToSingle(dmMotionBuf, 0);
                            float dmNormY = BitConverter.ToSingle(dmMotionBuf, 4);
                            if (OnDragMoved != null) OnDragMoved(dmNormX, dmNormY);
                            break;

                        case PacketType.DragDrop:
                            byte[] ddDropBuf = new byte[8];
                            ReadExact(_activeStream, ddDropBuf, 0, 8);
                            float ddNormX = BitConverter.ToSingle(ddDropBuf, 0);
                            float ddNormY = BitConverter.ToSingle(ddDropBuf, 4);
                            _isCrossScreenDragActive = false;
                            Log("📂 Dragged file dropped on this machine!");
                            if (OnDragDropped != null) OnDragDropped(ddNormX, ddNormY);
                            break;

                        case PacketType.DragCancel:
                            _isCrossScreenDragActive = false;
                            if (!string.IsNullOrEmpty(_stagedReceivedFilePath) && File.Exists(_stagedReceivedFilePath))
                            {
                                try { File.Delete(_stagedReceivedFilePath); } catch { }
                                _stagedReceivedFilePath = null;
                                _stagedReceivedFileName = null;
                            }
                            if (OnDragCancelled != null) OnDragCancelled();
                            break;

                        case PacketType.RequestReverseDrag:
                            HandleReverseDragRequest();
                            break;
                    }
                }
                catch
                {
                    break;
                }
            }

            lock (_streamLock)
            {
                if (_activeClient != null)
                {
                    try { _activeClient.Close(); } catch { }
                    _activeClient = null;
                    _activeStream = null;
                }
            }

            Log("Host disconnected");
            if (OnConnectionStatusChanged != null) OnConnectionStatusChanged(false);
        }

        private void UdpMotionLoop()
        {
            try
            {
                int taskIndex = 0;
                NativeMethods.AvSetMmThreadCharacteristics("Games", ref taskIndex);
            }
            catch { }

            NativeMethods.TimeBeginPeriod(1);
            try
            {
                uint curRes;
                NativeMethods.NtSetTimerResolution(5000, true, out curRes); // 0.5ms precision!
            }
            catch { }

            byte[] recvBuf = new byte[128];
            EndPoint senderEp = new IPEndPoint(IPAddress.Any, 0);

            while (_isRunning)
            {
                try
                {
                    // Direct zero-heap-allocation socket receive (eliminates Gen-0 GC pauses & stutter)
                    int received = _udpReceiver.Client.ReceiveFrom(recvBuf, ref senderEp);
                    if (received >= Protocol.LEGACY_MOTION_PACKET_SIZE)
                    {
                        ProcessMouseMovePacket(recvBuf, received);
                    }
                }
                catch (SocketException)
                {
                    if (!_isRunning) break;
                    Thread.SpinWait(50);
                }
                catch (Exception)
                {
                    if (!_isRunning) break;
                }
            }

            NativeMethods.TimeEndPeriod(1);
        }

        private void ProcessMouseMovePacket(byte[] data, int length)
        {
            lock (_cursorSyncLock)
            {
                if (length >= Protocol.MOTION_PACKET_SIZE && data[0] == Protocol.MAGIC_BYTE)
                {
                    // High-Performance Redundant Multi-Delta Datagram (37 bytes)
                    uint seq;
                    long qpc;
                    float normX, normY;
                    short curDx, curDy, hist1Dx, hist1Dy, hist2Dx, hist2Dy, wheel;
                    byte buttons;

                    Protocol.DeserializeMotionPacket(
                        data,
                        out seq,
                        out qpc,
                        out normX,
                        out normY,
                        out curDx,
                        out curDy,
                        out hist1Dx,
                        out hist1Dy,
                        out hist2Dx,
                        out hist2Dy,
                        out wheel,
                        out buttons
                    );

                    // Discard duplicate or stale packets (out-of-order network arrival)
                    if (_lastProcessedSeq != 0 && (int)(seq - _lastProcessedSeq) <= 0)
                    {
                        return;
                    }

                    int gap = (_lastProcessedSeq != 0) ? (int)(seq - _lastProcessedSeq) : 1;
                    _lastProcessedSeq = seq;

                    // Initial crossover placement (delta == 0 signals entry coordinate)
                    if (curDx == 0 && curDy == 0 && hist1Dx == 0 && hist1Dy == 0)
                    {
                        SetInitialCursorPlacement(normX, normY);
                        return;
                    }

                    // ZERO-RETRANSMIT REDUNDANCY RECOVERY:
                    if (gap == 2)
                    {
                        // 1 packet was dropped by Wi-Fi! Apply hist1 first, then curDx
                        ApplyMotionWithFilter(hist1Dx, hist1Dy);
                        ApplyMotionWithFilter(curDx, curDy);
                    }
                    else if (gap == 3)
                    {
                        // 2 packets were dropped! Apply hist2, hist1, then curDx
                        ApplyMotionWithFilter(hist2Dx, hist2Dy);
                        ApplyMotionWithFilter(hist1Dx, hist1Dy);
                        ApplyMotionWithFilter(curDx, curDy);
                    }
                    else
                    {
                        // Sequential packet
                        ApplyMotionWithFilter(curDx, curDy);
                    }

                    if (wheel != 0)
                    {
                        InjectMouseWheel(wheel);
                    }
                }
                else if (length >= Protocol.LEGACY_MOTION_PACKET_SIZE && data[0] == (byte)PacketType.MouseMove)
                {
                    // Legacy 21-byte backward-compatibility packet
                    uint seq;
                    float normX, normY;
                    int deltaX, deltaY;
                    Protocol.DeserializeMouseMove(data, out seq, out normX, out normY, out deltaX, out deltaY);

                    if (_lastProcessedSeq != 0 && (int)(seq - _lastProcessedSeq) <= 0)
                    {
                        return;
                    }
                    _lastProcessedSeq = seq;

                    if (deltaX == 0 && deltaY == 0)
                    {
                        SetInitialCursorPlacement(normX, normY);
                        return;
                    }

                    ApplyMotionWithFilter(deltaX, deltaY);
                }
            }
        }

        private void SetInitialCursorPlacement(float normX, float normY)
        {
            _clientCursorX = (int)Math.Round(normX * (ScreenWidth - 1));
            _clientCursorY = (int)Math.Round(normY * (ScreenHeight - 1));
            ClampCursor();

            NativeMethods.SetCursorPos(_clientCursorX, _clientCursorY);
            _enterTime = DateTime.Now;
            _returnPushCount = 0;
            _filteredDx = 0f;
            _filteredDy = 0f;
            _subPixelRemX = 0f;
            _subPixelRemY = 0f;
        }

        private void ApplyMotionWithFilter(int rawDx, int rawDy)
        {
            // Case 1: Mouse stopped - flush residual sub-pixels instantly and reset filter
            if (rawDx == 0 && rawDy == 0)
            {
                _filteredDx = 0f;
                _filteredDy = 0f;
                int remX = (int)Math.Round(_subPixelRemX);
                int remY = (int)Math.Round(_subPixelRemY);
                _subPixelRemX = 0f;
                _subPixelRemY = 0f;

                if (remX != 0 || remY != 0)
                {
                    _moveInputs[0].u.mi.dx = remX;
                    _moveInputs[0].u.mi.dy = remY;
                    NativeMethods.SendInput(1, _moveInputs, _inputSize);
                    _clientCursorX += remX;
                    _clientCursorY += remY;
                    ClampCursor();
                }
                return;
            }

            // Ensure physical hardware cursor coordinates initialized
            if (_clientCursorX < 0 || _clientCursorY < 0)
            {
                NativeMethods.POINT curPt;
                if (NativeMethods.GetCursorPos(out curPt))
                {
                    _clientCursorX = curPt.X;
                    _clientCursorY = curPt.Y;
                }
                else
                {
                    _clientCursorX = ScreenWidth / 2;
                    _clientCursorY = ScreenHeight / 2;
                }
            }

            // Case 2: Adaptive Sub-Millisecond Micro-EMA Filter (Display Refresh Sync)
            // - Fast flicks / high velocity (speed >= 8.0) -> alpha = 1.0 (pure 1:1 raw pass-through, zero lag!)
            // - Smooth gliding (speed < 8.0) -> alpha = 0.72 - 1.00 (eliminates Wi-Fi micro-jitter variance into glass-smooth motion)
            float speed = (float)Math.Sqrt(rawDx * rawDx + rawDy * rawDy);
            float alpha = 1.0f;
            if (speed < 8.0f)
            {
                float t = speed / 8.0f;
                alpha = 0.72f + (0.28f * t);
            }

            _filteredDx = (alpha * rawDx) + ((1.0f - alpha) * _filteredDx);
            _filteredDy = (alpha * rawDy) + ((1.0f - alpha) * _filteredDy);

            float totalX = _filteredDx + _subPixelRemX;
            float totalY = _filteredDy + _subPixelRemY;

            int injectDx = (int)Math.Round(totalX);
            int injectDy = (int)Math.Round(totalY);

            _subPixelRemX = totalX - injectDx;
            _subPixelRemY = totalY - injectDy;

            if (injectDx != 0 || injectDy != 0)
            {
                _moveInputs[0].u.mi.dx = injectDx;
                _moveInputs[0].u.mi.dy = injectDy;
                NativeMethods.SendInput(1, _moveInputs, _inputSize);

                _clientCursorX += injectDx;
                _clientCursorY += injectDy;
                ClampCursor();
            }

            if (_isCrossScreenDragActive && OnDragMoved != null)
            {
                OnDragMoved((float)_clientCursorX / (float)ScreenWidth, (float)_clientCursorY / (float)ScreenHeight);
            }

            CheckReturnEdge(_clientCursorX, _clientCursorY, rawDx, rawDy);
        }

        private void ClampCursor()
        {
            if (_clientCursorX < 0) _clientCursorX = 0;
            if (_clientCursorX >= ScreenWidth) _clientCursorX = ScreenWidth - 1;
            if (_clientCursorY < 0) _clientCursorY = 0;
            if (_clientCursorY >= ScreenHeight) _clientCursorY = ScreenHeight - 1;
        }

        private void CheckReturnEdge(int x, int y, int deltaX, int deltaY)
        {
            // Debounce: MUST be on client for at least 1.0 seconds before returning
            if ((DateTime.Now - _enterTime).TotalMilliseconds < 1000)
            {
                return;
            }

            bool pushingTowardsEdge = false;

            switch (HostPositionRelativeToClient)
            {
                case ScreenEdge.Left:
                    // Host is on Left: to return, cursor must be at left boundary (x <= 1) AND moving left!
                    if (x <= 1 && deltaX < 0) pushingTowardsEdge = true;
                    break;
                case ScreenEdge.Right:
                    // Host is on Right: cursor must be at right boundary AND moving right!
                    if (x >= ScreenWidth - 2 && deltaX > 0) pushingTowardsEdge = true;
                    break;
                case ScreenEdge.Top:
                    if (y <= 1 && deltaY < 0) pushingTowardsEdge = true;
                    break;
                case ScreenEdge.Bottom:
                    if (y >= ScreenHeight - 2 && deltaY > 0) pushingTowardsEdge = true;
                    break;
            }

            if (pushingTowardsEdge)
            {
                _returnPushCount++;
                // Must actively push for 8 frames into the border to return
                if (_returnPushCount >= 8)
                {
                    _returnPushCount = 0;
                    float returnNormY = (float)y / (float)ScreenHeight;

                    // Reverse Drag & Drop: Check if user is dragging a file from Client back to Host!
                    bool isLButtonHeld = _isLeftButtonDown || ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0);
                    if (isLButtonHeld)
                    {
                        ThreadPool.QueueUserWorkItem(s => HandleReverseDragRequest());
                    }

                    SendSwitchBack(returnNormY);
                }
            }
            else
            {
                // Moving away from edge resets push counter immediately
                _returnPushCount = 0;
            }
        }

        public void SendDragStartToHost(string fileName, long fileSize)
        {
            try
            {
                byte[] packet = Protocol.SerializeDragStart(fileName, fileSize);
                lock (_streamLock)
                {
                    if (_activeStream != null)
                    {
                        _activeStream.Write(packet, 0, packet.Length);
                    }
                }
            }
            catch { }
        }

        public void SendSwitchBack(float normY)
        {
            try
            {
                // Guarantee mouse button is released on client whenever control switches back
                InjectMouseButton(MouseButtonType.Left, ButtonState.Up);

                byte[] packet = Protocol.SerializeSwitchBack(normY);
                lock (_streamLock)
                {
                    if (_activeStream != null)
                    {
                        _activeStream.Write(packet, 0, packet.Length);
                    }
                }
                Log("Sent SwitchBack to Host");
            }
            catch { }
        }

        private void HandleReverseDragRequest()
        {
            try
            {
                // 1. First extract selected file(s) - check pre-captured files first!
                List<string> selectedFiles = _lastClientSelectedFiles;
                if (selectedFiles == null || selectedFiles.Count == 0)
                {
                    selectedFiles = ShellHelper.GetSelectedExplorerFiles();
                }

                // 2. Break Windows Explorer drag lock immediately so file NEVER sticks at the screen border!
                InjectMouseButton(MouseButtonType.Left, ButtonState.Up);
                // Also send Escape key to smoothly cancel modal OLE drag loop in Windows Explorer
                InjectKeyEvent((ushort)27, 0, 0, true);
                Thread.Sleep(5);
                InjectKeyEvent((ushort)27, 0, 0, false);

                // 3. If file found, send DragStart to Host and stream the file!
                if (selectedFiles != null && selectedFiles.Count > 0)
                {
                    string draggedPath = selectedFiles[0];
                    if (File.Exists(draggedPath))
                    {
                        FileInfo fi = new FileInfo(draggedPath);
                        Log(string.Format("🚀 Beaming reverse-dragged file to Host: {0} ({1:F1} MB)", fi.Name, fi.Length / (1024.0 * 1024.0)));

                        SendDragStartToHost(fi.Name, fi.Length);
                        SendFileToHost(draggedPath, null);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Reverse drag error: " + ex.Message);
            }
        }

        public void SendClipboardToHost(string text)
        {
            try
            {
                byte[] packet = Protocol.SerializeClipboard(text);
                lock (_streamLock)
                {
                    if (_activeStream != null)
                    {
                        _activeStream.Write(packet, 0, packet.Length);
                    }
                }
            }
            catch { }
        }

        public void SendImageClipboardToHost(byte[] pngBytes)
        {
            try
            {
                byte[] packet = Protocol.SerializeImageClipboard(pngBytes);
                lock (_streamLock)
                {
                    if (_activeStream != null)
                    {
                        _activeStream.Write(packet, 0, packet.Length);
                    }
                }
            }
            catch { }
        }

        public void SendFileToHost(string filePath, Action<int> progressCallback)
        {
            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    if (!File.Exists(filePath)) return;
                    FileInfo fi = new FileInfo(filePath);
                    long totalBytes = fi.Length;
                    string fileName = fi.Name;

                    lock (_streamLock)
                    {
                        if (_activeStream == null) return;

                        // 1. Send Header
                        byte[] header = Protocol.SerializeFileHeader(fileName, totalBytes);
                        _activeStream.Write(header, 0, header.Length);

                        // 2. Stream Chunks (64KB blocks)
                        byte[] buffer = new byte[65536];
                        long sentBytes = 0;

                        using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            int read;
                            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                byte[] chunk = Protocol.SerializeFileChunk(buffer, 0, read);
                                _activeStream.Write(chunk, 0, chunk.Length);
                                sentBytes += read;

                                int pct = (int)((sentBytes * 100) / totalBytes);
                                if (progressCallback != null) progressCallback(pct);
                            }
                        }

                        // 3. Send Complete
                        byte[] complete = Protocol.SerializeFileComplete(fileName);
                        _activeStream.Write(complete, 0, complete.Length);
                    }

                    Log("📁 File sent to Host: " + fileName);
                }
                catch (Exception ex)
                {
                    Log("File send error: " + ex.Message);
                }
            });
        }

        public string PreferredDropFolder { get; set; }

        private void StartReceivingFile(string fileName, long fileSize)
        {
            try
            {
                string folder = !string.IsNullOrEmpty(PreferredDropFolder) && Directory.Exists(PreferredDropFolder)
                    ? PreferredDropFolder
                    : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                string safeName = Path.GetFileName(fileName);
                string destPath = Path.Combine(folder, safeName);

                if (File.Exists(destPath))
                {
                    string nameNoExt = Path.GetFileNameWithoutExtension(safeName);
                    string ext = Path.GetExtension(safeName);
                    destPath = Path.Combine(folder, nameNoExt + "_" + DateTime.Now.ToString("HHmmss") + ext);
                }

                _receivingFileName = safeName;
                _receivingFileSize = fileSize;
                _receivingBytesReceived = 0;
                _receivingFileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

                Log("Receiving incoming file: " + safeName + " -> " + Path.GetFileName(folder));
            }
            catch (Exception ex)
            {
                Log("File receive start error: " + ex.Message);
            }
        }

        private void WriteReceivingFileChunk(byte[] chunk, int count)
        {
            try
            {
                if (_receivingFileStream != null)
                {
                    _receivingFileStream.Write(chunk, 0, count);
                    _receivingBytesReceived += count;
                }
            }
            catch (Exception ex)
            {
                Log("File chunk write error: " + ex.Message);
            }
        }

        private void FinishReceivingFile(string fileName)
        {
            try
            {
                string savedPath = null;
                if (_receivingFileStream != null)
                {
                    savedPath = _receivingFileStream.Name;
                    _receivingFileStream.Flush();
                    _receivingFileStream.Close();
                    _receivingFileStream = null;
                }

                if (_isCrossScreenDragActive)
                {
                    // User is still dragging across the screen! Hold file in staging until user drops!
                    _stagedReceivedFilePath = savedPath;
                    _stagedReceivedFileName = fileName;
                    Log("File pre-buffered and ready for drop: " + fileName);
                    return;
                }

                // If drag already finished or file came from direct beam:
                if (!string.IsNullOrEmpty(PreferredDropFolder) && Directory.Exists(PreferredDropFolder) && savedPath != null)
                {
                    string targetFolder = PreferredDropFolder;
                    string currentFolder = Path.GetDirectoryName(savedPath);
                    if (!string.Equals(targetFolder, currentFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string targetPath = Path.Combine(targetFolder, Path.GetFileName(savedPath));
                            if (File.Exists(targetPath))
                            {
                                string nameNoExt = Path.GetFileNameWithoutExtension(targetPath);
                                string ext = Path.GetExtension(targetPath);
                                targetPath = Path.Combine(targetFolder, nameNoExt + "_" + DateTime.Now.ToString("HHmmss") + ext);
                            }
                            File.Move(savedPath, targetPath);
                            savedPath = targetPath;
                        }
                        catch { }
                    }
                }

                // Refresh the target folder so the file appears on Desktop or in the folder immediately!
                if (savedPath != null)
                {
                    ShellHelper.NotifyShellFolderUpdated(Path.GetDirectoryName(savedPath));
                }

                Log("✅ File placed at: " + savedPath);
                if (OnFileReceived != null && savedPath != null)
                {
                    OnFileReceived(fileName, savedPath);
                }
            }
            catch (Exception ex)
            {
                Log("File complete error: " + ex.Message);
            }
        }

        public string FinalizeDragDrop(string targetFolder)
        {
            _isCrossScreenDragActive = false;
            PreferredDropFolder = targetFolder;
            string finalPath = null;

            if (!string.IsNullOrEmpty(_stagedReceivedFilePath) && File.Exists(_stagedReceivedFilePath))
            {
                try
                {
                    if (!Directory.Exists(targetFolder))
                    {
                        Directory.CreateDirectory(targetFolder);
                    }
                    string dest = Path.Combine(targetFolder, Path.GetFileName(_stagedReceivedFilePath));
                    if (File.Exists(dest) && !string.Equals(_stagedReceivedFilePath, dest, StringComparison.OrdinalIgnoreCase))
                    {
                        string nameNoExt = Path.GetFileNameWithoutExtension(dest);
                        string ext = Path.GetExtension(dest);
                        dest = Path.Combine(targetFolder, nameNoExt + "_" + DateTime.Now.ToString("HHmmss") + ext);
                    }

                    if (!string.Equals(_stagedReceivedFilePath, dest, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Move(_stagedReceivedFilePath, dest);
                    }
                    finalPath = dest;
                    ShellHelper.NotifyShellFolderUpdated(targetFolder);

                    if (OnFileReceived != null)
                    {
                        OnFileReceived(_stagedReceivedFileName ?? Path.GetFileName(finalPath), finalPath);
                    }
                }
                catch (Exception ex)
                {
                    Log("Drop finalize error: " + ex.Message);
                }
                _stagedReceivedFilePath = null;
                _stagedReceivedFileName = null;
            }

            return finalPath;
        }

        public static string GetReceivedFilesFolder()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        private void InjectMouseButton(MouseButtonType button, ButtonState state)
        {
            uint flag = 0;
            if (button == MouseButtonType.Left)
            {
                _isLeftButtonDown = (state == ButtonState.Down);
                flag = (state == ButtonState.Down) ? NativeMethods.MOUSEEVENTF_LEFTDOWN : NativeMethods.MOUSEEVENTF_LEFTUP;
            }
            else if (button == MouseButtonType.Right)
                flag = (state == ButtonState.Down) ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MOUSEEVENTF_RIGHTUP;
            else if (button == MouseButtonType.Middle)
                flag = (state == ButtonState.Down) ? NativeMethods.MOUSEEVENTF_MIDDLEDOWN : NativeMethods.MOUSEEVENTF_MIDDLEUP;

            if (flag != 0)
            {
                NativeMethods.INPUT[] inputs = new NativeMethods.INPUT[1];
                inputs[0].type = NativeMethods.INPUT_MOUSE;
                inputs[0].u.mi.dwFlags = flag;
                NativeMethods.SendInput(1, inputs, _inputSize);
            }
        }

        private void InjectMouseWheel(int delta)
        {
            NativeMethods.INPUT[] inputs = new NativeMethods.INPUT[1];
            inputs[0].type = NativeMethods.INPUT_MOUSE;
            inputs[0].u.mi.dwFlags = NativeMethods.MOUSEEVENTF_WHEEL;
            inputs[0].u.mi.mouseData = (uint)delta;
            NativeMethods.SendInput(1, inputs, _inputSize);
        }

        private void InjectKeyEvent(ushort vkCode, ushort scanCode, uint flags, bool isDown)
        {
            NativeMethods.INPUT[] inputs = new NativeMethods.INPUT[1];
            inputs[0].type = NativeMethods.INPUT_KEYBOARD;
            inputs[0].u.ki.wVk = vkCode;
            inputs[0].u.ki.wScan = scanCode;
            uint keyFlags = 0;
            if (!isDown) keyFlags |= NativeMethods.KEYEVENTF_KEYUP;
            if ((flags & 1) != 0) keyFlags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
            inputs[0].u.ki.dwFlags = keyFlags;

            NativeMethods.SendInput(1, inputs, _inputSize);
        }

        private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read <= 0) throw new IOException("Stream closed prematurely");
                totalRead += read;
            }
        }

        private void Log(string msg)
        {
            if (OnLogMessage != null) OnLogMessage(msg);
        }

        public void Stop()
        {
            _isRunning = false;
            try
            {
                if (_tcpListener != null) _tcpListener.Stop();
                if (_activeClient != null) _activeClient.Close();
                if (_udpReceiver != null) _udpReceiver.Close();
                if (_receivingFileStream != null)
                {
                    _receivingFileStream.Close();
                    _receivingFileStream = null;
                }
                NativeMethods.TimeEndPeriod(1);
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
