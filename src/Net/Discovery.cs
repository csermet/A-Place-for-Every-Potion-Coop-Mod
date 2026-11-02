using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PotionCoop.Net
{
    /// <summary>
    /// LAN discovery over UDP broadcast: the host answers "who is hosting?" with its TCP port and name.
    /// </summary>
    internal sealed class DiscoveryResponder : IDisposable
    {
        const string Question = "POTIONCOOP?";
        readonly UdpClient _udp;
        readonly Thread _thread;
        readonly string _answer;
        volatile bool _closing;

        public DiscoveryResponder(int discoveryPort, int tcpPort, string hostName)
        {
            _answer = $"POTIONCOOP!|{tcpPort}|{hostName}";
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
            _thread = new Thread(Loop) { IsBackground = true, Name = "PotionCoop.Discovery" };
            _thread.Start();
        }

        void Loop()
        {
            var answer = Encoding.UTF8.GetBytes(_answer);
            while (!_closing)
            {
                try
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    var data = _udp.Receive(ref from);
                    if (Encoding.UTF8.GetString(data) == Question)
                        _udp.Send(answer, answer.Length, from);
                }
                catch (Exception) when (_closing) { }
                catch (Exception e)
                {
                    CoopMod.Log.Warning($"discovery responder: {e.Message}");
                    Thread.Sleep(500);
                }
            }
        }

        public void Dispose()
        {
            _closing = true;
            try { _udp.Close(); } catch { }
        }

        /// <summary>Asks the LAN who is hosting and collects every answer within the window. Blocking; run off the main thread.</summary>
        public static List<(IPAddress address, int port, string name)> Scan(int discoveryPort, TimeSpan window)
        {
            var found = new List<(IPAddress, int, string)>();
            using var udp = new UdpClient(0) { EnableBroadcast = true };
            udp.Client.ReceiveTimeout = 250;
            var question = Encoding.UTF8.GetBytes(Question);
            var deadline = DateTime.UtcNow + window;
            var nextAsk = DateTime.MinValue;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (DateTime.UtcNow >= nextAsk)
                    {
                        udp.Send(question, question.Length, new IPEndPoint(IPAddress.Broadcast, discoveryPort));
                        nextAsk = DateTime.UtcNow.AddMilliseconds(400);
                    }
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    var parts = Encoding.UTF8.GetString(udp.Receive(ref from)).Split('|');
                    if (parts.Length >= 3 && parts[0] == "POTIONCOOP!" && int.TryParse(parts[1], out var port)
                        && !found.Any(f => f.Item1.Equals(from.Address) && f.Item2 == port))
                        found.Add((from.Address, port, parts[2]));
                }
                catch (SocketException) { }
            }
            return found;
        }

        /// <summary>This PC's addresses a friend on the same home network can reach (private IPv4 ranges, real adapters first).</summary>
        public static List<string> LocalLanAddresses()
        {
            var result = new List<(string ip, bool gateway)>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    var props = nic.GetIPProperties();
                    var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                                     && !g.Address.Equals(IPAddress.Any));
                    foreach (var unicast in props.UnicastAddresses)
                    {
                        var a = unicast.Address;
                        if (a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a))
                            result.Add((a.ToString(), hasGateway));
                    }
                }
            }
            catch (Exception) { }
            // Adapters without a gateway are usually virtual (WSL, Hyper-V, VPN): show them only if nothing else exists.
            if (result.Any(r => r.gateway))
                result.RemoveAll(r => !r.gateway);
            return result.Select(r => r.ip).Distinct().ToList();
        }

        static bool IsPrivate(IPAddress address)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }

        /// <summary>Broadcasts the question and waits for the first host to answer. Blocking; run off the main thread.</summary>
        public static bool TryFind(int discoveryPort, TimeSpan timeout, out IPAddress address, out int tcpPort, out string hostName)
        {
            address = null;
            tcpPort = 0;
            hostName = "";
            using var udp = new UdpClient(0) { EnableBroadcast = true };
            udp.Client.ReceiveTimeout = 400;
            var question = Encoding.UTF8.GetBytes(Question);
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    udp.Send(question, question.Length, new IPEndPoint(IPAddress.Broadcast, discoveryPort));
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    var parts = Encoding.UTF8.GetString(udp.Receive(ref from)).Split('|');
                    if (parts.Length >= 3 && parts[0] == "POTIONCOOP!" && int.TryParse(parts[1], out tcpPort))
                    {
                        address = from.Address;
                        hostName = parts[2];
                        return true;
                    }
                }
                catch (SocketException) { }
            }
            return false;
        }
    }
}
