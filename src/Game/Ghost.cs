using UnityEngine;
using Object = UnityEngine.Object;

namespace PotionCoop.Game
{
    /// <summary>
    /// The other player's body: a little alchemist built from simple shapes (robe, head, pointy hat, hands),
    /// with no colliders and no injected components. Moved every frame from <see cref="Coop"/>.
    /// </summary>
    internal sealed class Ghost
    {
        const float HeadHeight = 1.47f;
        static readonly Color Skin = new(0.96f, 0.80f, 0.66f);

        readonly GameObject _root;
        readonly Transform _body;
        readonly Transform _head;
        Vector3 _targetPos;
        float _targetYaw;
        float _targetPitch;
        float _yaw;
        float _pitch;
        float _speed;
        float _walkPhase;

        public string Name;
        public bool InMenu;
        public int Held;
        public Vector3 Position => _root.transform.position;
        public Vector3 LabelAnchor => _root.transform.position + Vector3.up * 2.25f;
        public bool Alive => _root != null;

        public Ghost(Color color, Vector3 position)
        {
            var robe = MakeMaterial(color);
            var trim = MakeMaterial(color * 0.55f);
            var skin = MakeMaterial(Skin);
            var dark = MakeMaterial(new Color(0.08f, 0.06f, 0.05f));
            var gold = MakeMaterial(new Color(1f, 0.76f, 0.25f), glow: 0.45f);
            var beard = MakeMaterial(new Color(0.93f, 0.92f, 0.88f));

            _root = new GameObject("PotionCoop.Ghost");
            Object.DontDestroyOnLoad(_root);
            _root.transform.position = position;

            // Everything below the root bobs while walking.
            _body = new GameObject("Body").transform;
            _body.SetParent(_root.transform, false);

            Mesh("Robe", _body, Frustum(0.38f, 0.17f, 1.25f), robe, Vector3.zero, Quaternion.identity, Vector3.one);
            Shape(PrimitiveType.Sphere, "Shoulders", _body, trim, new Vector3(0f, 1.22f, 0f), new Vector3(0.44f, 0.16f, 0.40f));
            Shape(PrimitiveType.Cylinder, "Belt", _body, trim, new Vector3(0f, 0.78f, 0f), new Vector3(0.53f, 0.035f, 0.53f));
            Shape(PrimitiveType.Cube, "Buckle", _body, gold, new Vector3(0f, 0.78f, 0.255f), new Vector3(0.09f, 0.07f, 0.02f));
            // Sleeves run from the shoulders down and forward to the hands.
            Shape(PrimitiveType.Cylinder, "SleeveL", _body, robe, new Vector3(-0.205f, 1.09f, 0.1f), new Vector3(0.11f, 0.15f, 0.11f), Quaternion.Euler(-42f, 0f, 0f));
            Shape(PrimitiveType.Cylinder, "SleeveR", _body, robe, new Vector3(0.205f, 1.09f, 0.1f), new Vector3(0.11f, 0.15f, 0.11f), Quaternion.Euler(-42f, 0f, 0f));
            Shape(PrimitiveType.Sphere, "HandL", _body, skin, new Vector3(-0.21f, 0.98f, 0.2f), Vector3.one * 0.1f);
            Shape(PrimitiveType.Sphere, "HandR", _body, skin, new Vector3(0.21f, 0.98f, 0.2f), Vector3.one * 0.1f);

            _head = new GameObject("Head").transform;
            _head.SetParent(_body, false);
            _head.localPosition = Vector3.up * HeadHeight;

            Shape(PrimitiveType.Sphere, "Face", _head, skin, Vector3.zero, Vector3.one * 0.34f);
            Shape(PrimitiveType.Sphere, "EyeL", _head, dark, new Vector3(-0.065f, 0.03f, 0.155f), Vector3.one * 0.05f);
            Shape(PrimitiveType.Sphere, "EyeR", _head, dark, new Vector3(0.065f, 0.03f, 0.155f), Vector3.one * 0.05f);
            Shape(PrimitiveType.Sphere, "Nose", _head, skin, new Vector3(0f, -0.02f, 0.17f), Vector3.one * 0.06f);
            // A pointy white beard hanging from the chin.
            Mesh("Beard", _head, Frustum(0.11f, 0.008f, 0.24f), beard, new Vector3(0f, -0.07f, 0.09f),
                Quaternion.Euler(165f, 0f, 0f), Vector3.one);
            Shape(PrimitiveType.Cylinder, "Brim", _head, trim, new Vector3(0f, 0.12f, 0f), new Vector3(0.5f, 0.012f, 0.5f));
            var hat = new GameObject("Hat").transform;
            hat.SetParent(_head, false);
            hat.localPosition = new Vector3(0f, 0.12f, -0.01f);
            hat.localRotation = Quaternion.Euler(-12f, 0f, 0f);
            Mesh("Cone", hat, Frustum(0.17f, 0.012f, 0.46f), robe, Vector3.zero, Quaternion.identity, Vector3.one);
            Shape(PrimitiveType.Cylinder, "Band", hat, gold, new Vector3(0f, 0.04f, 0f), new Vector3(0.335f, 0.025f, 0.335f));
            Shape(PrimitiveType.Sphere, "Star", hat, gold, new Vector3(0f, 0.17f, 0.115f), new Vector3(0.07f, 0.07f, 0.02f));

            _targetPos = position;
        }

