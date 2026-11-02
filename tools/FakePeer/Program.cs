// FakePeer: a scripted stand-in for the second player, for testing the host side of PotionCoop on one PC.
// Usage: dotnet run -- [host=127.0.0.1] [port=47777] [gameVersion=1.2.6]
// It joins, orbits the host's position as a ghost, and keeps picking up and dropping one bottle at its own feet.

using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 ? int.Parse(args[1]) : 47777;
var gameVersion = args.Length > 2 ? args[2] : "1.2.6";
const string ModVersion = "0.1.0";

using var client = new TcpClient();
client.Connect(host, port);
client.NoDelay = true;
var stream = client.GetStream();
var sendLock = new object();
var clock = System.Diagnostics.Stopwatch.StartNew();

void Send(object msg)
{
    var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg));
    lock (sendLock)
    {
        stream.Write(BitConverter.GetBytes(payload.Length));
        stream.Write(payload);
    }
}

JsonNode Receive()
{
    var header = new byte[4];
    stream.ReadExactly(header);
    var payload = new byte[BitConverter.ToInt32(header)];
    stream.ReadExactly(payload);
    return JsonNode.Parse(Encoding.UTF8.GetString(payload));
}

void Log(string s) => Console.WriteLine($"[{clock.Elapsed.TotalSeconds,6:F1}s] {s}");

Send(new { T = "hello", Mod = ModVersion, Game = gameVersion, Name = "Bot" });

float hx = 0, hy = 0, hz = 0, hyaw = 0;
bool haveHostPos = false;
JsonNode borrowed = null; // original state of the bottle we play with
var floor = new List<JsonNode>(); // floor vessels with positions, to pick one next to the host
var welcomed = false;

var reader = new Thread(() =>
{
    try
    {
        while (true)
        {
            var msg = Receive();
            var type = (string)msg["T"];
            switch (type)
            {
                case "welcome":
                    welcomed = true;
                    Log($"welcomed by {msg["Name"]}");
                    Send(new { T = "need", Buckets = Enumerable.Range(0, 17).ToArray() });
                    break;
                case "reject":
                    Log($"REJECTED: {msg["Reason"]}");
                    Environment.Exit(1);
                    break;
                case "p":
                    var p = msg["Pos"].AsArray();
                    (hx, hy, hz) = ((float)p[0], (float)p[1], (float)p[2]);
                    hyaw = (float)msg["Yaw"];
                    haveHostPos = true;
                    break;
                case "ping":
                    Send(new { T = "pong", Sent = (double)msg["Sent"] });
                    break;
                case "s":
                    var potions = msg["Potions"].AsArray();
                    var vats = msg["Vats"].AsArray();
                    var resync = (bool?)msg["Resync"] ?? false;
                    if (resync)
                    {
                        var byLoc = potions.GroupBy(x => (int)x["l"]).Select(g => $"{g.Key}:{g.Count()}");
                        Log($"world chunk: {potions.Count} vessels ({string.Join(" ", byLoc)}), {vats.Count} vats");
                        lock (floor)
                            floor.AddRange(potions.Where(x => (int)x["l"] == 0 && x["p"] != null).Select(x => x.DeepClone()));
                    }
                    else
                        Log($"host changed: {string.Join(", ", potions.Select(x => $"#{x["u"]} loc={x["l"]} place={x["s"]}"))} vats={vats.Count}");
                    break;
                case "d":
                    Log("digest from host");
                    break;
            }
        }
    }
    catch (Exception e)
    {
        Log($"link closed: {e.Message}");
        Environment.Exit(0);
    }
}) { IsBackground = true };
reader.Start();

// Ping the host so its panel shows a ping.
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(2000);
        if (welcomed) Send(new { T = "ping", Sent = clock.Elapsed.TotalSeconds });
    }
});

// Carry loop: grab the floor bottle nearest the ghost, then every 1.5 s alternately drop it at the
// ghost's feet and pick it up again, so it visibly follows the ghost around its circle.
var nextAction = 6.0;
var holding = false;
while (true)
{
    Thread.Sleep(50);
    if (!welcomed || !haveHostPos)
        continue;

    var t = clock.Elapsed.TotalSeconds;
    var angle = (float)(t * 0.4);
    var x = hx + 2.5f * MathF.Cos(angle);
    var z = hz + 2.5f * MathF.Sin(angle);
    // Face the host while circling.
    var yaw = MathF.Atan2(hx - x, hz - z) * 180f / MathF.PI;
    Send(new { T = "p", Pos = new[] { x, hy, z }, Yaw = yaw, Pitch = 10f, InMenu = false, Held = holding ? 1 : 0 });

    if (t < nextAction)
        continue;
    nextAction = t + 1.5;

    if (borrowed == null)
    {
        lock (floor)
            borrowed = floor.OrderBy(v =>
            {
                var q = v["p"].AsArray();
                float dx = (float)q[0] - x, dz = (float)q[2] - z;
                return dx * dx + dz * dz;
            }).FirstOrDefault();
        if (borrowed == null)
            continue;
        Log($"carrying #{borrowed["u"]} around");
    }

    var uid = (int)borrowed["u"];
    var units = borrowed["n"];
    if (!holding)
        Send(new { T = "s", Potions = new[] { new { u = uid, l = 11, s = 0, n = units } }, Vats = Array.Empty<object>() });
    else
        Send(new { T = "s", Potions = new[] { new { u = uid, l = 0, s = 0, p = new[] { x, hy + 0.1f, z }, r = new[] { 0f, 0f, 0.7071f, 0.7071f }, n = units } }, Vats = Array.Empty<object>() });
    holding = !holding;
}
