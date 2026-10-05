using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Fh6.Recorder;

public sealed record LanAddress(string Address, string InterfaceName, int Score);

public static class NetworkUtil
{
    private static readonly string[] VirtualHints =
        { "virtual", "vethernet", "hyper-v", "vmware", "virtualbox", "wsl", "tap", "tailscale", "zerotier", "bluetooth", "loopback", "docker" };

    /// <summary>スマホから届きそうな IPv4 アドレスを、それらしい順に返す</summary>
    public static List<LanAddress> GetCandidates()
    {
        var list = new List<LanAddress>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); } catch { continue; }

            bool hasGateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            var text = (ni.Name + " " + ni.Description).ToLowerInvariant();
            bool isVirtual = VirtualHints.Any(text.Contains);

            foreach (var ua in props.UnicastAddresses)
            {
                var ip = ua.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
                var b = ip.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue; // APIPA

                bool isPrivate = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
                int score = (hasGateway ? 10 : 0) + (isPrivate ? 5 : 0) - (isVirtual ? 20 : 0)
                            + (ni.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet ? 2 : 0);
                list.Add(new LanAddress(ip.ToString(), ni.Name, score));
            }
        }
        return list.OrderByDescending(x => x.Score).ThenBy(x => x.Address).ToList();
    }
}
