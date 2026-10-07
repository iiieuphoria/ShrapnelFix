using System.Reflection;
using EFT;
using EFT.Ballistics;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShrapnelFix
{
    // F: draws recent fragment rays with GL (Debug.DrawLine doesn't render in builds).
    internal class DebugLines : MonoBehaviour
    {
        internal static readonly Color Band = Color.green;
        internal static readonly Color Stray = Color.yellow;
        internal static readonly Color Aimed = Color.red;

        private const int Capacity = 1024;
        private const float Lifetime = 3f;
        private const float MaxLength = 30f;

        private struct Line
        {
            public Vector3 A, B;
            public Color Color;
            public float Until;
        }

        private static readonly Line[] Lines = new Line[Capacity];
        private static int _next;
        private static float _latest;
        private static DebugLines _instance;
        private static Material _material;

        internal static void AddRay(Vector3 origin, Vector3 dir, Color color)
        {
            if (_instance == null)
            {
                var go = new GameObject("ShrapnelFixDebugLines");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<DebugLines>();
            }

            // Approximate path: first solid thing on a straight line (the real shot also has drop, ricochets, penetration).
            float length = Physics.Raycast(origin, dir, out RaycastHit hit, MaxLength, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                ? hit.distance
                : MaxLength;
            _latest = Time.time + Lifetime;
            Lines[_next] = new Line { A = origin, B = origin + dir * length, Color = color, Until = _latest };
            _next = (_next + 1) % Capacity;
        }

        private void OnRenderObject()
        {
            if (Time.time > _latest) return;
            if (_material == null)
            {
                var shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null) return;
                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                _material.SetInt("_ZTest", (int)CompareFunction.Always); // see rays through walls
                _material.SetInt("_Cull", (int)CullMode.Off);
                _material.SetInt("_ZWrite", 0);
            }

            _material.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            float now = Time.time;
            foreach (var line in Lines)
            {
                if (line.Until < now) continue;
                GL.Color(line.Color);
                GL.Vertex(line.A);
                GL.Vertex(line.B);
            }
            GL.End();
            GL.PopMatrix();
        }
    }

    // F: logs every grenade-fragment body hit. Only the attacker's machine applies the damage in FIKA, but every
    // machine simulates the hit, so this logs on all of them.
    internal class FragmentHitLogPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            typeof(BodyPartCollider).GetMethod(nameof(BodyPartCollider.ApplyHit));

        [PatchPostfix]
        public static void Postfix(BodyPartCollider __instance, DamageInfo damageInfo)
        {
            if (!Plugin.DebugLog.Value || damageInfo.DamageType != EDamageType.GrenadeFragment) return;
            Logger.LogInfo($"[hit] {__instance.Player?.Profile?.Nickname ?? "?"} {__instance.BodyPartType} dmg={damageInfo.Damage:F0} " +
                           $"dist={Vector3.Distance(Blast.Origin, damageInfo.HitPoint):F1}m from last explosion origin");
        }
    }
}