        // ---- construction helpers ----

        static void Shape(PrimitiveType type, string name, Transform parent, Material material, Vector3 localPos, Vector3 scale,
            Quaternion? localRot = null)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var collider = go.GetComponent<Collider>();
            if (collider != null)
                Object.Destroy(collider);
            go.GetComponent<Renderer>().sharedMaterial = material;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot ?? Quaternion.identity;
            go.transform.localScale = scale;
        }

        static void Mesh(string name, Transform parent, Mesh mesh, Material material, Vector3 localPos, Quaternion localRot, Vector3 scale)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        /// <summary>A capped cone/frustum standing on y = 0.</summary>
        static Mesh Frustum(float bottomRadius, float topRadius, float height, int segments = 24)
        {
            var vertices = new Vector3[segments * 2 + 2];
            var triangles = new int[segments * 12];
            for (var i = 0; i < segments; i++)
            {
                var a = i * Mathf.PI * 2f / segments;
                var dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                vertices[i] = dir * bottomRadius;
                vertices[segments + i] = dir * topRadius + Vector3.up * height;
            }
            var bottomCenter = segments * 2;
            var topCenter = bottomCenter + 1;
            vertices[bottomCenter] = Vector3.zero;
            vertices[topCenter] = Vector3.up * height;

            var t = 0;
            for (var i = 0; i < segments; i++)
            {
                var next = (i + 1) % segments;
                // side
                triangles[t++] = i; triangles[t++] = segments + i; triangles[t++] = next;
                triangles[t++] = next; triangles[t++] = segments + i; triangles[t++] = segments + next;
                // bottom cap
                triangles[t++] = bottomCenter; triangles[t++] = i; triangles[t++] = next;
                // top cap
                triangles[t++] = topCenter; triangles[t++] = segments + next; triangles[t++] = segments + i;
            }

            var mesh = new Mesh();
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static Material MakeMaterial(Color color, float glow = 0.18f)
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
            var material = new Material(shader);
            material.color = color;
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Smoothness"))
                material.SetFloat("_Smoothness", 0.25f);
            if (material.HasProperty("_EmissionColor"))
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

            // A little waddle while walking.
            var walk = Mathf.Clamp01(_speed / 3f);
            _walkPhase += dt * (6f + _speed * 2.5f);
            _body.localPosition = Vector3.up * (Mathf.Abs(Mathf.Sin(_walkPhase)) * 0.05f * walk);
            _body.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(_walkPhase) * 4f * walk);
        }

        /// <summary>Where the i-th of n carried bottles floats: in front of the chest, side by side.</summary>
        public (Vector3 position, Quaternion rotation) CarryPose(int index, int count)
        {
            var root = _root.transform;
            var spread = (index - (count - 1) / 2f) * 0.13f;
            var position = _body.position + Vector3.up * 0.92f + root.forward * 0.36f + root.right * spread;
            return (position, root.rotation);
        }

        public void Destroy()
        {
            if (_root != null)
                Object.Destroy(_root);
        }
    }
}
