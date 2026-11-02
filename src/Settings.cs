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
            _hostId = category.CreateEntry("HostId", "", description: "Bu kurulumun kimliği (katılanlar profillerini buna göre ayırır); değiştirme");
            _machine = category.CreateEntry("Machine", "", description: "Bu ayarların ait olduğu bilgisayar (klasör kopyalanınca isim ve kimlik yenilenir)");

            // The game folder is copied to the friend's PC with these settings in it: a different computer gets
            // its own id and starts with its own Windows user name instead of ours.
            var machine = Environment.MachineName;
            var copied = !string.IsNullOrEmpty(_machine.Value) && _machine.Value != machine;
            if (copied || string.IsNullOrWhiteSpace(_name.Value))
                _name.Value = Environment.UserName;
            if (copied || string.IsNullOrWhiteSpace(_hostId.Value))
                _hostId.Value = Guid.NewGuid().ToString("N");
            if (copied || _machine.Value != machine)
            {
                _machine.Value = machine;
                Save();
            }
        }

        static MelonPreferences_Entry<string> _hostId;
        static MelonPreferences_Entry<string> _machine;

        /// <summary>Stable id of this install, sent when hosting: a guest keeps one profile per host.</summary>
        public static string HostId => _hostId.Value;

        public static string Name => string.IsNullOrWhiteSpace(_name.Value) ? "Player" : _name.Value.Trim();
        public static string RawName => _name.Value;

        public static void SetName(string name)
        {
            if (name == _name.Value)
                return;
            _name.Value = name.Length > 24 ? name.Substring(0, 24) : name;
        }

        public static void SetHostAddress(string address) => _hostAddress.Value = address;

        /// <summary>Writes UserData\MelonPreferences.cfg (called when the co-op panel closes, not on every keystroke).</summary>
        public static void Save() => MelonPreferences.Save();
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
