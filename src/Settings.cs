using System;
using System.Collections.Concurrent;
using MelonLoader;
using PotionCoop.Game;
using UnityEngine;

namespace PotionCoop
{
    /// <summary>User settings, stored by MelonLoader in UserData\MelonPreferences.cfg under [PotionCoop].</summary>
    internal static class Settings
    {
        static MelonPreferences_Entry<string> _name;
        static MelonPreferences_Entry<int> _port;
        static MelonPreferences_Entry<string> _hostAddress;
        static MelonPreferences_Entry<bool> _verbose;

        public static void Init()
        {
            var category = MelonPreferences.CreateCategory("PotionCoop");
            _name = category.CreateEntry("Name", Environment.UserName, description: "Diğer oyuncunun göreceği isim");
            _port = category.CreateEntry("Port", 47777, description: "TCP portu (keşif için Port+1 UDP kullanılır)");
            _hostAddress = category.CreateEntry("HostAddress", "", description: "Boşsa host ağda otomatik aranır; bulunamazsa host'un IP'sini yaz (ör. 192.168.1.20)");
            _verbose = category.CreateEntry("Verbose", false, description: "Gönderilen her değişikliği logla");
        }

        public static string Name => string.IsNullOrWhiteSpace(_name.Value) ? "Oyuncu" : _name.Value.Trim();
        public static int Port => _port.Value;
        public static int DiscoveryPort => _port.Value + 1;
        public static string HostAddress => _hostAddress.Value;
        public static bool Verbose => _verbose.Value;

        /// <summary>Host sees the client in teal, the client sees the host in amber.</summary>
        public static Color RemoteColor => Coop.IsHost ? new Color(0.2f, 0.85f, 0.8f) : new Color(1f, 0.6f, 0.15f);
    }

    /// <summary>Runs callbacks from worker threads on the Unity main thread.</summary>
    internal static class MainThread
    {
        static readonly ConcurrentQueue<Action> Queue = new();

        public static void Post(Action action) => Queue.Enqueue(action);

        public static void Drain()
        {
            while (Queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { CoopMod.Log.Error(e.ToString()); }
            }
        }
    }
}
