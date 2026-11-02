using System.Collections.Generic;
using System.Linq;
using Il2CppCodeBase.Components.Cat;
using PotionCoop.Net;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PotionCoop.Game
{
    /// <summary>
    /// One cat for both players: the host's cat lives normally and is broadcast (pose + animator parameters);
    /// on the client the cat's own behaviours are switched off and it mirrors the host's cat.
    /// </summary>
    internal static class CatSync
    {
        const float SendInterval = 1f / 15f;
        // Kept running on the client: the facade (registry/interaction), highlight on aim, and the spine fader.
        static readonly HashSet<string> KeepEnabled = new() { "CatFacade", "CatHighlighter", "CatSpineFader" };

        static CatFacade _cat;
        static Animator _animator;
        static AnimatorControllerParameter[] _parameters;
        static readonly List<Behaviour> SwitchedOff = new();
        static float _nextSend;
        static bool _loggedMissing;

        static Vector3 _targetPos;
        static Quaternion _targetRot;
        static bool _haveTarget;

        static bool Resolve()
        {
            if (_cat != null)
                return true;
            _cat = Object.FindFirstObjectByType<CatFacade>();
            if (_cat == null)
            {
                if (!_loggedMissing)
                {
                    _loggedMissing = true;
                    CoopMod.Log.Msg("no cat in this lab (yet)");
                }
                return false;
            }
            _animator = _cat.GetComponentInChildren<Animator>(true);
            _parameters = _animator != null ? _animator.parameters.ToArray() : new AnimatorControllerParameter[0];
            CoopMod.Log.Msg($"cat found: animator={(_animator != null)}, {_parameters.Length} parameter(s)");
            return true;
        }

        // ---- host ----

        public static void HostTick(TcpSession session, float now)
        {
            if (now < _nextSend || !Resolve())
                return;
            _nextSend = now + SendInterval;

            var t = _cat.transform;
            var msg = new CatMsg
            {
                Pos = new[] { t.position.x, t.position.y, t.position.z },
                Rot = new[] { t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w },
            };
            if (_animator != null && _animator.isActiveAndEnabled)
            {
                foreach (var p in _parameters)
                {
                    switch (p.type)
                    {
                        case AnimatorControllerParameterType.Float:
                            (msg.F ??= new Dictionary<int, float>())[p.nameHash] = _animator.GetFloat(p.nameHash);
                            break;
                        case AnimatorControllerParameterType.Int:
                            (msg.I ??= new Dictionary<int, int>())[p.nameHash] = _animator.GetInteger(p.nameHash);
                            break;
                        case AnimatorControllerParameterType.Bool:
                            (msg.B ??= new Dictionary<int, bool>())[p.nameHash] = _animator.GetBool(p.nameHash);
                            break;
                    }
                }
            }
            session.Send(msg);
        }

        // ---- client ----

        public static void OnCat(CatMsg msg)
        {
            if (!Resolve())
                return;
            TakeOver();
            _targetPos = new Vector3(msg.Pos[0], msg.Pos[1], msg.Pos[2]);
            _targetRot = new Quaternion(msg.Rot[0], msg.Rot[1], msg.Rot[2], msg.Rot[3]);
            if (!_haveTarget)
                _cat.transform.SetPositionAndRotation(_targetPos, _targetRot);
            _haveTarget = true;

            if (_animator == null)
                return;
            if (msg.F != null)
                foreach (var pair in msg.F)
                    _animator.SetFloat(pair.Key, pair.Value);
            if (msg.I != null)
                foreach (var pair in msg.I)
                    _animator.SetInteger(pair.Key, pair.Value);
            if (msg.B != null)
                foreach (var pair in msg.B)
                    _animator.SetBool(pair.Key, pair.Value);
        }

        public static void ClientTick(float dt)
        {
            if (!_haveTarget || _cat == null)
                return;
            var t = _cat.transform;
            var k = 1f - Mathf.Exp(-12f * dt);
            var pos = (t.position - _targetPos).sqrMagnitude > 9f ? _targetPos : Vector3.Lerp(t.position, _targetPos, k);
            t.SetPositionAndRotation(pos, Quaternion.Slerp(t.rotation, _targetRot, k));
        }

        /// <summary>Switch off the client cat's own brain so it only mirrors the host's.</summary>
        static void TakeOver()
        {
            if (SwitchedOff.Count > 0)
                return;
            foreach (var behaviour in _cat.GetComponentsInChildren<MonoBehaviour>(true))
            {
                var type = behaviour.GetIl2CppType();
                if (type == null || type.Namespace != "CodeBase.Components.Cat" || KeepEnabled.Contains(type.Name) || !behaviour.enabled)
                    continue;
                behaviour.enabled = false;
                SwitchedOff.Add(behaviour);
            }
            CoopMod.Log.Msg($"client cat now mirrors the host's ({SwitchedOff.Count} behaviour(s) paused)");
        }

        /// <summary>Link closed: give the client its own cat back.</summary>
        public static void Release()
        {
            foreach (var behaviour in SwitchedOff)
                if (behaviour != null)
                    behaviour.enabled = true;
            if (SwitchedOff.Count > 0)
                CoopMod.Log.Msg("cat back to its own ways");
            SwitchedOff.Clear();
            _haveTarget = false;
        }

        /// <summary>Level unloaded: the cat object is gone.</summary>
        public static void Forget()
        {
            SwitchedOff.Clear();
            _cat = null;
            _animator = null;
            _parameters = null;
            _haveTarget = false;
            _loggedMissing = false;
        }
    }
}
