using System;
using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;

namespace AssetStudio
{
    /// <summary>
    /// Converts a Unity <see cref="ParticleSystem"/> (read generically from its type tree, so no
    /// hand-written class is needed and it stays version-robust) into a Godot 4 scene (.tscn) holding a
    /// GPUParticles3D node with an embedded ParticleProcessMaterial. Constant / two-constant values map
    /// directly; curve/gradient-driven values are approximated by their scalar and flagged as TODO.
    /// </summary>
    public static class GodotParticleExporter
    {
        public sealed class Result
        {
            public string Name;
            public string Tscn;
            public bool Ok;
        }

        // Unity ParticleSystemShapeType (subset we map)
        private static string MapShape(int unityType, out string extraLines, OrderedDictionary shape)
        {
            extraLines = "";
            float radius = GetF(shape, "radius", 1f);
            if (radius <= 0f) radius = 1f;
            switch (unityType)
            {
                case 0: case 1: case 2: case 3: // Sphere / SphereShell / Hemisphere(Shell)
                    extraLines = $"emission_sphere_radius = {F(radius)}\n";
                    return "1"; // Sphere
                case 5: case 15: case 16: // Box / BoxShell / BoxEdge
                    extraLines = $"emission_box_extents = Vector3({F(radius)}, {F(radius)}, {F(radius)})\n";
                    return "3"; // Box
                case 10: case 11: case 17: // Circle / CircleEdge / Donut
                    extraLines = $"emission_ring_radius = {F(radius)}\nemission_ring_inner_radius = 0.0\nemission_ring_height = 0.0\nemission_ring_axis = Vector3(0, 1, 0)\n";
                    return "6"; // Ring
                default: // Cone family and others -> point emitter with spread (set by caller)
                    return "0"; // Point
            }
        }

        public static Result Export(OrderedDictionary root, string name)
        {
            var res = new Result { Name = Sanitize(string.IsNullOrEmpty(name) ? "particles" : name) };
            if (root == null) { res.Tscn = ""; return res; }

            float duration = GetF(root, "lengthInSec", 3f);
            bool looping = GetB(root, "looping", true);
            bool prewarm = GetB(root, "prewarm", false);
            float simSpeed = GetF(root, "simulationSpeed", 1f);

            var initial = GetD(root, "InitialModule");
            var emission = GetD(root, "EmissionModule");
            var shape = GetD(root, "ShapeModule");

            // lifetime
            (float lifeMin, float lifeMax) = ReadCurve(initial, "startLifetime", 3f);
            float lifetime = lifeMax > 0 ? lifeMax : 3f;
            float lifeRand = lifeMax > 0 ? Clamp01((lifeMax - lifeMin) / lifeMax) : 0f;

            // speed / size / rotation / gravity
            (float spMin, float spMax) = ReadCurve(initial, "startSpeed", 1f);
            (float szMin, float szMax) = ReadCurve(initial, "startSize", 1f);
            (float rotMin, float rotMax) = ReadCurve(initial, "startRotation", 0f);
            (float _, float gravMax) = ReadCurve(initial, "gravityModifier", 0f);
            int maxParticles = GetI(initial, "maxNumParticles", 0);

            // emission rate -> amount fallback
            (float _, float rate) = ReadCurve(emission, "rateOverTime", 0f);
            int amount = maxParticles > 0 ? maxParticles : (int)Math.Ceiling(Math.Max(1f, rate * lifetime));
            if (amount <= 0) amount = 8;
            if (amount > 100000) amount = 100000;

            // start color (constant part of the MinMaxGradient)
            var startColor = ReadColor(GetD(initial, "startColor"));

            // shape
            int shapeType = shape != null ? GetI(shape, "type", -1) : -1;
            float shapeAngle = shape != null ? GetF(shape, "angle", 25f) : 25f;
            string emissionShape = "0";
            string shapeExtra = "";
            string spread = "45.0";
            if (shape != null && GetB(shape, "enabled", true))
            {
                emissionShape = MapShape(shapeType, out shapeExtra, shape);
                if (emissionShape == "0") // cone-ish -> point + spread from half-angle
                    spread = F(Math.Min(90f, Math.Max(0f, shapeAngle)));
            }

            float angMinDeg = rotMin * 57.29578f;
            float angMaxDeg = rotMax * 57.29578f;
            float gravY = -9.8f * gravMax;

            var mat = new StringBuilder();
            mat.Append("[sub_resource type=\"ParticleProcessMaterial\" id=\"ppm_1\"]\n");
            mat.Append("emission_shape = ").Append(emissionShape).Append('\n');
            mat.Append(shapeExtra);
            mat.Append("direction = Vector3(0, 1, 0)\n");
            mat.Append("spread = ").Append(spread).Append('\n');
            mat.Append("initial_velocity_min = ").Append(F(spMin)).Append('\n');
            mat.Append("initial_velocity_max = ").Append(F(spMax)).Append('\n');
            mat.Append("gravity = Vector3(0, ").Append(F(gravY)).Append(", 0)\n");
            mat.Append("scale_min = ").Append(F(szMin)).Append('\n');
            mat.Append("scale_max = ").Append(F(szMax)).Append('\n');
            if (Math.Abs(angMinDeg) > 0.001f || Math.Abs(angMaxDeg) > 0.001f)
            {
                mat.Append("angle_min = ").Append(F(angMinDeg)).Append('\n');
                mat.Append("angle_max = ").Append(F(angMaxDeg)).Append('\n');
            }
            if (startColor != null)
                mat.Append("color = Color(").Append(startColor).Append(")\n");

            var sb = new StringBuilder();
            sb.Append("[gd_scene load_steps=2 format=3]\n\n");
            sb.Append("; Generated by Reunity (AssetStudioMod) from Unity ParticleSystem \"").Append(name).Append("\".\n");
            sb.Append("; Constant values map directly; curve/gradient-driven fields are approximated by\n");
            sb.Append("; their scalar (see color_ramp / *_curve in Godot to restore over-lifetime behaviour).\n\n");
            sb.Append(mat).Append('\n');
            sb.Append("[node name=\"").Append(res.Name).Append("\" type=\"GPUParticles3D\"]\n");
            sb.Append("amount = ").Append(amount).Append('\n');
            sb.Append("lifetime = ").Append(F(lifetime)).Append('\n');
            sb.Append("one_shot = ").Append(looping ? "false" : "true").Append('\n');
            if (prewarm) sb.Append("preprocess = ").Append(F(duration)).Append('\n');
            if (Math.Abs(simSpeed - 1f) > 0.001f) sb.Append("speed_scale = ").Append(F(simSpeed)).Append('\n');
            if (lifeRand > 0.001f) sb.Append("lifetime_randomness = ").Append(F(lifeRand)).Append('\n');
            sb.Append("process_material = SubResource(\"ppm_1\")\n");

            res.Tscn = sb.ToString();
            res.Ok = true;
            return res;
        }

