using System;
using System.Net;
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
