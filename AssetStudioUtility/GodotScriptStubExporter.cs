using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;

namespace AssetStudio
{
    /// <summary>
    /// Generates a Godot 4 GDScript stub from a Unity MonoBehaviour's script class and its serialized fields
    /// (read from the type tree; works for both Mono and IL2CPP once the assemblies/dummy-assemblies are
    /// loaded). The stub declares the serialized fields as @export vars with default values captured from a
    /// representative instance, and leaves _ready()/_process() as TODOs — the Unity logic is not translated.
    /// </summary>
    public static class GodotScriptStubExporter
    {
        // Godot builtin types, keywords, and common native classes. A class_name or field identifier that
        // matches one of these must be renamed (Godot rejects "class Button hides a native class" and
        // "member Color cannot have the same name as a builtin type").
        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
        {
            // builtin types
            "bool","int","float","String","StringName","NodePath","Vector2","Vector2i","Vector3","Vector3i",
            "Vector4","Vector4i","Rect2","Rect2i","Transform2D","Transform3D","Plane","Quaternion","AABB",
            "Basis","Color","RID","Object","Callable","Signal","Dictionary","Array","PackedByteArray",
            "PackedInt32Array","PackedInt64Array","PackedFloat32Array","PackedFloat64Array","PackedStringArray",
            "PackedVector2Array","PackedVector3Array","PackedColorArray","Variant",
            // gdscript keywords
            "if","elif","else","for","while","match","break","continue","pass","return","class","class_name",
            "extends","is","in","as","self","tool","signal","func","static","const","enum","var","breakpoint",
            "preload","await","yield","assert","void","super","true","false","null","and","or","not","print",
            // common native classes that scripts often shadow
            "Node","Node2D","Node3D","Control","Button","Label","Slider","Image","Timer","Sprite2D","Sprite3D",
            "Camera2D","Camera3D","Light2D","Light3D","Panel","Line2D","Path2D","Path3D","Curve","Gradient",
            "Area2D","Area3D","Resource","Material","Shader","Mesh","MeshInstance3D","Texture","Texture2D",
            "AudioStreamPlayer","AnimationPlayer","CollisionShape2D","CollisionShape3D","RigidBody2D","RigidBody3D",
            "CharacterBody2D","CharacterBody3D","TileMap","Window","Viewport","Container","Range","Tree","Time",
        };

        // Base fields from Object/Component/Behaviour/MonoBehaviour that are not user data.
        private static readonly HashSet<string> BaseFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_GameObject", "m_Enabled", "m_Script", "m_Name", "m_ObjectHideFlags",
            "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset",
            "m_EditorHideFlags", "m_EditorClassIdentifier", "m_PrefabInternal", "m_PrefabParentObject",
        };

        public sealed class Result
        {
            public string ClassName;   // sanitized Godot class_name
            public string Gd;          // .gd text
        }

        public static Result Export(string unityClass, string unityNamespace, string assembly, OrderedDictionary fields)
        {
            var cls = SanitizeIdent(string.IsNullOrEmpty(unityClass) ? "UnityScript" : unityClass);
            if (Reserved.Contains(cls))
                cls += "Script"; // avoid shadowing a Godot native class / builtin type
            var full = string.IsNullOrEmpty(unityNamespace) ? unityClass : unityNamespace + "." + unityClass;

            var sb = new StringBuilder();
            sb.Append("# UnityRift stub for Unity MonoBehaviour ").Append(full);
            if (!string.IsNullOrEmpty(assembly)) sb.Append(" [").Append(assembly).Append(']');
            sb.Append('\n');
            sb.Append("# Serialized field defaults captured from an asset instance. The Unity logic is NOT\n");
            sb.Append("# translated - port Awake()/Start()/Update() etc. by hand into the methods below.\n\n");
            sb.Append("class_name ").Append(cls).Append('\n');
            sb.Append("extends Node\n\n");

            var emitted = new HashSet<string>(StringComparer.Ordinal);
            int count = 0;
            if (fields != null)
            {
                foreach (DictionaryEntry kv in fields)
                {
                    var name = kv.Key as string;
                    if (name == null || BaseFields.Contains(name) || !emitted.Add(name))
                        continue;
                    var line = FieldLine(name, kv.Value);
                    if (line != null) { sb.Append(line).Append('\n'); count++; }
                }
            }
            if (count == 0)
                sb.Append("# (no simple serialized fields detected)\n");

            sb.Append("\nfunc _ready() -> void:\n\tpass # TODO: port Unity Awake()/OnEnable()/Start()\n\n");
            sb.Append("func _process(delta: float) -> void:\n\tpass # TODO: port Unity Update()\n");

            return new Result { ClassName = cls, Gd = sb.ToString() };
        }

        // Returns an "@export var name: Type = value" line, or null for unsupported/reference fields.
        private static string FieldLine(string name, object value)
        {
            var id = SanitizeIdent(name.StartsWith("m_") ? name.Substring(2) : name);
            if (Reserved.Contains(id))
                id += "_"; // a field named e.g. "Color" or "Timer" would clash with a builtin/native name
            switch (value)
            {
                case null:
                    return null;
                case bool b:
                    return $"@export var {id}: bool = {(b ? "true" : "false")}";
                case string s:
                    return $"@export var {id}: String = {Quote(s)}";
                case float f:
                    return $"@export var {id}: float = {F(f)}";
                case double d:
                    return $"@export var {id}: float = {F((float)d)}";
                case sbyte _:
                case byte _:
                case short _:
                case ushort _:
                case int _:
                case uint _:
                case long _:
                case ulong _:
                    return $"@export var {id}: int = {Convert.ToInt64(value, CultureInfo.InvariantCulture)}";
                case OrderedDictionary od:
                    return StructLine(id, od);
                case object[] _:
                    return $"@export var {id}: Array = []  # TODO: fill array";
                default:
                    return null;
            }
        }

        private static string StructLine(string id, OrderedDictionary od)
        {
            bool Has(string k) => od.Contains(k) && od[k] != null;
            float G(string k) => Has(k) ? SafeF(od[k]) : 0f;
            // Color
            if (Has("r") && Has("g") && Has("b"))
                return $"@export var {id}: Color = Color({F(G("r"))}, {F(G("g"))}, {F(G("b"))}, {F(Has("a") ? G("a") : 1f)})";
            // Vector4 / Quaternion
            if (Has("x") && Has("y") && Has("z") && Has("w"))
                return $"@export var {id}: Quaternion = Quaternion({F(G("x"))}, {F(G("y"))}, {F(G("z"))}, {F(G("w"))})";
            // Vector3
            if (Has("x") && Has("y") && Has("z"))
                return $"@export var {id}: Vector3 = Vector3({F(G("x"))}, {F(G("y"))}, {F(G("z"))})";
            // Vector2
            if (Has("x") && Has("y"))
                return $"@export var {id}: Vector2 = Vector2({F(G("x"))}, {F(G("y"))})";
            // PPtr reference or nested object -> not a simple export
            if (Has("m_PathID") || Has("m_FileID"))
                return $"# {id}: object reference (Unity PPtr) - wire up a NodePath/Resource by hand";
            return $"# {id}: nested struct - port by hand";
        }

        private static float SafeF(object o)
        {
            try { return Convert.ToSingle(o, CultureInfo.InvariantCulture); } catch { return 0f; }
        }

        private static string F(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v) || v == 0f) return "0.0";
            return v.ToString("0.0######", CultureInfo.InvariantCulture);
        }

        private static string Quote(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
        }

        private static string SanitizeIdent(string name)
        {
            if (string.IsNullOrEmpty(name)) return "_x";
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
                sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }
    }
}
