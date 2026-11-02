using UnityEngine;
using Object = UnityEngine.Object;

namespace PotionCoop.Game
{
    /// <summary>
    /// The other player's body: plain primitives (no colliders, no injected components),
    /// moved every frame from <see cref="Coop"/> towards the last received pose.
    /// </summary>
    internal sealed class Ghost
    {
        const float BodyHeight = 1.5f;
        const float EyeHeight = 1.6f;

        readonly GameObject _root;
        readonly Transform _head;
        Vector3 _targetPos;
        float _targetYaw;
        float _targetPitch;
        float _yaw;
        float _pitch;

        public string Name;
        public bool InMenu;
        public int Held;
        public Vector3 Position => _root.transform.position;
        public Vector3 LabelAnchor => _root.transform.position + Vector3.up * (EyeHeight + 0.35f);
        public bool Alive => _root != null;

        public Ghost(Color color, Vector3 position)
        {
            var material = MakeMaterial(color);

            _root = new GameObject("PotionCoop.Ghost");
            Object.DontDestroyOnLoad(_root);
            _root.transform.position = position;

            var body = Primitive(PrimitiveType.Capsule, _root.transform, material);
            body.transform.localPosition = Vector3.up * (BodyHeight / 2f);
            body.transform.localScale = new Vector3(0.55f, BodyHeight / 2f, 0.55f);

            _head = new GameObject("Head").transform;
            _head.SetParent(_root.transform, false);
            _head.localPosition = Vector3.up * EyeHeight;

            var skull = Primitive(PrimitiveType.Sphere, _head, material);
            skull.transform.localScale = Vector3.one * 0.38f;

            // A short "nose" so you can tell where they are looking.
            var nose = Primitive(PrimitiveType.Cube, _head, MakeMaterial(Color.white));
            nose.transform.localPosition = new Vector3(0f, 0f, 0.25f);
            nose.transform.localScale = new Vector3(0.08f, 0.08f, 0.22f);

            _targetPos = position;
        }

        static GameObject Primitive(PrimitiveType type, Transform parent, Material material)
        {
            var go = GameObject.CreatePrimitive(type);
            var collider = go.GetComponent<Collider>();
            if (collider != null)
                Object.Destroy(collider);
            go.GetComponent<Renderer>().sharedMaterial = material;
            go.transform.SetParent(parent, false);
            return go;
        }

        static Material MakeMaterial(Color color)
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
            if (material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", color * 0.4f);
            }
            return material;
        }

        public void SetTarget(Vector3 position, float yaw, float pitch)
        {
            _targetPos = position;
            _targetYaw = yaw;
            _targetPitch = pitch;
        }

        public void Tick(float dt)
        {
            var t = 1f - Mathf.Exp(-15f * dt);
            var pos = _root.transform.position;
            // Snap if far (teleport, reconnect) instead of sliding across the hall.
            _root.transform.position = (pos - _targetPos).sqrMagnitude > 25f ? _targetPos : Vector3.Lerp(pos, _targetPos, t);
            _yaw = Mathf.LerpAngle(_yaw, _targetYaw, t);
            _pitch = Mathf.LerpAngle(_pitch, _targetPitch, t);
            _root.transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
            _head.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        public void Destroy()
        {
            if (_root != null)
                Object.Destroy(_root);
        }
    }
}
