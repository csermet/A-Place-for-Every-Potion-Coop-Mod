using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Il2Cpp;
using Il2CppCodeBase.Components.Potions;
using Il2CppCodeBase.Components.Vats;
using Newtonsoft.Json.Linq;
using PotionCoop.Net;
using UnityEngine;

namespace PotionCoop.Game
{
    /// <summary>Session controller: connection, handshake, change tracking, healing and the ghost.</summary>
    internal static class Coop
    {
        const float PlayerSendInterval = 0.05f;
        const float PingInterval = 2f;
        const float DigestInterval = 4f;
        const int ScanChunk = 150;
        const float SettleTimeout = 8f;

        static TcpSession _session;
        static DiscoveryResponder _responder;
        static bool _ready;
        static string _remoteName = "";

        // change tracking
        static readonly HashSet<int> DirtyPotions = new();
        static readonly HashSet<int> DirtyVats = new();
        static readonly Dictionary<int, float> PendingSettle = new();
        static readonly HashSet<int> SentFalling = new();
        static int _tossLogs;
        static readonly int[] FruitlessResyncs = new int[Protocol.BucketCount];
        static HashSet<int> _lastHeld = new();

        // world messages wait here while the level is loading
        static readonly List<(string type, JObject body)> Deferred = new();

        // digest scanning
        static PotionFacade[] _scanList;
        static int[] _scanUids;
        static Dictionary<int, int> _scanSlotOf;
        static double _scanCostMs;
        static double _scanWorstFrameMs;
        static float _nextScanReport;
        static int _scanIndex = -1;
        static uint[] _scanAcc;
        static HashSet<int> _scanHeld;
        static float _nextScan;
        static uint[] _myDigest;
        static readonly int[] MismatchStreak = new int[Protocol.BucketCount];
        static bool _initialSyncRequested;

        static Ghost _ghost;
        static float _nextPlayerSend;
        static float _nextPing;

        public static bool IsHost => _session != null && _session.IsHost;
        public static bool IsConnected => _session != null && _session.IsConnected && _ready;
        public static bool IsRunning => _session != null;
        public const string StatusOff = "off";
        public static string Status { get; private set; } = StatusOff;
        public static string RemoteName => _remoteName;
        public static double PingMs { get; private set; } = -1;
        /// <summary>Client: the host's vat capacities (they follow the host's skills, see <see cref="VatCapacityPatch"/>).</summary>
        public static readonly Dictionary<int, int> HostVatCapacity = new();
        /// <summary>null = unknown, true = worlds match, false = healing.</summary>
        public static bool? InSync { get; private set; }
        public static Ghost Ghost => _ghost;

        static float Now => Time.realtimeSinceStartup;

        // ---------- lifecycle ----------

        public static void StartHost()
        {
            Stop();
            try
            {
                _session = TcpSession.Host(Settings.Port);
                _responder = new DiscoveryResponder(Settings.DiscoveryPort, Settings.Port, Settings.Name);
                Status = $"hosting, waiting for a player (port {Settings.Port})";
                CoopMod.Log.Msg($"hosting on TCP {Settings.Port}, discovery UDP {Settings.DiscoveryPort}");
            }
            catch (Exception e)
            {
                Status = $"could not start hosting: {e.Message}";
                CoopMod.Log.Error(Status);
                Stop();
            }
        }

        /// <summary>Joining is done from the main menu, so the shared world is loaded into the co-op save folder.</summary>
        public static string JoinBlocker =>
            IsRunning && IsHost ? "you are hosting (stop hosting first)"
            : !SavePaths.Active && GameRefs.State is not (eGameState.Lobby or eGameState.LoadLobby or eGameState.None)
                ? "go back to the main menu to join"
                : null;

