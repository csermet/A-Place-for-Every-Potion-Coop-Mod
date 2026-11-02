using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PotionCoop.Game
{
    /// <summary>
    /// The other player's body: a hooded alchemist built at runtime from generated meshes. A long robe in the
    /// player's color with a gold hem, a mantle, a pointed hood whose dark opening shows two glowing eyes, gloved
    /// hands, potion vials on the belt. It walks with a bob and swinging sleeves, holds carried bottles in its hands and
    /// makes small moves when pouring or throwing. No colliders, no injected components; <see cref="Coop"/> moves it
    /// every frame towards the last received pose.
    /// </summary>
    internal sealed class Ghost
    {
        const float HeadHeight = 1.44f;
        const float Scale = 1.12f;
        const float HideCloserThan = 0.6f;

        static readonly Color Gold = new(0.86f, 0.66f, 0.3f);
        static readonly Color Leather = new(0.24f, 0.15f, 0.1f);
        static readonly Color EyeGlow = new(1f, 0.78f, 0.42f);
        static readonly Color[] Vials = { new(0.86f, 0.2f, 0.32f), new(0.2f, 0.72f, 0.68f), new(0.95f, 0.78f, 0.25f) };

        readonly GameObject _root;
        readonly GameObject _visual;
        readonly Transform _body;
        readonly Transform _head;
        readonly Transform _armL, _armR;
        readonly Transform _handL, _handR;
        readonly Transform _footL, _footR;
        readonly List<Transform> _eyes = new();

        Vector3 _targetPos;
        float _targetYaw, _targetPitch, _yaw, _pitch;
        float _speed, _walkPhase, _time;
        float _nextBlink = 2f, _blinkUntil;
        string _move;
        float _moveTime;
        int _lastHeld;

        public string Name;
        public bool InMenu;
        public int Held;
        public Vector3 Position => _root.transform.position;
        public Vector3 LabelAnchor => _root.transform.position + Vector3.up * (2.05f * Scale);
        public bool Alive => _root != null;

        public Ghost(Color playerColor, Vector3 position)
        {
            // The player's color, deepened towards plum so it sits well in the candle-lit hall.
            var robeColor = Color.Lerp(playerColor, new Color(0.2f, 0.14f, 0.24f), 0.3f);
            var robe = MakeMaterial(robeColor);
            var hood = MakeMaterial(robeColor * 0.86f);
            var mantle = MakeMaterial(robeColor * 0.74f);
            var lining = MakeMaterial(Color.Lerp(robeColor, new Color(0.9f, 0.84f, 0.72f), 0.35f));
            var shadow = MakeMaterial(new Color(0.03f, 0.02f, 0.035f), glow: 0f);
            var glove = MakeMaterial(Leather, glow: 0.02f);
            var gold = MakeMaterial(Gold, glow: 0.3f);
            var eyes = MakeMaterial(EyeGlow, glow: 2.4f);

            _root = new GameObject("PotionCoop.Ghost");
            Object.DontDestroyOnLoad(_root);
            _root.transform.position = position;
            _targetPos = position;
            _visual = new GameObject("Visual");
            _visual.transform.SetParent(_root.transform, false);
            // Built at a small size; scaled so the eyes sit about where the player's own eyes are.
            _visual.transform.localScale = Vector3.one * Scale;

            // Everything under the body bobs and sways while walking.
            _body = Node("Body", _visual.transform, Vector3.zero);

            // Robe: a long bell to the floor, with a gold hem.
            Mesh("Robe", _body, Lathe(new[]
            {
                new Vector2(0.34f, 0.00f), new Vector2(0.335f, 0.07f), new Vector2(0.30f, 0.35f), new Vector2(0.25f, 0.72f),
                new Vector2(0.21f, 1.00f), new Vector2(0.19f, 1.16f), new Vector2(0.12f, 1.26f), new Vector2(0.0f, 1.28f),
            }), robe);
            Mesh("Hem", _body, Lathe(new[] { new Vector2(0.346f, 0.0f), new Vector2(0.341f, 0.055f), new Vector2(0.336f, 0.07f) }, capBottom: false, capTop: false), gold);

            // Leather belt with a buckle and three little potions hanging at the side.
            Mesh("Belt", _body, Lathe(new[] { new Vector2(0.262f, 0.69f), new Vector2(0.256f, 0.75f) }, capBottom: false, capTop: false), glove);
            Shape(PrimitiveType.Cube, "Buckle", _body, gold, new Vector3(0f, 0.72f, 0.258f), new Vector3(0.06f, 0.055f, 0.02f));
            for (var i = 0; i < Vials.Length; i++)
            {
                var angle = (28f + i * 22f) * Mathf.Deg2Rad;
                var at = new Vector3(Mathf.Sin(angle) * 0.275f, 0.635f, Mathf.Cos(angle) * 0.275f);
                Shape(PrimitiveType.Capsule, "Vial", _body, MakeMaterial(Vials[i], glow: 0.6f), at, new Vector3(0.045f, 0.045f, 0.045f));
                Shape(PrimitiveType.Cube, "Cork", _body, glove, at + Vector3.up * 0.05f, new Vector3(0.022f, 0.02f, 0.022f));
            }

            // A short mantle over the shoulders, edged in gold.
            Mesh("Mantle", _body, Lathe(new[]
            {
                new Vector2(0.31f, 0.96f), new Vector2(0.29f, 1.04f), new Vector2(0.24f, 1.16f), new Vector2(0.15f, 1.25f),
                new Vector2(0.06f, 1.29f), new Vector2(0f, 1.30f),
            }, capBottom: false), mantle);
            Mesh("MantleEdge", _body, Lathe(new[] { new Vector2(0.316f, 0.955f), new Vector2(0.311f, 0.978f) }, capBottom: false, capTop: false), gold);

            // Shoes peek out under the hem and take turns stepping forward.
            _footL = Node("FootL", _body, new Vector3(-0.1f, 0.035f, 0.22f));
            _footR = Node("FootR", _body, new Vector3(0.1f, 0.035f, 0.22f));
            Shape(PrimitiveType.Sphere, "Shoe", _footL, glove, Vector3.zero, new Vector3(0.11f, 0.065f, 0.2f));
            Shape(PrimitiveType.Sphere, "Shoe", _footR, glove, Vector3.zero, new Vector3(0.11f, 0.065f, 0.2f));

            // Arms hang from shoulder pivots so they can swing; gloved hands at the end of wide sleeves.
            (_armL, _handL) = Arm("ArmL", -1f, robe, gold, glove);
            (_armR, _handR) = Arm("ArmR", 1f, robe, gold, glove);

            // The hood: a rounded shell with a pointed tip falling back, and in its dark opening two glowing eyes.
            _head = Node("Head", _body, Vector3.up * HeadHeight);
            Mesh("Hood", _head, Lathe(new[]
            {
                new Vector2(0.17f, -0.16f), new Vector2(0.205f, -0.06f), new Vector2(0.215f, 0.04f), new Vector2(0.19f, 0.14f),
                new Vector2(0.13f, 0.22f), new Vector2(0.06f, 0.27f), new Vector2(0f, 0.28f),
            }), hood);
            var tip = Node("Tip", _head, new Vector3(0f, 0.24f, -0.05f));
            tip.localRotation = Quaternion.Euler(-38f, 0f, 0f);
            Mesh("Tip", tip, Lathe(new[] { new Vector2(0.065f, 0f), new Vector2(0.04f, 0.1f), new Vector2(0.012f, 0.18f), new Vector2(0f, 0.19f) }, capBottom: false), hood);
            Shape(PrimitiveType.Sphere, "Rim", _head, lining, new Vector3(0f, -0.015f, 0.158f), new Vector3(0.29f, 0.31f, 0.17f));
            Shape(PrimitiveType.Sphere, "Opening", _head, shadow, new Vector3(0f, -0.015f, 0.172f), new Vector3(0.245f, 0.265f, 0.16f));
            Eye(-0.052f, eyes);
            Eye(0.052f, eyes);
        }

        // ---- construction helpers ----

        static Transform Node(string name, Transform parent, Vector3 localPos)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = localPos;
            return t;
        }

        (Transform pivot, Transform hand) Arm(string name, float side, Material sleeve, Material cuff, Material glove)
        {
            var pivot = Node(name, _body, new Vector3(0.2f * side, 1.17f, 0f));
            // A sleeve flaring towards a gold cuff, hanging down from the shoulder, and a gloved hand at its end.
            var hang = Node("Hang", pivot, Vector3.zero);
            hang.localRotation = Quaternion.Euler(180f, 0f, 0f);
            Mesh("Sleeve", hang, Lathe(new[] { new Vector2(0.05f, 0f), new Vector2(0.065f, 0.22f), new Vector2(0.088f, 0.36f) }, capBottom: false, capTop: false), sleeve);
            Mesh("Cuff", hang, Lathe(new[] { new Vector2(0.09f, 0.335f), new Vector2(0.091f, 0.365f) }, capBottom: false, capTop: false), cuff);
            var hand = Shape(PrimitiveType.Sphere, "Hand", pivot, glove, new Vector3(0f, -0.41f, 0f), new Vector3(0.095f, 0.11f, 0.095f));
            return (pivot, hand);
        }

        /// <summary>A glowing eye in the hood's dark opening; blinking squashes it.</summary>
        void Eye(float x, Material glow)
        {
            var eye = Node("Eye", _head, new Vector3(x, 0.0f, 0.248f));
            Shape(PrimitiveType.Sphere, "Glow", eye, glow, Vector3.zero, new Vector3(0.042f, 0.05f, 0.02f));
            _eyes.Add(eye);
        }

        static Transform Shape(PrimitiveType type, string name, Transform parent, Material material, Vector3 localPos, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var collider = go.GetComponent<Collider>();
            if (collider != null)
                Object.Destroy(collider);
            go.GetComponent<Renderer>().sharedMaterial = material;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            return go.transform;
        }

        static void Mesh(string name, Transform parent, Mesh mesh, Material material, Vector3? scale = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localScale = scale ?? Vector3.one;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        /// <summary>A surface of revolution around Y from a (radius, height) profile, bottom to top.</summary>
        static Mesh Lathe(Vector2[] profile, bool capBottom = true, bool capTop = true, int segments = 32)
        {
            var rings = profile.Length;
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            for (var k = 0; k < rings; k++)
                for (var i = 0; i < segments; i++)
                {
                    var a = i * Mathf.PI * 2f / segments;
                    vertices.Add(new Vector3(Mathf.Sin(a) * profile[k].x, profile[k].y, Mathf.Cos(a) * profile[k].x));
                }
            for (var k = 0; k < rings - 1; k++)
                for (var i = 0; i < segments; i++)
                {
                    var next = (i + 1) % segments;
                    // Clockwise seen from outside (Unity's front faces): the surface faces out.
                    int a = k * segments + i, b = (k + 1) * segments + i, c = k * segments + next, d = (k + 1) * segments + next;
                    triangles.AddRange(new[] { a, c, b, c, d, b });
                }
            if (capBottom && profile[0].x > 0.0001f)
            {
                var center = vertices.Count;
                vertices.Add(new Vector3(0f, profile[0].y, 0f));
                for (var i = 0; i < segments; i++)
                    triangles.AddRange(new[] { center, (i + 1) % segments, i });
            }
            var last = (rings - 1) * segments;
            if (capTop && profile[rings - 1].x > 0.0001f)
            {
                var center = vertices.Count;
                vertices.Add(new Vector3(0f, profile[rings - 1].y, 0f));
                for (var i = 0; i < segments; i++)
                    triangles.AddRange(new[] { center, last + i, last + (i + 1) % segments });
            }
            var mesh = new Mesh();
            mesh.vertices = vertices.ToArray();
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static Material MakeMaterial(Color color, float glow = 0.08f)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
            {
                // Borrow a shader that is certainly in the build from any renderer in the scene.
                foreach (var renderer in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
                {
                    var candidate = renderer.sharedMaterial != null ? renderer.sharedMaterial.shader : null;
                    if (candidate != null && candidate.name.Contains("Lit"))
                    {
                        shader = candidate;
                        break;
                    }
                }
            }
            var material = new Material(shader) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            material.color = color;
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Smoothness"))
                material.SetFloat("_Smoothness", 0.32f);
            if (glow > 0f && material.HasProperty("_EmissionColor"))
            {
                // The hall is dim; a faint glow keeps the other player easy to spot.
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", color * glow);
            }
            return material;
        }

        // ---- per frame ----

        public void SetTarget(Vector3 position, float yaw, float pitch)
        {
            _targetPos = position;
            _targetYaw = yaw;
            _targetPitch = pitch;
        }

        public void Tick(float dt)
        {
            if (dt <= 0f)
                return;
            _time += dt;
            var t = 1f - Mathf.Exp(-15f * dt);
            var pos = _root.transform.position;
            // Snap if far (teleport, reconnect) instead of sliding across the hall.
            var next = (pos - _targetPos).sqrMagnitude > 25f ? _targetPos : Vector3.Lerp(pos, _targetPos, t);
            var flat = next - pos;
            flat.y = 0f;
            _speed = Mathf.Lerp(_speed, flat.magnitude / dt, 1f - Mathf.Exp(-8f * dt));
            _root.transform.position = next;

            _yaw = Mathf.LerpAngle(_yaw, _targetYaw, t);
            _pitch = Mathf.LerpAngle(_pitch, Mathf.Clamp(_targetPitch, -35f, 35f), t);
            _root.transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
            _head.localRotation = Quaternion.Euler(_pitch, 0f, 0f);

            // Walk cycle: bob, sway, arms swing, shoes step. Idle: slow breathing.
            var walk = Mathf.Clamp01(_speed / 2.5f);
            _walkPhase += dt * (5f + _speed * 2.2f);
            var s = Mathf.Sin(_walkPhase);
            var breathe = (1f - walk) * Mathf.Sin(_time * 2.2f) * 0.012f;
            _body.localPosition = Vector3.up * (Mathf.Abs(s) * 0.045f * walk);
            _body.localRotation = Quaternion.Euler(0f, 0f, s * 3.5f * walk);
            _body.localScale = new Vector3(1f, 1f + breathe, 1f);

            var carrying = Held > 0;
            if (Held != _lastHeld)
            {
                // Picked something up or put it down: a quick reach.
                Act("reach");
                _lastHeld = Held;
            }
            var reach = carrying ? -62f : -8f;
            var swing = carrying ? 0f : s * 28f * walk;
            var (left, right, roll) = MoveOffsets(dt);
            _armL.localRotation = Quaternion.Euler(reach + swing + left, 0f, carrying ? 10f : -6f);
            _armR.localRotation = Quaternion.Euler(reach - swing + right, 0f, (carrying ? -10f : 6f) + roll);
            _footL.localPosition = new Vector3(-0.1f, 0.035f + Mathf.Max(0f, s) * 0.04f * walk, 0.22f + s * 0.09f * walk);
            _footR.localPosition = new Vector3(0.1f, 0.035f + Mathf.Max(0f, -s) * 0.04f * walk, 0.22f - s * 0.09f * walk);

            // Blink now and then.
            if (_time >= _nextBlink)
            {
                _blinkUntil = _time + 0.12f;
                _nextBlink = _time + Random.Range(2.5f, 6f);
            }
            var eyeScale = _time < _blinkUntil ? new Vector3(1f, 0.12f, 1f) : Vector3.one;
            foreach (var eye in _eyes)
                eye.localScale = eyeScale;

            // Standing inside the ghost would show its insides: hide it when the camera is right on top of it.
            var camera = Camera.main;
            if (camera != null)
            {
                var offset = camera.transform.position - _root.transform.position;
                offset.y = 0f;
                _visual.SetActive(offset.magnitude > HideCloserThan);
            }
        }

        // ---- small moves of the arms ----

        /// <summary>A short move: "pour" (tips the right hand), "throw" (swings the right arm), "reach" (both arms dip).</summary>
        public void Act(string move)
        {
            _move = move;
            _moveTime = 0f;
        }

        /// <summary>Extra arm angles for the move in progress: left and right swing, right-hand roll (degrees).</summary>
        (float left, float right, float roll) MoveOffsets(float dt)
        {
            if (_move == null)
                return (0f, 0f, 0f);
            _moveTime += dt;
            var length = _move switch { "pour" => 0.9f, "throw" => 0.5f, _ => 0.35f };
            var k = Mathf.Clamp01(_moveTime / length);
            if (k >= 1f)
                _move = null;
            var bump = Mathf.Sin(k * Mathf.PI);
            return _move switch
            {
                "pour" => (0f, -18f * bump, 32f * bump),
                // Back, then forward and up, then home.
                "throw" => (0f, k < 0.35f ? 45f * (k / 0.35f) : Mathf.Lerp(45f, -115f, Mathf.Clamp01((k - 0.35f) / 0.35f)) * (1f - Mathf.Clamp01((k - 0.7f) / 0.3f)), 0f),
                _ => (-28f * bump, -28f * bump, 0f),
            };
        }

        /// <summary>Where the i-th of n carried bottles is: in the hands (one between them, two one each, more side by side).</summary>
        public (Vector3 position, Quaternion rotation) CarryPose(int index, int count)
        {
            var root = _root.transform;
            var along = count <= 1 ? 0.5f : index / (float)(count - 1);
            var position = Vector3.Lerp(_handL.position, _handR.position, along) + root.forward * 0.05f;
            return (position, root.rotation);
        }

        public void Destroy()
        {
            if (_root != null)
                Object.Destroy(_root);
        }
    }
}
