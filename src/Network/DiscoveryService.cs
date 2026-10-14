using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using ButterFlow.Core;

namespace ButterFlow.Network
{
    public class PeerInfo
    {
        public string MachineName { get; set; }
        public string IpAddress { get; set; }
        public AppMode Mode { get; set; }
        public int ScreenWidth { get; set; }
        public int ScreenHeight { get; set; }
        public DateTime LastSeen { get; set; }

        public override string ToString()
        {
            return string.Format("{0} ({1}) - {2} [{3}x{4}]", MachineName, IpAddress, Mode, ScreenWidth, ScreenHeight);
        }
    }

    public class DiscoveryService : IDisposable
    {
        private UdpClient _udpListener;
        private Thread _listenerThread;
        private Thread _beaconThread;
        private bool _isRunning;
        private readonly Dictionary<string, PeerInfo> _peers = new Dictionary<string, PeerInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();

        public event Action<PeerInfo> OnPeerFound;
        public event Action<string> OnPeerRemoved;

        public AppMode CurrentMode { get; set; }
        public int LocalScreenWidth { get; set; }
        public int LocalScreenHeight { get; set; }

        public DiscoveryService(AppMode initialMode, int screenW, int screenH)
        {
            CurrentMode = initialMode;
            LocalScreenWidth = screenW;
            LocalScreenHeight = screenH;
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;

            try
            {
                _udpListener = new UdpClient();
                _udpListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpListener.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.DISCOVERY_PORT));
                _udpListener.EnableBroadcast = true;

                // Disable WSAECONNRESET (10054) on UDP socket so ICMP errors never abort the listener
                try
                {
                    const int SIO_UDP_CONNRESET = -1744830452;
                    _udpListener.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, new byte[] { 0 });
                }
                catch { }