        /// <summary>Join a known host, or (no address) the configured HostAddress, or whoever answers on the LAN.</summary>
        public static void Join(IPAddress address = null, int port = 0)
        {
            var blocker = JoinBlocker;
            if (blocker != null)
            {
                Status = blocker;
                return;
            }
            Stop();
            if (address != null)
            {
                Connect(address, port > 0 ? port : Settings.Port);
                return;
            }
            Status = "looking for a host…";
            var configured = Settings.HostAddress;
            Task.Run(() =>
            {
                IPAddress found;
                var foundPort = Settings.Port;
                if (!string.IsNullOrWhiteSpace(configured) && IPAddress.TryParse(configured.Trim(), out var parsed))
                    found = parsed;
                else if (!DiscoveryResponder.TryFind(Settings.DiscoveryPort, TimeSpan.FromSeconds(4), out found, out foundPort, out _))
                {
                    MainThread.Post(() => Status = "no host found: type the IP shown on the host's screen");
                    return;
                }
                MainThread.Post(() => Connect(found, foundPort));
            });
        }

        static void Connect(IPAddress address, int port)
        {
            Status = $"connecting to {address}…";
            _session = TcpSession.Join(address, port);
        }

        /// <summary>Host: the client claimed something we hold. Send our state of it again (we have the final word).</summary>
        public static void Reassert(int uid)
        {
            if (IsConnected && IsHost)
                DirtyPotions.Add(uid);
        }

        public static void Stop()
        {
            if (_session != null && GameRefs.InLevel && _ghost != null)
                World.DropRemoteHeld(_ghost.Position);
            _session?.Dispose();
            _session = null;
            _responder?.Dispose();
            _responder = null;
            ResetLink();
            Status = StatusOff;
        }

        static void ResetLink()
        {
            _ready = false;
            _remoteName = "";
            PingMs = -1;
            InSync = null;
            _initialSyncRequested = false;
            Deferred.Clear();
            DirtyPotions.Clear();
            DirtyVats.Clear();
            PendingSettle.Clear();
            SentFalling.Clear();
            HostVatCapacity.Clear();
            Array.Clear(FruitlessResyncs, 0, FruitlessResyncs.Length);
            Array.Clear(MismatchStreak, 0, MismatchStreak.Length);
            _ghost?.Destroy();
            _ghost = null;
            CatSync.Release();
        }

        // ---------- per-frame ----------

        public static void Update()
        {
            MainThread.Drain();
            if (_session == null)
                return;

            while (_session != null && _session.Poll(out var frame))
            {
                try { Handle(frame); }
                catch (Exception e) { CoopMod.Log.Error($"message failed: {e}"); }
            }

            if (!IsConnected)
                return;

            if (GameRefs.InLevel)
            {
                if (Deferred.Count > 0)
                {
                    var queued = Deferred.ToList();
                    Deferred.Clear();
                    foreach (var (type, body) in queued)
                        HandleWorld(type, body);
                }

                if (!IsHost && !_initialSyncRequested)
                {
                    _initialSyncRequested = true;
                    _session.Send(new NeedMsg { Buckets = Enumerable.Range(0, Protocol.BucketCount).ToArray() });
                    CoopMod.Log.Msg("asked host for the full world");
                }

                ScanStep();
                _ghost?.Tick(Time.unscaledDeltaTime);
                World.TickMotion(_ghost, Time.unscaledDeltaTime);
                if (IsHost)
                    CatSync.HostTick(_session, Now);
                else
                    CatSync.ClientTick(Time.unscaledDeltaTime);
                World.TickDiagnostics();
            }
            else if (_ghost != null && !GameRefs.InLevel)
            {
                _ghost.Destroy();
                _ghost = null;
            }

            if (Now >= _nextPing)
            {
                _nextPing = Now + PingInterval;
                _session.Send(new PingMsg { Sent = Now });
            }
        }

        public static void LateUpdate()
        {
            if (!IsConnected || !GameRefs.InLevel)
                return;
            FlushChanges();
            SendPlayer();
        }

        // ---------- incoming ----------

