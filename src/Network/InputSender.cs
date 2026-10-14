using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using ButterFlow.Core;

namespace ButterFlow.Network
{
    public class InputSender : IDisposable
    {
        private TcpClient _tcpClient;
        private NetworkStream _tcpStream;
        private UdpClient _udpSender;
        private IPEndPoint _udpTarget;
        private Thread _receiveThread;
        private Thread _pingThread;
        private Thread _tcpMotionThread;
        private bool _isConnected;
        private readonly object _lock = new object();

        public string TargetIp { get; private set; }
        public bool IsConnected { get { return _isConnected; } }

        public event Action<float> OnSwitchBack;
        public event Action<string> OnRemoteClipboard;
        public event Action<byte[]> OnRemoteImageClipboard;
        public event Action<string, string> OnFileReceived;
        public event Action<string, long> OnRemoteDragStarted;
        public event Action<double> OnLatencyMeasured;
        public event Action OnDisconnected;

        private uint _sequenceCounter = 0;
        private readonly byte[] _motionBuffer = new byte[Protocol.MOTION_PACKET_SIZE];
        private short _hist1Dx = 0, _hist1Dy = 0;
        private short _hist2Dx = 0, _hist2Dy = 0;

        // Zero-lag TCP motion worker (prevents blocking Win32 Mouse Hook!)
        private readonly ConcurrentQueue<byte[]> _tcpMotionQueue = new ConcurrentQueue<byte[]>();
        private readonly AutoResetEvent _tcpMotionSignal = new AutoResetEvent(false);

        // File transfer receiving state
        private FileStream _receivingFileStream = null;
        private string _receivingFileName = null;
        private long _receivingFileSize = 0;
        private long _receivingBytesReceived = 0;

        public bool Connect(string targetIp)
        {
            Disconnect();
            TargetIp = targetIp;

            try
            {
                _tcpClient = new TcpClient();
                _tcpClient.NoDelay = true; // Disable Nagle's algorithm for sub-millisecond response!
                _tcpClient.SendBufferSize = 262144; // 256KB send buffer
                _tcpClient.ReceiveBufferSize = 262144; // 256KB receive buffer

                // QoS VIP Priority (DSCP 46 = Expedited Forwarding for sub-1ms Wi-Fi/router bypass)
                try
                {
                    _tcpClient.Client.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)3, 0xB8);
                }
                catch { }

                IAsyncResult ar = _tcpClient.BeginConnect(targetIp, Protocol.TCP_CONTROL_PORT, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(3500, true))
                {
                    _tcpClient.Close();
                    return false;
                }
                _tcpClient.EndConnect(ar);

                _tcpStream = _tcpClient.GetStream();

                // Send Handshake with our MachineName so Client knows who connected
                try
                {
                    string myMachine = Environment.MachineName;
                    byte[] nameBytes = Encoding.UTF8.GetBytes(myMachine);
                    byte[] handshake = new byte[1 + 4 + nameBytes.Length];
                    handshake[0] = (byte)PacketType.HandshakeRequest;
                    BitConverter.GetBytes(nameBytes.Length).CopyTo(handshake, 1);
                    Array.Copy(nameBytes, 0, handshake, 5, nameBytes.Length);
                    _tcpStream.Write(handshake, 0, handshake.Length);
                }
                catch { }