                _listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "DiscoveryListener" };
                _listenerThread.Start();

                _beaconThread = new Thread(BeaconLoop) { IsBackground = true, Name = "DiscoveryBeacon" };
                _beaconThread.Start();

                // Send immediate probe to get fast response from any running PC
                SendProbe();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Discovery start error: " + ex.Message);
            }
        }

        public void SendProbe()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    BroadcastMessage("BUTTERFLOW_PROBE|" + Environment.MachineName);
                    BroadcastBeacon();
                }
                catch { }
            });
        }

        private void BroadcastBeacon()
        {
            try
            {
                string localIp = GetPrimaryLocalIpAddress();
                // Format: BUTTERFLOW|MachineName|IP|Mode|Width|Height
                string msg = string.Format("BUTTERFLOW|{0}|{1}|{2}|{3}|{4}",
                    Environment.MachineName,
                    localIp,
                    CurrentMode.ToString(),
                    LocalScreenWidth,
                    LocalScreenHeight);

                BroadcastMessage(msg);
            }
            catch { }
        }

        private void BroadcastMessage(string message)
        {
            byte[] data = Encoding.UTF8.GetBytes(message);
            var targets = GetAllBroadcastTargets(Protocol.DISCOVERY_PORT);

            using (UdpClient broadcaster = new UdpClient())
            {
                broadcaster.EnableBroadcast = true;
                foreach (var ep in targets)
                {
                    try
                    {
                        broadcaster.Send(data, data.Length, ep);
                    }
                    catch { }
                }
            }
        }

        private void BeaconLoop()
        {
            while (_isRunning)
            {
                BroadcastBeacon();
                CleanExpiredPeers();
                Thread.Sleep(1500);
            }
        }

        private void ListenLoop()
        {
            IPEndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

            while (_isRunning)
            {
                try
                {
                    byte[] data = _udpListener.Receive(ref remoteEp);
                    string message = Encoding.UTF8.GetString(data);

                    if (message.StartsWith("BUTTERFLOW_PROBE|"))
                    {
                        string senderMachine = message.Substring("BUTTERFLOW_PROBE|".Length).Trim();
                        if (!string.Equals(senderMachine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                        {
                            // Respond with our beacon immediately
                            ThreadPool.QueueUserWorkItem(_ => BroadcastBeacon());
                        }
                        continue;
                    }

                    if (message.StartsWith("BUTTERFLOW|"))
                    {
                        string[] parts = message.Split('|');
                        if (parts.Length >= 6)
                        {
                            string machineName = parts[1];

                            // 1. Don't register self
                            if (string.Equals(machineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            // 2. Identify IP
                            string ip = remoteEp.Address.ToString();
                            if (remoteEp.Address.IsIPv4MappedToIPv6)
                            {
                                ip = remoteEp.Address.MapToIPv4().ToString();
                            }
                            if (IsLocalIpAddress(remoteEp.Address))
                            {
                                continue;
                            }

                            // If payload has a valid LAN IP, use payload IP if remoteEp is loopback
                            if (!string.IsNullOrEmpty(parts[2]) && parts[2] != "127.0.0.1" && (ip == "127.0.0.1" || ip == "::1"))
                            {
                                ip = parts[2];
                            }

                            AppMode mode = AppMode.Client;
                            try { mode = (AppMode)Enum.Parse(typeof(AppMode), parts[3], true); } catch { }

                            int w = 1920;
                            int h = 1080;
                            int.TryParse(parts[4], out w);
                            int.TryParse(parts[5], out h);

                            PeerInfo peer = new PeerInfo
                            {
                                MachineName = machineName,
                                IpAddress = ip,
                                Mode = mode,
                                ScreenWidth = w > 0 ? w : 1920,
                                ScreenHeight = h > 0 ? h : 1080,
                                LastSeen = DateTime.Now
                            };

                            lock (_lock)
                            {
                                _peers[peer.IpAddress] = peer;
                            }

                            if (OnPeerFound != null)
                            {
                                OnPeerFound(peer);
                            }
                        }
                    }
                }
                catch (SocketException)
                {
                    break;
                }
                catch { }
            }
        }

        private void CleanExpiredPeers()
        {
            List<string> expired = new List<string>();
            lock (_lock)
            {
                DateTime now = DateTime.Now;
                foreach (var kvp in _peers)
                {
                    // If no beacon in 8 seconds, peer is offline
                    if ((now - kvp.Value.LastSeen).TotalSeconds > 8)
                    {
                        expired.Add(kvp.Key);
                    }
                }
                foreach (string key in expired)
                {
                    _peers.Remove(key);
                }
            }

            if (OnPeerRemoved != null)
            {
                foreach (string key in expired)
                {
                    OnPeerRemoved(key);
                }
            }
        }

        public List<PeerInfo> GetActivePeers()
        {
            lock (_lock)
            {
                return new List<PeerInfo>(_peers.Values);
            }
        }

        public static string GetPrimaryLocalIpAddress()
        {
            try
            {
                var ips = GetAllLocalIPv4Addresses();
                if (ips.Count > 0)
                {
                    return ips[0].ToString();
                }
            }
            catch { }
            return "127.0.0.1";
        }

        public static bool IsLocalIpAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address)) return true;
            var locals = GetAllLocalIPv4Addresses();
            foreach (var local in locals)
            {
                if (local.Equals(address)) return true;
            }
            return false;
        }

        public static List<IPAddress> GetAllLocalIPv4Addresses()
        {
            List<IPAddress> list = new List<IPAddress>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    var ipProps = ni.GetIPProperties();
                    foreach (UnicastIPAddressInformation uni in ipProps.UnicastAddresses)
                    {
                        if (uni.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(uni.Address))
                        {
                            list.Add(uni.Address);
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        public static List<IPEndPoint> GetAllBroadcastTargets(int port)
        {
            List<IPEndPoint> targets = new List<IPEndPoint>();
            targets.Add(new IPEndPoint(IPAddress.Broadcast, port)); // 255.255.255.255

            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    var ipProps = ni.GetIPProperties();
                    foreach (UnicastIPAddressInformation uni in ipProps.UnicastAddresses)
                    {
                        if (uni.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(uni.Address))
                        {
                            if (uni.IPv4Mask != null)
                            {
                                byte[] ipBytes = uni.Address.GetAddressBytes();
                                byte[] maskBytes = uni.IPv4Mask.GetAddressBytes();
                                if (ipBytes.Length == 4 && maskBytes.Length == 4)
                                {
                                    byte[] bcastBytes = new byte[4];
                                    for (int i = 0; i < 4; i++)
                                    {
                                        bcastBytes[i] = (byte)(ipBytes[i] | (~maskBytes[i]));
                                    }
                                    targets.Add(new IPEndPoint(new IPAddress(bcastBytes), port));
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return targets;
        }

        public void Stop()
        {
            _isRunning = false;
            try
            {
                if (_udpListener != null) _udpListener.Close();
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
