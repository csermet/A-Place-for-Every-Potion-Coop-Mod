using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PotionCoop.Net
{
    /// <summary>
    /// One TCP link to the other player. Length-prefixed UTF-8 JSON frames.
    /// A background thread reads; everything else (including Send) happens on the Unity main thread.
    /// Incoming frames and connection events are queued and drained by <see cref="Poll"/>.
    /// </summary>
    internal sealed class TcpSession : IDisposable
    {
        public const string Disconnected = "\u0000disconnected";
        const int MaxFrame = 32 * 1024 * 1024;

        readonly ConcurrentQueue<string> _inbox = new();
        readonly object _sendLock = new();
        TcpListener _listener;
        TcpClient _client;
        NetworkStream _stream;
        Thread _thread;
        volatile bool _closing;

        public bool IsHost { get; private set; }
        public bool IsConnected => _stream != null && !_closing;
        public string RemoteAddress { get; private set; } = "";

        public static TcpSession Host(int port)
        {
            var session = new TcpSession { IsHost = true };
            session._listener = new TcpListener(IPAddress.Any, port);
            session._listener.Start(1);
            session._thread = new Thread(session.AcceptLoop) { IsBackground = true, Name = "PotionCoop.Accept" };
            session._thread.Start();
            return session;
        }

        public static TcpSession Join(IPAddress address, int port)
        {
            var session = new TcpSession { IsHost = false };
            session._thread = new Thread(() => session.ConnectLoop(address, port)) { IsBackground = true, Name = "PotionCoop.Connect" };
            session._thread.Start();
            return session;
        }

        void AcceptLoop()
        {
            try
            {
                while (!_closing)
                {
                    var client = _listener.AcceptTcpClient();
                    if (_stream != null)
                    {
                        // Only two players: politely refuse a third.
                        client.Close();
                        continue;
                    }
                    Attach(client);
                    ReadLoop();
                    // Connection ended: wait for the next one.
                    Detach();
                }
            }
            catch (Exception) when (_closing) { }
            catch (Exception e)
            {
                _inbox.Enqueue(Disconnected);
                CoopMod.Log.Warning($"accept loop ended: {e.Message}");
            }
        }

        void ConnectLoop(IPAddress address, int port)
        {
            try
            {
                var client = new TcpClient();
                client.Connect(address, port);
                Attach(client);
                ReadLoop();
            }
            catch (Exception e) when (!_closing)
            {
                CoopMod.Log.Warning($"connection failed: {e.Message}");
            }
            catch (Exception) { }
            Detach();
        }

        void Attach(TcpClient client)
        {
            client.NoDelay = true;
            _client = client;
            RemoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
            _stream = client.GetStream();
            _inbox.Enqueue("\u0000connected");
        }

        void Detach()
        {
            var hadStream = _stream != null;
            _stream = null;
            try { _client?.Close(); } catch { }
            _client = null;
            if (hadStream || !IsHost)
                _inbox.Enqueue(Disconnected);
        }

        void ReadLoop()
        {
            var header = new byte[4];
            try
            {
                while (!_closing)
                {
                    ReadExactly(header, 4);
                    var length = BitConverter.ToInt32(header, 0);
                    if (length <= 0 || length > MaxFrame)
                        throw new IOException($"bad frame length {length}");
                    var payload = new byte[length];
                    ReadExactly(payload, length);
                    _inbox.Enqueue(Encoding.UTF8.GetString(payload));
                }
            }
            catch (Exception) { }
        }

        void ReadExactly(byte[] buffer, int count)
        {
            var read = 0;
            while (read < count)
            {
                var n = _stream.Read(buffer, read, count - read);
                if (n <= 0)
                    throw new EndOfStreamException();
                read += n;
            }
        }

        public void Send(object message)
        {
            var stream = _stream;
            if (stream == null)
                return;
            var payload = Encoding.UTF8.GetBytes(Protocol.Encode(message));
            var frame = new byte[4 + payload.Length];
            BitConverter.GetBytes(payload.Length).CopyTo(frame, 0);
            payload.CopyTo(frame, 4);
            try
            {
                lock (_sendLock)
                    stream.Write(frame, 0, frame.Length);
            }
            catch (Exception e)
            {
                CoopMod.Log.Warning($"send failed: {e.Message}");
                try { _client?.Close(); } catch { }
            }
        }

        /// <summary>Next queued frame or event; call from the main thread.</summary>
        public bool Poll(out string frame) => _inbox.TryDequeue(out frame);

        public void Dispose()
        {
            _closing = true;
            try { _listener?.Stop(); } catch { }
            try { _client?.Close(); } catch { }
            _stream = null;
        }
    }
}