        static void Handle(string frame)
        {
            if (frame == "\u0000connected")
            {
                _remoteName = "";
                Status = IsHost ? "a player connected, saying hello…" : "connected, saying hello…";
                if (!IsHost)
                    _session.Send(new HelloMsg { Mod = Protocol.ModVersion, Game = Application.version, Name = Settings.Name });
                return;
            }
            if (frame == TcpSession.Disconnected)
            {
                var who = string.IsNullOrEmpty(_remoteName) ? "the other player" : _remoteName;
                if (GameRefs.InLevel && _ghost != null)
                    World.DropRemoteHeld(_ghost.Position);
                var wasReady = _ready;
                ResetLink();
                if (IsHost)
                    Status = wasReady ? $"{who} left; waiting for a player" : $"hosting, waiting for a player (port {Settings.Port})";
                else
                {
                    Status = wasReady ? "connection lost (join again from Co-op)" : "could not connect (IP, port or firewall?)";
                    _session?.Dispose();
                    _session = null;
                }
                CoopMod.Log.Msg($"link closed: {Status}");
                return;
            }

            (string type, JObject body) msg;
            try { msg = Protocol.Decode(frame); }
            catch (Exception e)
            {
                CoopMod.Log.Warning($"bad frame: {e.Message}");
                return;
            }

            switch (msg.type)
            {
                case "hello" when IsHost:
                    var hello = msg.body.ToObject<HelloMsg>();
                    if (hello.Mod != Protocol.ModVersion || hello.Game != Application.version)
                    {
                        _session.Send(new RejectMsg
                        {
                            Reason = $"different versions (you: mod {hello.Mod}, game {hello.Game}; host: mod {Protocol.ModVersion}, game {Application.version}). " +
                                     "Use the same game folder on both PCs.",
                        });
                        Status = $"{hello.Name} turned away: different versions";
                        return;
                    }
                    _remoteName = hello.Name;
                    _ready = true;
                    _session.Send(new WelcomeMsg { Name = Settings.Name });
                    Status = $"{_remoteName} joined";
                    CoopMod.Log.Msg($"{_remoteName} joined from {_session.RemoteAddress}");
                    return;

                case "welcome" when !IsHost:
                    _remoteName = msg.body.ToObject<WelcomeMsg>().Name;
                    _ready = true;
                    SavePaths.Activate();
                    Status = SavePaths.HasStartedGame()
                        ? $"connected to {_remoteName}: press Continue"
                        : $"connected to {_remoteName}: no save on this PC yet, press New Game";
                    CoopMod.Log.Msg($"joined {_remoteName}'s world");
                    return;

                case "reject":
                    Status = "turned away: " + msg.body.ToObject<RejectMsg>().Reason;
                    CoopMod.Log.Warning(Status);
                    _session?.Dispose();
                    _session = null;
                    return;

                case "ping":
                    _session.Send(new PongMsg { Sent = msg.body.ToObject<PingMsg>().Sent });
                    return;

                case "pong":
                    PingMs = (Now - msg.body.ToObject<PongMsg>().Sent) * 1000.0;
                    return;
            }

            if (!_ready)
                return;

            if (msg.type == "cat")
            {
                if (GameRefs.InLevel && !IsHost)
                    CatSync.OnCat(msg.body.ToObject<CatMsg>());
                return;
            }

            if (msg.type == "p")
            {
                if (GameRefs.InLevel)
                    OnPlayer(msg.body.ToObject<PlayerMsg>());
                return;
            }

            if (GameRefs.InLevel)
                HandleWorld(msg.type, msg.body);
            else
                Deferred.Add(msg);
        }

        static void HandleWorld(string type, JObject body)
        {
            switch (type)
            {
                case "s":
                    var states = body.ToObject<StatesMsg>();
                    var changed = World.Apply(states.Potions, states.Vats, animate: !states.Resync);
                    if (states.Resync)
                        CheckResyncProgress(states, changed);
                    if (states.Resync || changed > 0)
                        CoopMod.Log.Msg($"applied {states.Potions.Count} vessel(s), {states.Vats.Count} vat(s): {changed} changed{(states.Resync ? " [resync]" : "")}");
                    break;

                case "need" when IsHost:
                    SendBuckets(body.ToObject<NeedMsg>().Buckets);
                    break;

                case "d" when !IsHost:
                    var digest = body.ToObject<DigestMsg>();
                    if (digest.VatCapacity != null)
                        foreach (var pair in digest.VatCapacity)
                            HostVatCapacity[pair.Key] = pair.Value;
                    CompareDigest(digest.Buckets);
                    break;
            }
        }

        static void OnPlayer(PlayerMsg msg)
        {
            var pos = new Vector3(msg.Pos[0], msg.Pos[1], msg.Pos[2]);
            if (_ghost == null || !_ghost.Alive)
                _ghost = new Ghost(Settings.RemoteColor, pos);
            _ghost.Name = _remoteName;
            _ghost.InMenu = msg.InMenu;
            _ghost.Held = msg.Held;
            _ghost.SetTarget(pos, msg.Yaw, msg.Pitch);
        }