                _udpSender = new UdpClient();
                _udpSender.Client.SendBufferSize = 262144; // 256KB send buffer for zero drops
                try
                {
                    _udpSender.Client.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)3, 0xB8);
                }
                catch { }

                _udpTarget = new IPEndPoint(IPAddress.Parse(targetIp), Protocol.UDP_MOTION_PORT);

                _isConnected = true;

                _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "SenderTcpReceiver" };
                _receiveThread.Start();

                _pingThread = new Thread(PingLoop) { IsBackground = true, Name = "SenderPing" };
                _pingThread.Start();

                _tcpMotionThread = new Thread(TcpMotionWorker) { IsBackground = true, Name = "SenderTcpMotion" };
                _tcpMotionThread.Priority = ThreadPriority.AboveNormal;
                _tcpMotionThread.Start();

                return true;
            }
            catch (Exception ex)
            {
                Disconnect();
                Debug.WriteLine("Connect error: " + ex.Message);
                return false;
            }
        }

        public void SendMouseMove(float normX, float normY, int deltaX, int deltaY, short wheel = 0, byte buttons = 0)
        {
            if (!_isConnected || _udpSender == null || _udpTarget == null) return;
            try
            {
                lock (_motionBuffer)
                {
                    _sequenceCounter++;
                    long qpc;
                    NativeMethods.QueryPerformanceCounter(out qpc);

                    short sDx = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, deltaX));
                    short sDy = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, deltaY));

                    Protocol.SerializeMotionPacketDirect(
                        _motionBuffer,
                        _sequenceCounter,
                        qpc,
                        normX,
                        normY,
                        sDx,
                        sDy,
                        _hist1Dx,
                        _hist1Dy,
                        _hist2Dx,
                        _hist2Dy,
                        wheel,
                        buttons
                    );

                    // Shift historical deltas for zero-retransmit recovery on Wi-Fi packet drops
                    _hist2Dx = _hist1Dx;
                    _hist2Dy = _hist1Dy;
                    _hist1Dx = sDx;
                    _hist1Dy = sDy;

                    _udpSender.Client.SendTo(_motionBuffer, 0, Protocol.MOTION_PACKET_SIZE, SocketFlags.None, _udpTarget);
                }
            }
            catch { }
        }

        private void TcpMotionWorker()
        {
            while (_isConnected)
            {
                _tcpMotionSignal.WaitOne(50);
                if (!_isConnected) break;

                byte[] packet;
                while (_tcpMotionQueue.TryDequeue(out packet))
                {
                    try
                    {
                        if (_tcpStream != null)
                        {
                            lock (_lock)
                            {
                                _tcpStream.Write(packet, 0, packet.Length);
                            }
                        }
                    }
                    catch { break; }
                }
            }
        }

        public void SendMouseButton(MouseButtonType button, ButtonState state)
        {
            if (!_isConnected || _tcpStream == null) return;
            try
            {
                byte[] packet = Protocol.SerializeMouseButton(button, state);
                lock (_lock)
                {
                    _tcpStream.Write(packet, 0, packet.Length);
                }
            }
            catch { Disconnect(); }
        }

        public void SendMouseWheel(int delta)
        {
            if (!_isConnected || _tcpStream == null) return;
            try
            {
                byte[] packet = Protocol.SerializeMouseWheel(delta);
                lock (_lock)
                {
                    _tcpStream.Write(packet, 0, packet.Length);
                }
            }
            catch { Disconnect(); }
        }

        public void SendKeyEvent(ushort vkCode, ushort scanCode, uint flags, bool isKeyDown)
        {
            if (!_isConnected || _tcpStream == null) return;
            try
            {
                byte[] packet = Protocol.SerializeKeyEvent(vkCode, scanCode, flags, isKeyDown);
                lock (_lock)
                {
                    _tcpStream.Write(packet, 0, packet.Length);
                }
            }
            catch { Disconnect(); }
        }

        public void SendClipboard(string text)
        {
            if (!_isConnected || _tcpStream == null) return;
            try
            {
                byte[] packet = Protocol.SerializeClipboard(text);
                lock (_lock)
                {
                    _tcpStream.Write(packet, 0, packet.Length);
                }
            }
            catch { Disconnect(); }
        }

        public void SendImageClipboard(byte[] pngBytes)
        {
            if (!_isConnected || _tcpStream == null || pngBytes == null || pngBytes.Length == 0) return;
            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    byte[] packet = Protocol.SerializeImageClipboard(pngBytes);
                    lock (_lock)
                    {
                        if (_tcpStream != null)
                        {
                            _tcpStream.Write(packet, 0, packet.Length);
                        }
                    }
                }
                catch { Disconnect(); }
            });
        }

        public void SendFile(string filePath, Action<int> onProgress)
        {
            if (!_isConnected || !File.Exists(filePath)) return;
            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    FileInfo fi = new FileInfo(filePath);
                    long totalBytes = fi.Length;
                    string fileName = fi.Name;

                    lock (_lock)
                    {
                        if (_tcpStream == null) return;

                        // 1. Send Header
                        byte[] header = Protocol.SerializeFileHeader(fileName, totalBytes);
                        _tcpStream.Write(header, 0, header.Length);

                        // 2. Stream in 64KB chunks
                        byte[] buffer = new byte[65536];
                        long sentBytes = 0;

                        using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            int read;
                            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                byte[] chunk = Protocol.SerializeFileChunk(buffer, 0, read);
                                _tcpStream.Write(chunk, 0, chunk.Length);
                                sentBytes += read;

                                int pct = totalBytes > 0 ? (int)((sentBytes * 100) / totalBytes) : 100;
                                if (onProgress != null) onProgress(pct);
                            }
                        }

                        // 3. Send Complete
                        byte[] complete = Protocol.SerializeFileComplete(fileName);
                        _tcpStream.Write(complete, 0, complete.Length);
                    }

                    if (onProgress != null) onProgress(100);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("SendFile error: " + ex.Message);
                }
            });
        }

        public void SendDragStart(string fileName, long fileSize)
        {
            if (!_isConnected || _tcpStream == null) return;
            try
            {
                byte[] packet = Protocol.SerializeDragStart(fileName, fileSize);
                lock (_lock)
                {
                    _tcpStream.Write(packet, 0, packet.Length);
                }
            }
            catch { }
        }

        public void SendDragMotion(float normX, float normY)
        {
            if (!_isConnected || _tcpStream == null) return;
            try
            {
                byte[] packet = Protocol.SerializeDragMotion(normX, normY);
                lock (_lock)
                {
                    _tcpStream.Write(packet, 0, packet.Length);
                }
            }
            catch { }
        }

        public void SendDragDrop(float dropX, float dropY)
        {
            if (!_isConnected || _tcpStream == null) return;
            try
            {
                byte[] packet = Protocol.SerializeDragDrop(dropX, dropY);
                lock (_lock)
                {
                    _tcpStream.Write(packet, 0, packet.Length);
                }
            }
            catch { }
        }

        public void SendDragCancel()
        {
            if (!_isConnected || _tcpStream == null) return;
            try
            {
                byte[] packet = Protocol.SerializeDragCancel();
                lock (_lock)
                {
                    _tcpStream.Write(packet, 0, packet.Length);
                }
            }
            catch { }
        }

        public void SendRequestReverseDrag()
        {
            if (!_isConnected || _tcpStream == null) return;
            try
            {
                byte[] packet = Protocol.SerializeRequestReverseDrag();
                lock (_lock)
                {
                    _tcpStream.Write(packet, 0, packet.Length);
                }
            }
            catch { }
        }

        private void PingLoop()
        {
            while (_isConnected)
            {
                try
                {
                    long startTicks = Stopwatch.GetTimestamp();
                    byte[] pingPacket = Protocol.SerializePing(startTicks);
                    lock (_lock)
                    {
                        if (_tcpStream != null)
                            _tcpStream.Write(pingPacket, 0, pingPacket.Length);
                    }
                }
                catch { break; }

                Thread.Sleep(1000);
            }
        }

        private void ReceiveLoop()
        {
            byte[] header = new byte[1];
            while (_isConnected)
            {
                try
                {
                    int read = _tcpStream.Read(header, 0, 1);
                    if (read <= 0) break;

                    PacketType type = (PacketType)header[0];
                    if (type == PacketType.SwitchBack)
                    {
                        byte[] body = new byte[4];
                        ReadExact(_tcpStream, body, 0, 4);
                        byte[] full = new byte[5];
                        full[0] = (byte)PacketType.SwitchBack;
                        Array.Copy(body, 0, full, 1, 4);
                        float normY = Protocol.DeserializeSwitchBack(full);

                        if (OnSwitchBack != null)
                        {
                            OnSwitchBack(normY);
                        }
                    }
                    else if (type == PacketType.Pong)
                    {
                        byte[] body = new byte[8];
                        ReadExact(_tcpStream, body, 0, 8);
                        byte[] full = new byte[9];
                        full[0] = (byte)PacketType.Pong;
                        Array.Copy(body, 0, full, 1, 8);
                        long sentTicks = Protocol.DeserializeTimestamp(full);
                        long currentTicks = Stopwatch.GetTimestamp();
                        double elapsedMs = (double)(currentTicks - sentTicks) * 1000.0 / Stopwatch.Frequency;

                        if (OnLatencyMeasured != null)
                        {
                            OnLatencyMeasured(elapsedMs);
                        }
                    }
                    else if (type == PacketType.ClipboardSync)
                    {
                        byte[] lenBuf = new byte[4];
                        ReadExact(_tcpStream, lenBuf, 0, 4);
                        int len = BitConverter.ToInt32(lenBuf, 0);
                        if (len > 0 && len < 10000000)
                        {
                            byte[] strBuf = new byte[len];
                            ReadExact(_tcpStream, strBuf, 0, len);
                            byte[] full = new byte[5 + len];
                            full[0] = (byte)PacketType.ClipboardSync;
                            Array.Copy(lenBuf, 0, full, 1, 4);
                            Array.Copy(strBuf, 0, full, 5, len);
                            string text = Protocol.DeserializeClipboard(full);

                            if (OnRemoteClipboard != null)
                            {
                                OnRemoteClipboard(text);
                            }
                        }
                    }
                    else if (type == PacketType.ImageClipboardSync)
                    {
                        byte[] imgLenBuf = new byte[4];
                        ReadExact(_tcpStream, imgLenBuf, 0, 4);
                        int imgLen = BitConverter.ToInt32(imgLenBuf, 0);
                        if (imgLen > 0 && imgLen < 50000000)
                        {
                            byte[] pngBytes = new byte[imgLen];
                            ReadExact(_tcpStream, pngBytes, 0, imgLen);
                            if (OnRemoteImageClipboard != null)
                            {
                                OnRemoteImageClipboard(pngBytes);
                            }
                        }
                    }
                    else if (type == PacketType.FileTransferHeader)
                    {
                        byte[] fNameLenBuf = new byte[4];
                        ReadExact(_tcpStream, fNameLenBuf, 0, 4);
                        int fNameLen = BitConverter.ToInt32(fNameLenBuf, 0);
                        byte[] fNameBuf = new byte[fNameLen];
                        ReadExact(_tcpStream, fNameBuf, 0, fNameLen);
                        string incomingName = System.Text.Encoding.UTF8.GetString(fNameBuf);

                        byte[] fSizeBuf = new byte[8];
                        ReadExact(_tcpStream, fSizeBuf, 0, 8);
                        long incomingSize = BitConverter.ToInt64(fSizeBuf, 0);

                        StartReceivingFile(incomingName, incomingSize);
                    }
                    else if (type == PacketType.FileTransferChunk)
                    {
                        byte[] chunkLenBuf = new byte[4];
                        ReadExact(_tcpStream, chunkLenBuf, 0, 4);
                        int chunkCount = BitConverter.ToInt32(chunkLenBuf, 0);
                        byte[] chunkBytes = new byte[chunkCount];
                        ReadExact(_tcpStream, chunkBytes, 0, chunkCount);

                        WriteReceivingFileChunk(chunkBytes, chunkCount);
                    }
                    else if (type == PacketType.FileTransferComplete)
                    {
                        byte[] compLenBuf = new byte[4];
                        ReadExact(_tcpStream, compLenBuf, 0, 4);
                        int compNameLen = BitConverter.ToInt32(compLenBuf, 0);
                        byte[] compNameBuf = new byte[compNameLen];
                        ReadExact(_tcpStream, compNameBuf, 0, compNameLen);
                        string compName = System.Text.Encoding.UTF8.GetString(compNameBuf);

                        FinishReceivingFile(compName);
                    }
                    else if (type == PacketType.DragStart)
                    {
                        byte[] dsNameLenBuf = new byte[4];
                        ReadExact(_tcpStream, dsNameLenBuf, 0, 4);
                        int dsNameLen = BitConverter.ToInt32(dsNameLenBuf, 0);
                        byte[] dsNameBuf = new byte[dsNameLen];
                        ReadExact(_tcpStream, dsNameBuf, 0, dsNameLen);
                        string dsName = System.Text.Encoding.UTF8.GetString(dsNameBuf);

                        byte[] dsSizeBuf = new byte[8];
                        ReadExact(_tcpStream, dsSizeBuf, 0, 8);
                        long dsSize = BitConverter.ToInt64(dsSizeBuf, 0);

                        if (OnRemoteDragStarted != null)
                        {
                            OnRemoteDragStarted(dsName, dsSize);
                        }
                    }
                }
                catch
                {
                    break;
                }
            }

            Disconnect();
            if (OnDisconnected != null)
            {
                OnDisconnected();
            }
        }

        public string PreferredDropFolder { get; set; }
        public bool IsReverseDragActive { get; set; }
        private string _stagedReceivedFilePath = null;
        private string _stagedReceivedFileName = null;

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
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Sender file receive error: " + ex.Message);
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
            catch { }
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

                if (IsReverseDragActive)
                {
                    // User is still dragging across Host screen! Hold file in staging until user drops!
                    _stagedReceivedFilePath = savedPath;
                    _stagedReceivedFileName = fileName;
                    return;
                }

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

                if (savedPath != null)
                {
                    ShellHelper.NotifyShellFolderUpdated(Path.GetDirectoryName(savedPath));
                }

                if (OnFileReceived != null && savedPath != null)
                {
                    OnFileReceived(fileName, savedPath);
                }
            }
            catch { }
        }

        public string FinalizeReverseDrop(string targetFolder)
        {
            IsReverseDragActive = false;
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
                catch { }
                _stagedReceivedFilePath = null;
                _stagedReceivedFileName = null;
            }

            return finalPath;
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

        public void Disconnect()
        {
            _isConnected = false;
            try
            {
                if (_tcpMotionSignal != null) _tcpMotionSignal.Set();
                if (_tcpStream != null) _tcpStream.Close();
                if (_tcpClient != null) _tcpClient.Close();
                if (_udpSender != null) _udpSender.Close();
                if (_receivingFileStream != null)
                {
                    _receivingFileStream.Close();
                    _receivingFileStream = null;
                }
            }
            catch { }
            _tcpStream = null;
            _tcpClient = null;
            _udpSender = null;
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
