using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AssetStudio
{
    /// <summary>
    /// Renders Mono.Cecil type/member definitions as C#-like declarations (a "stub" view of the
    /// game's managed code) plus optional IL listings for method bodies.
    /// </summary>
    public static class DotNetTypeDumper
    {
        private static readonly Dictionary<string, string> Keywords = new Dictionary<string, string>
        {
            { "System.Void", "void" }, { "System.Object", "object" }, { "System.String", "string" },
            { "System.Boolean", "bool" }, { "System.Char", "char" }, { "System.Byte", "byte" },
            { "System.SByte", "sbyte" }, { "System.Int16", "short" }, { "System.UInt16", "ushort" },
            { "System.Int32", "int" }, { "System.UInt32", "uint" }, { "System.Int64", "long" },
            { "System.UInt64", "ulong" }, { "System.Single", "float" }, { "System.Double", "double" },
            { "System.Decimal", "decimal" }, { "System.IntPtr", "nint" }, { "System.UIntPtr", "nuint" },
        };

        private static readonly HashSet<string> HiddenAttributes = new HashSet<string>
        {
            "System.Runtime.CompilerServices.CompilerGeneratedAttribute",
            "System.Runtime.CompilerServices.ExtensionAttribute",
            "System.Runtime.CompilerServices.AsyncStateMachineAttribute",
            "System.Runtime.CompilerServices.IteratorStateMachineAttribute",
            "System.Runtime.CompilerServices.NullableAttribute",
            "System.Runtime.CompilerServices.NullableContextAttribute",
            "System.Diagnostics.DebuggerHiddenAttribute",
            "System.Diagnostics.DebuggerStepThroughAttribute",
            "System.ParamArrayAttribute",
            "Cpp2ILInjected.TokenAttribute", // IL2CPP dummy DLLs: metadata token, noise (Address/FieldOffset are kept)
        };

        #region Type names

        public static string TypeName(TypeReference t, bool fullName = false)
        {
            if (t == null)
                return "?";
            if (t is ByReferenceType br)
                return "ref " + TypeName(br.ElementType, fullName);
            if (t is ArrayType at)
                return TypeName(at.ElementType, fullName) + "[" + new string(',', at.Rank - 1) + "]";
            if (t is PointerType pt)
                return TypeName(pt.ElementType, fullName) + "*";
            if (t is GenericParameter gp)
                return gp.Name;
            if (t is RequiredModifierType rm)
                return TypeName(rm.ElementType, fullName);
            if (t is OptionalModifierType om)
                return TypeName(om.ElementType, fullName);
            if (t is GenericInstanceType gi)
            {
                var elem = gi.ElementType;
                if (elem.FullName == "System.Nullable`1" && gi.GenericArguments.Count == 1)
                    return TypeName(gi.GenericArguments[0], fullName) + "?";
                var sb = new StringBuilder();
                sb.Append(StripArity(BaseName(elem, fullName)));
                sb.Append('<');
                for (var i = 0; i < gi.GenericArguments.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(TypeName(gi.GenericArguments[i], fullName));
                }
                sb.Append('>');
                return sb.ToString();
            }
            if (Keywords.TryGetValue(t.FullName, out var kw))
                return kw;
            var name = BaseName(t, fullName);
            if (t.HasGenericParameters)
            {
                name = StripArity(name) + "<" + string.Join(", ", t.GenericParameters.Select(p => p.Name)) + ">";
            }
            return name;
        }

        private static string BaseName(TypeReference t, bool fullName)
        {
            if (t.IsNested)
                return BaseName(t.DeclaringType, fullName) + "." + t.Name;
            if (fullName && !string.IsNullOrEmpty(t.Namespace))
                return t.Namespace + "." + t.Name;
            return t.Name;
        }

        private static string StripArity(string name)
        {
            if (name.IndexOf('`') < 0)
                return name;
            var parts = name.Split('.');
            for (var i = 0; i < parts.Length; i++)
            {
                var j = parts[i].IndexOf('`');
                if (j >= 0) parts[i] = parts[i].Substring(0, j);
            }
            return string.Join(".", parts);
        }

        #endregion

        #region Type dump

        /// <summary>C#-like stub of the whole type (members, nested types), optionally with IL bodies.</summary>
        public static string DumpType(TypeDefinition type, bool withIL = false)
        {
            var sb = new StringBuilder();
            sb.Append("// ").Append(type.Module.Name).Append("\r\n");
            var hasNs = !string.IsNullOrEmpty(type.Namespace);
            if (hasNs)
                sb.Append("namespace ").Append(type.Namespace).Append("\r\n{\r\n");
            AppendType(sb, type, hasNs ? "    " : "", withIL);
            if (hasNs)
                sb.Append("}\r\n");
            return sb.ToString();
        }

        private static void AppendType(StringBuilder sb, TypeDefinition type, string indent, bool withIL)
        {
            sb.Append(AttrLines(type.CustomAttributes, indent));
            sb.Append(indent).Append(TypeHeader(type)).Append("\r\n");
            if (IsDelegate(type))
                return;
            sb.Append(indent).Append("{\r\n");
            var inner = indent + "    ";

            if (type.IsEnum)
            {
                foreach (var f in type.Fields.Where(f => f.IsStatic && f.HasConstant))
                    sb.Append(inner).Append(f.Name).Append(" = ").Append(Literal(f.Constant)).Append(",\r\n");
                sb.Append(indent).Append("}\r\n");
                return;
            }

            var first = true;
            void Section(string title, IEnumerable<string> lines)
            {
                var list = lines.ToList();
                if (list.Count == 0) return;
                if (!first) sb.Append("\r\n");
                first = false;
                sb.Append(inner).Append("// ").Append(title).Append("\r\n");
                foreach (var l in list) sb.Append(l);
            }

            Section("Fields", type.Fields.Select(f => AttrLines(f.CustomAttributes, inner) + inner + FieldDecl(f) + "\r\n"));
            Section("Properties", type.Properties.Select(p => AttrLines(p.CustomAttributes, inner) + inner + PropertyDecl(p) + "\r\n"));
            Section("Events", type.Events.Select(e => AttrLines(e.CustomAttributes, inner) + inner + EventDecl(e) + "\r\n"));
            Section("Constructors", type.Methods.Where(m => m.IsConstructor).Select(m => MethodBlock(m, inner, withIL)));
            Section("Methods", type.Methods.Where(IsPlainMethod).Select(m => MethodBlock(m, inner, withIL)));

            if (type.HasNestedTypes)
            {
                foreach (var nt in type.NestedTypes)
                {
                    if (!first) sb.Append("\r\n");
                    first = false;
                    AppendType(sb, nt, inner, withIL);
                }
            }
            sb.Append(indent).Append("}\r\n");
        }

        /// <summary>Methods that are not constructors or property/event accessors.</summary>
        public static bool IsPlainMethod(MethodDefinition m)
        {
            return !m.IsConstructor && !m.IsGetter && !m.IsSetter && !m.IsAddOn && !m.IsRemoveOn && !m.IsFire;
        }

        public static string TypeHeader(TypeDefinition type)
        {
            var sb = new StringBuilder();
            sb.Append(TypeVisibility(type)).Append(' ');
            if (type.IsEnum)
            {
                sb.Append("enum ").Append(TypeName(type));
                var under = type.Fields.FirstOrDefault(f => f.Name == "value__")?.FieldType;
                if (under != null && under.FullName != "System.Int32")
                    sb.Append(" : ").Append(TypeName(under));
                return sb.ToString();
            }
            if (IsDelegate(type))
            {
                sb.Append("delegate ");
                var invoke = type.Methods.FirstOrDefault(m => m.Name == "Invoke");
                if (invoke != null)
                    sb.Append(TypeName(invoke.ReturnType)).Append(' ').Append(TypeName(type)).Append('(').Append(Params(invoke)).Append(");");
                else
                    sb.Append(TypeName(type)).Append(';');
                return sb.ToString();
            }
            if (type.IsInterface)
                sb.Append("interface ");
            else if (type.IsValueType)
                sb.Append("struct ");
            else
            {
                if (type.IsAbstract && type.IsSealed) sb.Append("static ");
                else if (type.IsAbstract) sb.Append("abstract ");
                else if (type.IsSealed) sb.Append("sealed ");
                sb.Append("class ");
            }
            sb.Append(TypeName(type));
            var bases = new List<string>();
            if (type.BaseType != null && !type.IsValueType && type.BaseType.FullName != "System.Object")
                bases.Add(TypeName(type.BaseType));
            foreach (var i in type.Interfaces)
                bases.Add(TypeName(i.InterfaceType));
            if (bases.Count > 0)
                sb.Append(" : ").Append(string.Join(", ", bases));
            return sb.ToString();
        }

        public static bool IsDelegate(TypeDefinition t)
        {
            var b = t.BaseType?.FullName;
            return b == "System.MulticastDelegate" || b == "System.Delegate";
        }

        private static string TypeVisibility(TypeDefinition t)
        {
            if (t.IsNested)
            {
                if (t.IsNestedPublic) return "public";
                if (t.IsNestedPrivate) return "private";
                if (t.IsNestedFamily) return "protected";
                if (t.IsNestedAssembly) return "internal";
                if (t.IsNestedFamilyOrAssembly) return "protected internal";
                if (t.IsNestedFamilyAndAssembly) return "private protected";
                return "";
            }
            return t.IsPublic ? "public" : "internal";
        }

        #endregion

        #region Members

        public static string FieldDecl(FieldDefinition f)
        {
            var sb = new StringBuilder();
            sb.Append(FieldVisibility(f)).Append(' ');
            if (f.HasConstant && f.IsLiteral) sb.Append("const ");
            else
            {
                if (f.IsStatic) sb.Append("static ");
                if (f.IsInitOnly) sb.Append("readonly ");
            }
            sb.Append(TypeName(f.FieldType)).Append(' ').Append(f.Name);
            if (f.HasConstant)
                sb.Append(" = ").Append(Literal(f.Constant));
            sb.Append(';');
            return sb.ToString();
        }

        private static string FieldVisibility(FieldDefinition f)
        {
            if (f.IsPublic) return "public";
            if (f.IsPrivate) return "private";
            if (f.IsFamily) return "protected";
            if (f.IsAssembly) return "internal";
            if (f.IsFamilyOrAssembly) return "protected internal";
            if (f.IsFamilyAndAssembly) return "private protected";
            return "";
        }

        public static string PropertyDecl(PropertyDefinition p)
        {
            var acc = p.GetMethod ?? p.SetMethod;
            var sb = new StringBuilder();
            if (acc != null) sb.Append(MethodModifiers(acc, p.DeclaringType));
            sb.Append(TypeName(p.PropertyType)).Append(' ');
            if (p.HasParameters && p.Name == "Item")
                sb.Append("this[").Append(Params(p.Parameters)).Append(']');
            else
                sb.Append(p.Name);
            sb.Append(" { ");
            if (p.GetMethod != null) sb.Append(AccessorPrefix(p.GetMethod, acc)).Append("get; ");
            if (p.SetMethod != null) sb.Append(AccessorPrefix(p.SetMethod, acc)).Append("set; ");
            sb.Append('}');
            return sb.ToString();
        }

        private static string AccessorPrefix(MethodDefinition accessor, MethodDefinition primary)
        {
            if (accessor == primary) return "";
            var a = MethodVisibility(accessor);
            var b = MethodVisibility(primary);
            return a == b ? "" : a + " ";
        }

        public static string EventDecl(EventDefinition e)
        {
            var acc = e.AddMethod ?? e.RemoveMethod;
            var sb = new StringBuilder();
            if (acc != null) sb.Append(MethodModifiers(acc, e.DeclaringType));
            sb.Append("event ").Append(TypeName(e.EventType)).Append(' ').Append(e.Name).Append(';');
            return sb.ToString();
        }

        public static string MethodDecl(MethodDefinition m)
        {
            var sb = new StringBuilder();
            sb.Append(MethodModifiers(m, m.DeclaringType));
            if (m.IsConstructor)
            {
                sb.Append(StripArity(m.DeclaringType.Name));
            }
            else
            {
                sb.Append(TypeName(m.ReturnType)).Append(' ').Append(m.Name);
                if (m.HasGenericParameters)
                    sb.Append('<').Append(string.Join(", ", m.GenericParameters.Select(g => g.Name))).Append('>');
            }
            sb.Append('(').Append(Params(m)).Append(')');
            foreach (var gp in m.GenericParameters)
            {
                var cs = new List<string>();
                if (gp.HasReferenceTypeConstraint) cs.Add("class");
                if (gp.HasNotNullableValueTypeConstraint) cs.Add("struct");
                foreach (var c in gp.Constraints)
                    if (c.ConstraintType.FullName != "System.ValueType") cs.Add(TypeName(c.ConstraintType));
                if (gp.HasDefaultConstructorConstraint && !gp.HasNotNullableValueTypeConstraint) cs.Add("new()");
                if (cs.Count > 0)
                    sb.Append(" where ").Append(gp.Name).Append(" : ").Append(string.Join(", ", cs));
            }
            sb.Append(';');
            return sb.ToString();
        }

        /// <summary>Short display label for tree nodes: name(paramTypes) : returnType.</summary>
        public static string MethodLabel(MethodDefinition m)
        {
            var name = m.IsConstructor ? StripArity(m.DeclaringType.Name) : m.Name;
            if (m.HasGenericParameters)
                name += "<" + string.Join(", ", m.GenericParameters.Select(g => g.Name)) + ">";
            var ps = string.Join(", ", m.Parameters.Select(p => TypeName(p.ParameterType)));
            var ret = m.IsConstructor ? "" : " : " + TypeName(m.ReturnType);
            return name + "(" + ps + ")" + ret;
        }

        private static string MethodModifiers(MethodDefinition m, TypeDefinition declaring)
        {
            var sb = new StringBuilder();
            if (!declaring.IsInterface)
                sb.Append(MethodVisibility(m)).Append(' ');
            if (m.IsStatic) sb.Append("static ");
            if (declaring.IsInterface)
            {
                // interface members: no modifiers
            }
            else if (m.IsAbstract) sb.Append("abstract ");
            else if (m.IsVirtual && !m.IsFinal)
                sb.Append(m.IsNewSlot ? "virtual " : "override ");
            else if (m.IsVirtual && m.IsFinal && !m.IsNewSlot)
                sb.Append("sealed override ");
            if (m.IsPInvokeImpl) sb.Append("extern ");
            if (m.HasCustomAttributes && m.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute"))
                sb.Append("async ");
            return sb.ToString();
        }

        private static string MethodVisibility(MethodDefinition m)
        {
            if (m.IsPublic) return "public";
            if (m.IsPrivate) return "private";
            if (m.IsFamily) return "protected";
            if (m.IsAssembly) return "internal";
            if (m.IsFamilyOrAssembly) return "protected internal";
            if (m.IsFamilyAndAssembly) return "private protected";
            return "";
        }

        private static string Params(MethodDefinition m)
        {
            var isExt = m.HasCustomAttributes && m.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.ExtensionAttribute");
            return Params(m.Parameters, isExt);
        }

        private static string Params(IEnumerable<ParameterDefinition> ps, bool firstIsThis = false)
        {
            var parts = new List<string>();
            var i = 0;
            foreach (var p in ps)
            {
                var sb = new StringBuilder();
                if (i == 0 && firstIsThis) sb.Append("this ");
                if (p.HasCustomAttributes && p.CustomAttributes.Any(a => a.AttributeType.FullName == "System.ParamArrayAttribute"))
                    sb.Append("params ");
                var t = p.ParameterType;
                if (t is ByReferenceType br)
                {
                    if (p.IsOut) sb.Append("out ");
                    else if (p.IsIn) sb.Append("in ");
                    else sb.Append("ref ");
                    sb.Append(TypeName(br.ElementType));
                }
                else
                {
                    sb.Append(TypeName(t));
                }
                sb.Append(' ').Append(string.IsNullOrEmpty(p.Name) ? "p" + i : p.Name);
                if (p.HasConstant)
                    sb.Append(" = ").Append(Literal(p.Constant));
                parts.Add(sb.ToString());
                i++;
            }
            return string.Join(", ", parts);
        }

        private static string MethodBlock(MethodDefinition m, string indent, bool withIL)
        {
            var sb = new StringBuilder();
            sb.Append(AttrLines(m.CustomAttributes, indent));
            sb.Append(indent).Append(MethodDecl(m)).Append("\r\n");
            if (withIL && m.HasBody)
                AppendIL(sb, m.Body, indent + "    ");
            return sb.ToString();
        }

        #endregion

        #region IL

        /// <summary>Signature plus IL listing (if the method has a body).</summary>
        public static string DumpMethod(MethodDefinition m)
        {
            var sb = new StringBuilder();
            sb.Append("// ").Append(m.DeclaringType.FullName).Append("\r\n");
            sb.Append(AttrLines(m.CustomAttributes, ""));
            sb.Append(MethodDecl(m)).Append("\r\n");
            if (m.HasBody)
                AppendIL(sb, m.Body, "    ");
            else
                sb.Append("    // (no body)\r\n");
            return sb.ToString();
        }

        private static void AppendIL(StringBuilder sb, MethodBody body, string indent)
        {
            sb.Append(indent).Append("// IL: ").Append(body.CodeSize).Append(" bytes, maxstack ").Append(body.MaxStackSize).Append("\r\n");
            if (body.HasVariables)
            {
                sb.Append(indent).Append("// locals: ");
                sb.Append(string.Join(", ", body.Variables.Select(v => TypeName(v.VariableType) + " V_" + v.Index)));
                sb.Append("\r\n");
            }
            foreach (var ins in body.Instructions)
            {
                sb.Append(indent).Append("IL_").Append(ins.Offset.ToString("x4")).Append(": ").Append(ins.OpCode.Name);
                var op = OperandText(ins);
                if (op != null) sb.Append(' ').Append(op);
                sb.Append("\r\n");
            }
            if (body.HasExceptionHandlers)
            {
                foreach (var eh in body.ExceptionHandlers)
                {
                    sb.Append(indent).Append("// ").Append(eh.HandlerType.ToString().ToLowerInvariant())
                      .Append(" try IL_").Append(eh.TryStart.Offset.ToString("x4")).Append("-IL_").Append(eh.TryEnd.Offset.ToString("x4"))
                      .Append(" handler IL_").Append(eh.HandlerStart.Offset.ToString("x4")).Append("-IL_").Append(eh.HandlerEnd?.Offset.ToString("x4") ?? "end");
                    if (eh.CatchType != null) sb.Append(" (").Append(TypeName(eh.CatchType)).Append(')');
                    sb.Append("\r\n");
                }
            }
        }

        private static string OperandText(Instruction ins)
        {
            var op = ins.Operand;
            switch (op)
            {
                case null: return null;
                case Instruction target: return "IL_" + target.Offset.ToString("x4");
                case Instruction[] targets: return "(" + string.Join(", ", targets.Select(t => "IL_" + t.Offset.ToString("x4"))) + ")";
                case string s: return "\"" + Escape(s) + "\"";
                case MethodReference mr: return TypeName(mr.ReturnType) + " " + TypeName(mr.DeclaringType) + "::" + mr.Name + "(" + string.Join(", ", mr.Parameters.Select(p => TypeName(p.ParameterType))) + ")";
                case FieldReference fr: return TypeName(fr.FieldType) + " " + TypeName(fr.DeclaringType) + "::" + fr.Name;
                case TypeReference tr: return TypeName(tr);
                case VariableDefinition v: return "V_" + v.Index;
                case ParameterDefinition p: return p.Name;
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                default: return Convert.ToString(op, CultureInfo.InvariantCulture);
            }
        }

        #endregion

        #region Helpers

        private static string AttrLines(IEnumerable<CustomAttribute> attrs, string indent)
        {
            var sb = new StringBuilder();
            foreach (var a in attrs)
            {
                var name = a.AttributeType.FullName;
                if (HiddenAttributes.Contains(name))
                    continue;
                var shortName = a.AttributeType.Name;
                if (shortName.EndsWith("Attribute")) shortName = shortName.Substring(0, shortName.Length - 9);
                sb.Append(indent).Append('[').Append(shortName);
                var args = new List<string>();
                try
                {
                    foreach (var ca in a.ConstructorArguments) args.Add(ArgText(ca));
                    foreach (var f in a.Fields) args.Add(f.Name + " = " + ArgText(f.Argument));
                    foreach (var p in a.Properties) args.Add(p.Name + " = " + ArgText(p.Argument));
                }
                catch
                {
                    // unresolved attribute argument types
                }
                if (args.Count > 0) sb.Append('(').Append(string.Join(", ", args)).Append(')');
                sb.Append("]\r\n");
            }
            return sb.ToString();
        }

        private static string ArgText(CustomAttributeArgument ca)
        {
            if (ca.Value is CustomAttributeArgument[] arr)
                return "new[] { " + string.Join(", ", arr.Select(ArgText)) + " }";
            if (ca.Value is CustomAttributeArgument inner)
                return ArgText(inner);
            if (ca.Value is TypeReference tr)
                return "typeof(" + TypeName(tr) + ")";
            TypeDefinition enumDef = null;
            try { enumDef = ca.Type?.Resolve(); } catch { /* unresolvable */ }
            if (enumDef != null && enumDef.IsEnum)
            {
                var match = enumDef.Fields.FirstOrDefault(f => f.HasConstant && Equals(f.Constant, ca.Value));
                if (match != null) return TypeName(ca.Type) + "." + match.Name;
                return "(" + TypeName(ca.Type) + ")" + Literal(ca.Value);
            }
            return Literal(ca.Value);
        }

        public static string Literal(object v)
        {
            switch (v)
            {
                case null: return "null";
                case string s: return "\"" + Escape(s) + "\"";
                case char c: return "'" + Escape(c.ToString()) + "'";
                case bool b: return b ? "true" : "false";
                case float f: return f.ToString("R", CultureInfo.InvariantCulture) + "f";
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case decimal m: return m.ToString(CultureInfo.InvariantCulture) + "m";
                case long l: return l + "L";
                case ulong ul: return ul + "UL";
                case uint ui: return ui + "u";
                default: return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        private static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\0': sb.Append("\\0"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        #endregion
    }
}