        // ---------- outgoing ----------

        static void SendPlayer()
        {
            if (Now < _nextPlayerSend)
                return;
            _nextPlayerSend = Now + PlayerSendInterval;
            var hero = GameRefs.Hero;
            if (hero == null)
                return;
            var pos = hero.Position;
            var look = hero.Look;
            var populator = GameRefs.Populator;
            _session.Send(new PlayerMsg
            {
                Pos = new[] { pos.x, pos.y, pos.z },
                Yaw = look != null ? look._yaw : 0f,
                Pitch = look != null ? look._pitch : 0f,
                InMenu = GameRefs.State != eGameState.Playing,
                Held = populator != null ? populator.HeldPotions().Count + populator.ApronPotions().Count : 0,
            });
        }

        /// <summary>Hand or apron row changed: everything in it now, and everything that just left it, is dirty.</summary>
        public static void MarkHeldRows()
        {
            if (!ShouldTrack())
                return;
            var held = World.LocalHeld(GameRefs.Populator);
            DirtyPotions.UnionWith(held);
            DirtyPotions.UnionWith(_lastHeld);
            _lastHeld = held;
        }

        public static void MarkPotion(PotionFacade potion)
        {
            if (ShouldTrack() && potion != null)
                DirtyPotions.Add(potion.Uid);
        }

        public static void MarkVat(VatFacade vat)
        {
            if (ShouldTrack() && vat != null)
                DirtyVats.Add(vat.Uid);
        }

        static bool ShouldTrack() => IsConnected && !World.Applying && GameRefs.InLevel;