        // ---- type-tree dictionary helpers ----
        private static OrderedDictionary GetD(OrderedDictionary d, string key)
            => d != null && d.Contains(key) ? d[key] as OrderedDictionary : null;

        private static float GetF(OrderedDictionary d, string key, float def)
        {
            if (d == null || !d.Contains(key) || d[key] == null) return def;
            try { return Convert.ToSingle(d[key], CultureInfo.InvariantCulture); } catch { return def; }
        }

        private static int GetI(OrderedDictionary d, string key, int def)
        {
            if (d == null || !d.Contains(key) || d[key] == null) return def;
            try { return Convert.ToInt32(d[key], CultureInfo.InvariantCulture); } catch { return def; }
        }

        private static bool GetB(OrderedDictionary d, string key, bool def)
        {
            if (d == null || !d.Contains(key) || d[key] == null) return def;
            try { return Convert.ToBoolean(d[key]); } catch { return def; }
        }

        // MinMaxCurve: minMaxState 0=const(scalar), 1=curve, 2=twoCurves, 3=twoConstants(minScalar..scalar)
        private static (float min, float max) ReadCurve(OrderedDictionary parent, string key, float def)
        {
            var c = GetD(parent, key);
            if (c == null) return (def, def);
            int state = GetI(c, "minMaxState", 0);
            float scalar = GetF(c, "scalar", def);
            float minScalar = GetF(c, "minScalar", scalar);
            if (state == 3) return (minScalar, scalar);       // two constants
            return (scalar, scalar);                          // constant / curve (approx by scalar)
        }

        // MinMaxGradient: use the constant color (maxColor) when available.
        private static string ReadColor(OrderedDictionary grad)
        {
            if (grad == null) return null;
            var col = GetD(grad, "maxColor") ?? GetD(grad, "minColor");
            if (col == null) return null;
            float r = GetF(col, "r", 1f), g = GetF(col, "g", 1f), b = GetF(col, "b", 1f), a = GetF(col, "a", 1f);
            return $"{F(r)}, {F(g)}, {F(b)}, {F(a)}";
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static string F(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v) || v == 0f) return "0.0"; // also normalizes -0.0
            return v.ToString("0.0######", CultureInfo.InvariantCulture);
        }

        private static string Sanitize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
                sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.' ? ch : '_');
            return sb.ToString();
        }
    }
}
