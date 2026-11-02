using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PotionCoop.Game
{
    /// <summary>
    /// The other player's body: a little alchemist built at runtime from generated meshes (smooth robe, swinging arms,
    /// stepping shoes, a bent pointy hat with a star, a white beard, blinking eyes). No colliders, no injected components;
    /// <see cref="Coop"/> moves it every frame towards the last received pose.
    /// </summary>
    internal sealed class Ghost
    {
        const float HeadHeight = 1.47f;
        const float HideCloserThan = 0.6f;

        static readonly Color Skin = new(0.95f, 0.78f, 0.64f);
        static readonly Color Gold = new(1f, 0.78f, 0.3f);

        readonly GameObject _root;
        readonly GameObject _visual;
        readonly Transform _body;
        readonly Transform _head;
        readonly Transform _armL, _armR;
        readonly Transform _footL, _footR;
        readonly List<Transform> _eyes = new();

        Vector3 _targetPos;
        float _targetYaw, _targetPitch, _yaw, _pitch;
        float _speed, _walkPhase, _time;
        float _nextBlink = 2f, _blinkUntil;

        public string Name;
        public bool InMenu;
        public int Held;
        public Vector3 Position => _root.transform.position;
        public Vector3 LabelAnchor => _root.transform.position + Vector3.up * 2.3f;
        public bool Alive => _root != null;

        public Ghost(Color playerColor, Vector3 position)
        {
            var robeColor = Color.Lerp(playerColor, new Color(0.5f, 0.5f, 0.5f), 0.18f);
            var robe = MakeMaterial(robeColor);
            var trim = MakeMaterial(robeColor * 0.5f);
            var hatColor = MakeMaterial(robeColor * 0.82f);
            var skin = MakeMaterial(Skin, glow: 0.05f);
            var white = MakeMaterial(new Color(0.96f, 0.95f, 0.92f), glow: 0.05f);
            var dark = MakeMaterial(new Color(0.07f, 0.05f, 0.04f), glow: 0f);
            var shoe = MakeMaterial(new Color(0.22f, 0.13f, 0.08f), glow: 0f);
            var gold = MakeMaterial(Gold, glow: 0.35f);

            _root = new GameObject("PotionCoop.Ghost");
            Object.DontDestroyOnLoad(_root);
            _root.transform.position = position;
            _visual = new GameObject("Visual");
            _visual.transform.SetParent(_root.transform, false);

            // Everything under the body bobs and sways while walking.
            _body = Node("Body", _visual.transform, Vector3.zero);

            // Robe: a smooth bell, wider at the hem, narrowing to the shoulders.
            Mesh("Robe", _body, Lathe(new[]
            {
                new Vector2(0.36f, 0.00f), new Vector2(0.35f, 0.08f), new Vector2(0.31f, 0.35f), new Vector2(0.26f, 0.70f),
                new Vector2(0.22f, 1.00f), new Vector2(0.18f, 1.18f), new Vector2(0.10f, 1.27f), new Vector2(0.0f, 1.29f),
            }), robe);
            Mesh("Hem", _body, Lathe(new[] { new Vector2(0.372f, 0.0f), new Vector2(0.366f, 0.06f), new Vector2(0.35f, 0.075f) }, capTop: false), trim);
            Mesh("Belt", _body, Lathe(new[] { new Vector2(0.268f, 0.68f), new Vector2(0.262f, 0.73f) }, capBottom: false, capTop: false), trim);
            Shape(PrimitiveType.Cube, "Buckle", _body, gold, new Vector3(0f, 0.705f, 0.262f), new Vector3(0.075f, 0.06f, 0.02f));
            Shape(PrimitiveType.Sphere, "Collar", _body, trim, new Vector3(0f, 1.22f, 0f), new Vector3(0.40f, 0.12f, 0.32f));

            // Shoes peek out under the hem and take turns stepping forward.
            _footL = Node("FootL", _body, new Vector3(-0.1f, 0.035f, 0.24f));
            _footR = Node("FootR", _body, new Vector3(0.1f, 0.035f, 0.24f));
            Shape(PrimitiveType.Sphere, "Shoe", _footL, shoe, Vector3.zero, new Vector3(0.12f, 0.07f, 0.22f));
            Shape(PrimitiveType.Sphere, "Shoe", _footR, shoe, Vector3.zero, new Vector3(0.12f, 0.07f, 0.22f));

            // Arms hang from shoulder pivots so they can swing.
            _armL = Arm("ArmL", -1f, robe, skin);
            _armR = Arm("ArmR", 1f, robe, skin);

            _head = Node("Head", _body, Vector3.up * HeadHeight);
            Shape(PrimitiveType.Sphere, "Face", _head, skin, Vector3.zero, Vector3.one * 0.34f);
            Eye(-0.066f, white, dark);
            Eye(0.066f, white, dark);
            Shape(PrimitiveType.Sphere, "Nose", _head, skin, new Vector3(0f, -0.02f, 0.17f), new Vector3(0.07f, 0.065f, 0.07f));
            // Beard: a white wedge from the chin, pointing down and a little forward, clear of the collar.
            var beard = Node("Beard", _head, new Vector3(0f, -0.085f, 0.115f));
            beard.localRotation = Quaternion.Euler(155f, 0f, 0f);
            Mesh("Wedge", beard, Lathe(new[] { new Vector2(0.11f, 0f), new Vector2(0.075f, 0.11f), new Vector2(0.0f, 0.23f) }), white,
                scale: new Vector3(1f, 1f, 0.55f));

            // Hat: a wide brim resting on the head, a cone in two parts with the tip bent back, a gold band and a star.
            var hat = Node("Hat", _head, new Vector3(0f, 0.085f, -0.005f));
            hat.localRotation = Quaternion.Euler(-8f, 0f, 0f);
            Mesh("Brim", hat, Lathe(new[] { new Vector2(0.27f, 0f), new Vector2(0.26f, 0.018f), new Vector2(0.17f, 0.03f) }), hatColor);
            Mesh("Cone", hat, Lathe(new[] { new Vector2(0.165f, 0.02f), new Vector2(0.125f, 0.16f), new Vector2(0.088f, 0.27f) }, capBottom: false), hatColor);
            Mesh("Band", hat, Lathe(new[] { new Vector2(0.168f, 0.025f), new Vector2(0.158f, 0.075f) }, capBottom: false, capTop: false), gold);
            var tip = Node("Tip", hat, new Vector3(0f, 0.27f, 0f));
            tip.localRotation = Quaternion.Euler(-28f, 0f, 0f);
            Mesh("Cone", tip, Lathe(new[] { new Vector2(0.088f, 0f), new Vector2(0.05f, 0.12f), new Vector2(0.012f, 0.24f), new Vector2(0f, 0.25f) }, capBottom: false), hatColor);
            var star = Node("Star", hat, new Vector3(0f, 0.15f, 0.138f));
            star.localRotation = Quaternion.Euler(-16f, 0f, 0f);
            Mesh("Star", star, Star(0.045f, 0.019f), gold);

            _targetPos = position;
        }

        // ---- construction helpers ----

        static Transform Node(string name, Transform parent, Vector3 localPos)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = localPos;
            return t;
        }

        Transform Arm(string name, float side, Material sleeve, Material skin)
        {
            var pivot = Node(name, _body, new Vector3(0.175f * side, 1.17f, 0f));
            // A sleeve flaring towards the cuff, hanging down from the shoulder, and a hand at its end.
            var hang = Node("Hang", pivot, Vector3.zero);
            hang.localRotation = Quaternion.Euler(180f, 0f, 0f);
            Mesh("Sleeve", hang, Lathe(new[] { new Vector2(0.055f, 0f), new Vector2(0.07f, 0.25f), new Vector2(0.085f, 0.38f) }, capBottom: false), sleeve);
            Shape(PrimitiveType.Sphere, "Hand", pivot, skin, new Vector3(0f, -0.42f, 0f), Vector3.one * 0.105f);
            return pivot;
        }

        void Eye(float x, Material white, Material dark)
        {
            var eye = Node("Eye", _head, new Vector3(x, 0.028f, 0.142f));
            Shape(PrimitiveType.Sphere, "White", eye, white, Vector3.zero, Vector3.one * 0.07f);
            Shape(PrimitiveType.Sphere, "Pupil", eye, dark, new Vector3(0f, 0f, 0.024f), Vector3.one * 0.038f);
            _eyes.Add(eye);
        }

        static void Shape(PrimitiveType type, string name, Transform parent, Material material, Vector3 localPos, Vector3 scale)
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
                    int a = k * segments + i, b = (k + 1) * segments + i, c = k * segments + next, d = (k + 1) * segments + next;
                    triangles.AddRange(new[] { a, b, c, c, b, d });
                }
            if (capBottom && profile[0].x > 0.0001f)
            {
                var center = vertices.Count;
                vertices.Add(new Vector3(0f, profile[0].y, 0f));
                for (var i = 0; i < segments; i++)
                    triangles.AddRange(new[] { center, i, (i + 1) % segments });
            }
            var last = (rings - 1) * segments;
            if (capTop && profile[rings - 1].x > 0.0001f)
            {
                var center = vertices.Count;
                vertices.Add(new Vector3(0f, profile[rings - 1].y, 0f));
                for (var i = 0; i < segments; i++)
                    triangles.AddRange(new[] { center, last + (i + 1) % segments, last + i });
            }
            var mesh = new Mesh();
            mesh.vertices = vertices.ToArray();
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>A flat five-pointed star facing +Z, with a little thickness so it reads from the side.</summary>
        static Mesh Star(float outer, float inner)
        {
            var vertices = new List<Vector3> { new(0f, 0f, 0.006f), new(0f, 0f, -0.002f) };
            for (var i = 0; i < 10; i++)
            {
                var a = Mathf.PI / 2f + i * Mathf.PI / 5f;
                var r = i % 2 == 0 ? outer : inner;
                vertices.Add(new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f));
            }
            var triangles = new List<int>();
            for (var i = 0; i < 10; i++)
            {
                int p = 2 + i, q = 2 + (i + 1) % 10;
                triangles.AddRange(new[] { 0, q, p }); // front, slightly raised
                triangles.AddRange(new[] { 1, p, q }); // back
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
            var reach = carrying ? -62f : -8f;
            var swing = carrying ? 0f : s * 28f * walk;
            _armL.localRotation = Quaternion.Euler(reach + swing, 0f, carrying ? 10f : -6f);
            _armR.localRotation = Quaternion.Euler(reach - swing, 0f, carrying ? -10f : 6f);
            _footL.localPosition = new Vector3(-0.1f, 0.035f + Mathf.Max(0f, s) * 0.04f * walk, 0.24f + s * 0.09f * walk);
            _footR.localPosition = new Vector3(0.1f, 0.035f + Mathf.Max(0f, -s) * 0.04f * walk, 0.24f - s * 0.09f * walk);

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

        /// <summary>Where the i-th of n carried bottles floats: between the outstretched hands, side by side.</summary>
        public (Vector3 position, Quaternion rotation) CarryPose(int index, int count)
        {
            var root = _root.transform;
            var spread = (index - (count - 1) / 2f) * 0.12f;
            var position = _body.position + Vector3.up * 0.98f + root.forward * 0.42f + root.right * spread;
            return (position, root.rotation);
        }

        public void Destroy()
        {
            if (_root != null)
                Object.Destroy(_root);
        }
    }
}
