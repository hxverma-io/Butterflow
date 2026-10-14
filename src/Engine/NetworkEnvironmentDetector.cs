using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;

namespace ButterFlow.Engine
{
    public sealed class NetworkAdapterInfo
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public NetworkInterfaceType InterfaceType { get; set; }
        public long SpeedBps { get; set; }
        public bool IsOptimal { get; set; }
        public string QualityRating { get; set; }
        public string Recommendation { get; set; }

        public override string ToString()
        {
            return string.Format("{0} [{1}] - {2}: {3}", Name, QualityRating, InterfaceType, Recommendation);
        }
    }

    public static class NetworkEnvironmentDetector
    {
        public static NetworkAdapterInfo DetectPrimaryAdapter()
        {
            NetworkAdapterInfo best = null;

            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    NetworkAdapterInfo info = new NetworkAdapterInfo
                    {
                        Name = ni.Name,
                        Description = ni.Description,
                        InterfaceType = ni.NetworkInterfaceType,
                        SpeedBps = ni.Speed
                    };

                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                    {
                        info.IsOptimal = true;
                        info.QualityRating = "⚡ Optimal (Wired LAN)";
                        info.Recommendation = "Wired Ethernet detected. Expected latency: < 1.0 ms. Zero jitter.";
                        return info; // Ethernet is immediately best choice
                    }
                    else if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    {
                        if (ni.Speed >= 300000000) // 300 Mbps+ -> 5 GHz / Wi-Fi 6
                        {
                            info.IsOptimal = true;
                            info.QualityRating = "🟢 Good (5 GHz / Wi-Fi 6)";
                            info.Recommendation = "High-speed Wi-Fi active. Disable Wi-Fi power saving in Device Manager for lowest latency.";
                        }
                        else
                        {
                            info.IsOptimal = false;
                            info.QualityRating = "⚠️ Sub-optimal (2.4 GHz Wi-Fi)";
                            info.Recommendation = "2.4 GHz band or low signal detected. Switch to 5 GHz or Ethernet for butter-smooth 1000Hz motion.";
                        }

                        if (best == null || info.SpeedBps > best.SpeedBps)
                        {
                            best = info;
                        }
                    }
                }
            }
            catch { }

            return best ?? new NetworkAdapterInfo
            {
                Name = "Local Loopback / Virtual",
                QualityRating = "ℹ️ Standard",
                Recommendation = "Ensure both computers are connected to the same high-speed Wi-Fi or Ethernet switch."
            };
        }
    }
}