        static void FlushChanges()
        {
            if (DirtyPotions.Count == 0 && DirtyVats.Count == 0 && PendingSettle.Count == 0)
                return;

            var populator = GameRefs.Populator;
            var held = World.LocalHeld(populator);
            var msg = new StatesMsg();

            foreach (var uid in DirtyPotions)
                if (!PendingSettle.ContainsKey(uid))
                    PendingSettle[uid] = Now;
            DirtyPotions.Clear();

            foreach (var uid in PendingSettle.Keys.ToList())
            {
                if (!populator.TryGetPotion(uid, out var potion) || potion == null)
                {
                    PendingSettle.Remove(uid);
                    continue;
                }
                var state = World.Read(populator, potion, held);
                if (state.Location == NetLocation.Floor && potion.CarryState.Value != ePotionCarryState.Idle
                    && Now - PendingSettle[uid] < SettleTimeout)
                {
                    // Just dropped and still falling: tell the other side right away (it lets the bottle fall too),
                    // then keep watching and send where it finally comes to rest.
                    if (SentFalling.Add(uid))
                    {
                        state.Falling = true;
                        // A thrown bottle (R) flies to the point the player aimed at; the game knows it already.
                        if (potion.DropStyle == eDropStyle.BallisticToss)
                        {
                            var target = potion.DropTargetPosition;
                            if (target != Vector3.zero && (target - potion.transform.position).sqrMagnitude < 400f)
                            {
                                var rot = potion._carrier != null ? potion._carrier.DropTargetRotation : potion.transform.rotation;
                                state.Pos = new[] { target.x, target.y, target.z };
                                state.Rot = new[] { rot.x, rot.y, rot.z, rot.w };
                                state.Toss = true;
                            }
                        }
                        if (_tossLogs < 10)
                        {
                            _tossLogs++;
                            CoopMod.Log.Msg($"[diag] drop #{uid} style={potion.DropStyle} from={potion.transform.position} " +
                                            $"target={potion.DropTargetPosition} toss={state.Toss}");
                        }
                        msg.Potions.Add(state);
                    }
                    continue;
                }
                PendingSettle.Remove(uid);
                SentFalling.Remove(uid);
                msg.Potions.Add(state);
            }

            if (DirtyVats.Count > 0)
            {
                foreach (var vat in World.Vats())
                    if (DirtyVats.Contains(vat.Uid))
                        msg.Vats.Add(World.ReadVat(vat));
                DirtyVats.Clear();
            }

            if (msg.Potions.Count > 0 || msg.Vats.Count > 0)
            {
                _session.Send(msg);
                if (Settings.Verbose)
                    CoopMod.Log.Msg($"sent {string.Join(", ", msg.Potions.Select(p => $"#{p.Uid}:{p.Location}{(p.Place != 0 ? "@" + p.Place : "")}"))} vats={msg.Vats.Count}");
            }
        }

        static void SendBuckets(int[] buckets)
        {
            var populator = GameRefs.Populator;
            var held = World.LocalHeld(populator);
            var wanted = new HashSet<int>(buckets);
            var msg = new StatesMsg { Resync = true };
            foreach (var entry in populator._potionsByUid)
                if (wanted.Contains(Protocol.BucketOf(entry.Key)))
                    msg.Potions.Add(World.Read(populator, entry.Value, held));
            if (wanted.Contains(0))
                msg.Vats.AddRange(World.Vats().Select(World.ReadVat));
            _session.Send(msg);
            CoopMod.Log.Msg($"sent {msg.Potions.Count} vessel(s) for bucket(s) {string.Join(",", buckets)}");
        }

        // ---------- digest healing ----------

        /// <summary>Hashes the whole world a few hundred vessels per frame; the host sends the result, the client compares.</summary>
        static void ScanStep()
        {
            var populator = GameRefs.Populator;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (_scanIndex < 0)
            {
                if (Now < _nextScan)
                    return;
                if (_scanList == null)
                {
                    var potions = new List<PotionFacade>(populator.PotionCount);
                    var uids = new List<int>(populator.PotionCount);
                    foreach (var entry in populator._potionsByUid)
                    {
                        potions.Add(entry.Value);
                        uids.Add(entry.Key);
                    }
                    _scanList = potions.ToArray();
                    _scanUids = uids.ToArray();
                }
                _scanSlotOf = new Dictionary<int, int>();
                foreach (var entry in populator._slotsByUid)
                {
                    var occupant = entry.Value.Potion;
                    if (occupant != null)
                        _scanSlotOf[occupant.Uid] = entry.Key;
                }
                _scanHeld = World.LocalHeld(populator);
                _scanAcc = new uint[Protocol.BucketCount];
                foreach (var vat in World.Vats())
                    _scanAcc[0] += World.Hash(World.ReadVat(vat));
                _scanIndex = 0;
                _scanCostMs = 0;
                _scanWorstFrameMs = 0;
            }

            var self = World.HeldBySelf;
            var other = World.HeldByOther;
            var end = Math.Min(_scanIndex + ScanChunk, _scanList.Length);
            for (; _scanIndex < end; _scanIndex++)
            {
                var potion = _scanList[_scanIndex];
                if (potion == null)
                    continue;
                var uid = _scanUids[_scanIndex];
                var place = 0;
                NetLocation location;
                if (_scanHeld.Contains(uid))
                    location = self;
                else if (World.RemoteHeld.Contains(uid))
                    location = other;
                else if (_scanSlotOf.TryGetValue(uid, out place))
                    location = NetLocation.Slot;
                else if (potion.IsHanging)
                {
                    location = NetLocation.Holder;
                    var holder = potion.HangingHolder;
                    place = holder != null ? holder.Index : 0;
                }
                else
                    location = NetLocation.Floor;
                _scanAcc[Protocol.BucketOf(uid)] += World.Hash(uid, location, place, potion.Contents._units);
            }

            var frameMs = watch.Elapsed.TotalMilliseconds;
            _scanCostMs += frameMs;
            _scanWorstFrameMs = Math.Max(_scanWorstFrameMs, frameMs);

            if (_scanIndex < _scanList.Length)
                return;

            _myDigest = _scanAcc;
            _scanIndex = -1;
            _nextScan = Now + DigestInterval;
            if (IsHost)
                _session.Send(new DigestMsg
                {
                    Buckets = _myDigest,
                    VatCapacity = World.Vats().ToDictionary(v => v.Uid, v => v.Capacity),
                });
            if (Now >= _nextScanReport)
            {
                _nextScanReport = Now + 60f;
                CoopMod.Log.Msg($"world scan: {_scanCostMs:F1} ms total, worst frame {_scanWorstFrameMs:F1} ms");
            }
        }

        static void CompareDigest(uint[] hostDigest)
        {
            if (_myDigest == null || hostDigest == null || hostDigest.Length != Protocol.BucketCount)
                return;

            var need = new List<int>();
            for (var b = 0; b < Protocol.BucketCount; b++)
            {
                if (hostDigest[b] == _myDigest[b])
                {
                    MismatchStreak[b] = 0;
                    continue;
                }
                // Scans run at different moments; only trust a mismatch that survives two rounds.
                if (++MismatchStreak[b] >= 2)
                {
                    need.Add(b);
                    MismatchStreak[b] = 0;
                }
            }

            InSync = need.Count == 0 && MismatchStreak.All(s => s == 0);
            if (need.Count > 0)
            {
                CoopMod.Log.Msg($"worlds differ in bucket(s) {string.Join(",", need)}; asking host");
                _session.Send(new NeedMsg { Buckets = need.ToArray() });
            }
        }

        /// <summary>
        /// A resync that changes nothing yet keeps being needed means something refuses to converge:
        /// name the vessels that still differ after applying, so the log says what and where.
        /// </summary>
        static void CheckResyncProgress(StatesMsg states, int changed)
        {
            var buckets = states.Potions.Select(p => Protocol.BucketOf(p.Uid)).Distinct().ToList();
            foreach (var b in buckets)
                FruitlessResyncs[b] = changed == 0 ? FruitlessResyncs[b] + 1 : 0;
            if (changed > 0 || buckets.All(b => FruitlessResyncs[b] < 3))
                return;

            var populator = GameRefs.Populator;
            var held = World.LocalHeld(populator);
            var report = new List<string>();
            foreach (var host in states.Potions)
            {
                if (!populator.TryGetPotion(host.Uid, out var potion) || potion == null)
                    continue;
                var mine = World.Read(populator, potion, held, withPose: false);
                if (World.Hash(mine) != World.Hash(host))
                    report.Add($"#{host.Uid} host={host.Location}@{host.Place} me={mine.Location}@{mine.Place}");
                if (report.Count >= 10)
                    break;
            }
            CoopMod.Log.Warning($"buckets {string.Join(",", buckets)} do not converge; differing: {(report.Count > 0 ? string.Join("; ", report) : "none per vessel (vats?)")}");
            foreach (var b in buckets)
                FruitlessResyncs[b] = 0;
        }

        /// <summary>Ctrl+F7: a one-line health report to compare between the two players' logs.</summary>
        public static void DumpStatus()
        {
            var line = $"[status] {(IsRunning ? (IsHost ? "host" : "client") : "offline")} connected={IsConnected} sync={InSync?.ToString() ?? "?"} ping={PingMs:F0}ms";
            var populator = GameRefs.Populator;
            if (GameRefs.InLevel && populator != null)
            {
                var data = populator._runtimeDataService.Get<Il2CppCodeBase.Services.RuntimeDataService.PlayerRuntimeData>();
                line += $" held={populator.HeldPotions().Count} apron={populator.ApronPotions().Count} remoteHeld={World.RemoteHeld.Count}";
                if (data != null)
                    line += $" shelvedOk={data.CorrectlyShelvedPotions.Value} sealed={data.SealedPotions.Value} " +
                            $"shelves={data.CompletedShelves.Value}/{data.TotalShelves.Value} sections={data.CompletedSections.Value}/{data.TotalSections.Value}";
                var hero = GameRefs.Hero;
                if (hero != null)
                    line += $" pos={hero.Position}";
            }
            CoopMod.Log.Msg(line);
        }

        /// <summary>Client: ask for everything again. Host: hash now.</summary>
        public static void ForceResync()
        {
            if (!IsConnected)
                return;
            if (IsHost)
                _nextScan = 0;
            else
                _session.Send(new NeedMsg { Buckets = Enumerable.Range(0, Protocol.BucketCount).ToArray() });
        }

        public static void OnLevelLeft()
        {
            GameRefs.Forget();
            _scanIndex = -1;
            _scanList = null;
            _scanUids = null;
            _lastHeld.Clear();
            _initialSyncRequested = false;
            World.ResetLevel();
            CatSync.Forget();
        }
    }
}
