using System.Globalization;
using System.Text;

namespace OptimusVibe.Handlers;

public sealed class GvasException(string message) : Exception(message);

/// <summary>
/// One Unreal Engine property of a GVAS SaveGame (name, type and a decoded value). Properties the reader does not decode
/// (maps, sets, text, unknown structs) keep their payload bytes verbatim in <see cref="Raw"/>, so a file always round-trips.
/// </summary>
public sealed class GvasProperty
{
    public string Name = "";
    public string Type = "";
    /// <summary>Type arguments from the property header: struct type name, enum name, array/set inner type, map key/value types.</summary>
    public List<string> Info = new();
    /// <summary>Struct GUID (16 bytes) of a StructProperty header.</summary>
    public byte[]? StructGuid;
    /// <summary>Property GUID when the header flag byte was 1; null when it was 0.</summary>
    public byte[]? PropertyGuid;
    /// <summary>bool, sbyte, byte, short, ushort, int, uint, long, ulong, float, double, string, <see cref="GvasStruct"/> or <see cref="GvasArray"/>.</summary>
    public object? Value;
    /// <summary>Verbatim payload when this property is not decoded.</summary>
    public byte[]? Raw;
    /// <summary>True for synthetic members of a fixed struct (IntPoint.X): no header is written for them.</summary>
    internal bool Synthetic;

    public bool IsScalar => Raw is null && Value is not (GvasStruct or GvasArray);
}

public sealed class GvasStruct
{
    public string TypeName = "";
    /// <summary>Fixed-layout engine struct (IntPoint, Vector...) whose members have no headers.</summary>
    public bool Fixed;
    public List<GvasProperty> Fields = new();
}

public sealed class GvasArray
{
    public string InnerType = "";
    public List<object?> Elements = new();
    // StructProperty element header (name, type, struct type name, guid) of an array of structs.
    public string StructName = "";
    public string StructTypeName = "";
    public byte[] StructGuid = new byte[16];
}

/// <summary>A property in the flattened, path-addressed view of a save.</summary>
public readonly record struct GvasEntry(string Path, string Type, string Value, bool Writable);

/// <summary>
/// Minimal Unreal Engine "GVAS" SaveGame reader/writer (UE 4.x/5.x property-list saves). Parse then <see cref="ToBytes"/>
/// is byte-identical for every property type it understands and for the ones it keeps raw. Written from the public
/// format (see docs); no third-party code.
/// </summary>
public sealed class GvasFile
{
    private static readonly byte[] Magic = "GVAS"u8.ToArray();

    public byte[] HeaderBytes { get; private set; } = [];
    public int SaveGameVersion { get; private set; }
    public string SaveClass { get; private set; } = "";
    public string EngineVersion { get; private set; } = "";
    public List<GvasProperty> Properties { get; } = new();
    public byte[] Trailer { get; private set; } = [];

    public static bool LooksLikeGvas(byte[] data) => data.Length > 8 && data.AsSpan(0, 4).SequenceEqual(Magic);

    public static GvasFile Parse(byte[] data)
    {
        if (!LooksLikeGvas(data)) throw new GvasException("Not a GVAS save (missing 'GVAS' magic).");
        var f = new GvasFile();
        var r = new Reader(data, 0, data.Length) { Pos = 4 };
        try
        {
            f.SaveGameVersion = r.Int32();
            if (f.SaveGameVersion is < 2 or > 3) throw new GvasException($"Unsupported GVAS save version {f.SaveGameVersion} (2 and 3 are supported).");
            r.Int32(); // package file version (UE4)
            if (f.SaveGameVersion >= 3) r.Int32(); // package file version (UE5)
            ushort major = r.UInt16(), minor = r.UInt16(), patch = r.UInt16();
            r.UInt32(); // changelist
            var branch = r.String();
            f.EngineVersion = $"{major}.{minor}.{patch} {branch}".Trim();
            r.Int32(); // custom version format
            int cv = r.Int32();
            if (cv < 0 || cv > 10000) throw new GvasException("Corrupt custom version table.");
            r.Skip(cv * 20);
            f.SaveClass = r.String();
            f.HeaderBytes = data[..r.Pos];
            while (true)
            {
                var p = ReadProperty(r);
                if (p is null) break;
                f.Properties.Add(p);
            }
            f.Trailer = data[r.Pos..];
        }
        catch (GvasException) { throw; }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new GvasException($"Corrupt GVAS data: {e.Message}");
        }
        return f;
    }

    public byte[] ToBytes()
    {
        var w = new Writer();
        w.Bytes(HeaderBytes);
        foreach (var p in Properties) WriteProperty(w, p);
        w.String("None");
        w.Bytes(Trailer);
        return w.ToArray();
    }

    // ---------- path-addressed view ----------

    /// <summary>
    /// Every property in "Path" form: top-level name, struct member as "Struct.Member", array element as "Array[i]".
    /// Opaque properties are listed (value "&lt;N bytes&gt;") but not writable.
    /// </summary>
    public IEnumerable<GvasEntry> Enumerate()
    {
        var all = new List<GvasEntry>();
        foreach (var p in Properties) Walk(p, p.Name, all);
        return all;
    }

    private static void Walk(GvasProperty p, string path, List<GvasEntry> into)
    {
        switch (p.Value)
        {
            case GvasStruct s when p.Raw is null:
                foreach (var m in s.Fields) Walk(m, path + "." + m.Name, into);
                return;
            case GvasArray a when p.Raw is null:
                into.Add(new GvasEntry(path, $"{p.Type}<{a.InnerType}>", $"[{a.Elements.Count}]", false));
                if (a.InnerType == "StructProperty")
                    for (int i = 0; i < a.Elements.Count; i++)
                        if (a.Elements[i] is GvasStruct es)
                            foreach (var m in es.Fields) Walk(m, $"{path}[{i}].{m.Name}", into);
                if (a.InnerType != "ByteProperty" && a.InnerType != "StructProperty")
                    for (int i = 0; i < a.Elements.Count; i++)
                        into.Add(new GvasEntry($"{path}[{i}]", a.InnerType, Format(a.Elements[i]), true));
                return;
        }
        if (p.Raw is not null) into.Add(new GvasEntry(path, p.Type, $"<{p.Raw.Length} bytes>", false));
        else into.Add(new GvasEntry(path, p.Type, Format(p.Value), true));
    }

    public string? Get(string path)
    {
        var t = Resolve(path, out var arrayIndex);
        if (t is null) return null;
        return arrayIndex is int i ? Format(((GvasArray)t.Value!).Elements[i]) : t.Raw is not null ? $"<{t.Raw.Length} bytes>" : Format(t.Value);
    }

    /// <summary>Type names accepted after a colon in a key ("ShadowQuality:int"), mapped to GVAS property types.</summary>
    public static readonly IReadOnlyDictionary<string, string> TypeHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["int"] = "IntProperty", ["uint"] = "UInt32Property", ["int64"] = "Int64Property", ["uint64"] = "UInt64Property",
        ["int16"] = "Int16Property", ["uint16"] = "UInt16Property", ["int8"] = "Int8Property",
        ["float"] = "FloatProperty", ["double"] = "DoubleProperty", ["bool"] = "BoolProperty",
        ["byte"] = "ByteProperty", ["str"] = "StrProperty", ["name"] = "NameProperty", ["enum"] = "EnumProperty",
    };

    /// <summary>Splits "Path:type" into the path and the property type the hint names (null when there is no hint).</summary>
    public static string SplitHint(string key, out string? type)
    {
        type = null;
        int colon = key.LastIndexOf(':');
        if (colon <= 0 || colon == key.Length - 1) return key;
        var hint = key[(colon + 1)..];
        if (!TypeHints.TryGetValue(hint, out var t))
            throw new GvasException($"Unknown type hint ':{hint}' in key '{key}' (use one of: {string.Join(", ", TypeHints.Keys)}).");
        type = t;
        return key[..colon];
    }

    /// <summary>
    /// Set one scalar by path. The path may end in a ":type" hint ("ViewDistanceQuality:int"). A property that is absent (Unreal
    /// leaves out properties equal to their defaults) is added to the end of its parent only when a hint says its type; an
    /// existing property keeps its type and a conflicting hint is an error. Throws <see cref="GvasException"/> for unknown paths,
    /// non-scalar targets and values that don't fit the type.
    /// </summary>
    public void Set(string key, string value)
    {
        var path = SplitHint(key, out var hint);
        var t = Resolve(path, out var arrayIndex);
        if (t is null)
        {
            if (hint is null) throw new GvasException(
                $"No property '{path}' in the save. Unreal omits properties that equal their defaults; to add it, give its type: '{path}:int' (int, uint, float, bool, byte, str, name, enum).");
            Insert(path, hint, value);
            return;
        }
        if (hint is not null && arrayIndex is null && hint != t.Type)
            throw new GvasException($"'{path}' is a {t.Type} in the save, not {hint}; drop or fix the ':type' hint.");
        if (arrayIndex is int i)
        {
            var a = (GvasArray)t.Value!;
            a.Elements[i] = Parse(a.InnerType, "", value, path, a.Elements[i]);
            return;
        }
        if (!t.IsScalar) throw new GvasException(t.Raw is not null
            ? $"'{path}' ({t.Type}) is not decoded by the gvas handler, so it cannot be written."
            : $"'{path}' is a {t.Type} container; address one scalar member or element ('{path}.Member' or '{path}[0]').");
        t.Value = Parse(t.Type, "", value, path, t.Value);
    }

    private void Insert(string path, string type, string value)
    {
        var list = Properties;
        string leaf = path;
        int dot = path.LastIndexOf('.');
        if (dot > 0)
        {
            var parent = Resolve(path[..dot], out var idx);
            if (parent is null || idx is not null || parent.Raw is not null || parent.Value is not GvasStruct { Fixed: false } ps)
                throw new GvasException($"Cannot add '{path}': '{path[..dot]}' is not a struct in the save.");
            list = ps.Fields; leaf = path[(dot + 1)..];
        }
        if (leaf.Length == 0 || leaf.Contains('[')) throw new GvasException($"Cannot add '{path}': bad property name.");
        var p = new GvasProperty { Name = leaf, Type = type };
        if (type == "ByteProperty") p.Info.Add("None");
        if (type == "EnumProperty") p.Info.Add("");
        p.Value = Parse(type, "", value, path, type == "ByteProperty" ? (byte)0 : null);
        if (type == "EnumProperty")
        {
            var sep = value.IndexOf("::", StringComparison.Ordinal);
            if (sep <= 0) throw new GvasException($"'{path}' is an enum; give the full value such as EnumName::Value.");
            p.Info[0] = value[..sep];
        }
        list.Add(p);
    }

    private GvasProperty? Resolve(string path, out int? arrayIndex)
    {
        arrayIndex = null;
        var list = Properties;
        var rest = path;
        while (true)
        {
            GvasProperty? hit = null;
            string remainder = "";
            foreach (var p in list)
            {
                if (rest == p.Name) { hit = p; remainder = ""; break; }
                if (rest.StartsWith(p.Name, StringComparison.Ordinal) && rest.Length > p.Name.Length && rest[p.Name.Length] is '.' or '[')
                { hit = p; remainder = rest[p.Name.Length..]; break; }
            }
            if (hit is null) return null;
            if (remainder.Length == 0) return hit;
            if (remainder[0] == '.')
            {
                if (hit.Value is not GvasStruct s || hit.Raw is not null) return null;
                list = s.Fields; rest = remainder[1..];
                continue;
            }
            // "[i]" or "[i].Member"
            int close = remainder.IndexOf(']');
            if (close < 0 || !int.TryParse(remainder.AsSpan(1, close - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var idx)) return null;
            if (hit.Value is not GvasArray a || hit.Raw is not null || idx >= a.Elements.Count) return null;
            var after = remainder[(close + 1)..];
            if (after.Length == 0) { arrayIndex = idx; return hit; }
            if (after[0] != '.' || a.Elements[idx] is not GvasStruct es) return null;
            list = es.Fields; rest = after[1..];
        }
    }

    public static string Format(object? v) => v switch
    {
        null => "",
        bool b => b ? "true" : "false",
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable fm => fm.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static object Parse(string type, string enumName, string text, string path, object? current)
    {
        var t = text.Trim();
        try
        {
            switch (type)
            {
                case "IntProperty": return int.Parse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                case "UInt32Property": return uint.Parse(t, NumberStyles.None, CultureInfo.InvariantCulture);
                case "Int64Property": return long.Parse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                case "UInt64Property": return ulong.Parse(t, NumberStyles.None, CultureInfo.InvariantCulture);
                case "Int16Property": return short.Parse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                case "UInt16Property": return ushort.Parse(t, NumberStyles.None, CultureInfo.InvariantCulture);
                case "Int8Property": return sbyte.Parse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                case "FloatProperty": return ParseFloat(t, float.MaxValue) is var f ? (float)f : 0f;
                case "DoubleProperty": return ParseFloat(t, double.MaxValue);
                case "BoolProperty":
                    if (t.Equals("true", StringComparison.OrdinalIgnoreCase) || t == "1") return true;
                    if (t.Equals("false", StringComparison.OrdinalIgnoreCase) || t == "0") return false;
                    throw new FormatException("expected true or false");
                case "ByteProperty":
                    if (current is string) return text;                       // enum stored as a name
                    return byte.Parse(t, NumberStyles.None, CultureInfo.InvariantCulture);
                case "EnumProperty":
                case "StrProperty":
                case "NameProperty":
                    return text;
                default:
                    throw new GvasException($"'{path}' has type {type}, which the gvas handler cannot write.");
            }
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            throw new GvasException($"'{text}' is not a valid {type} value for '{path}' ({e.Message}).");
        }
    }

    private static double ParseFloat(string t, double max)
    {
        var d = double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (double.IsNaN(d) || double.IsInfinity(d) || Math.Abs(d) > max) throw new OverflowException("out of range");
        return d;
    }

    // ---------- reading ----------

    private static GvasProperty? ReadProperty(Reader r)
    {
        var name = r.String();
        if (name == "None") return null;
        var p = new GvasProperty { Name = name, Type = r.String() };
        long size = r.Int64();
        if (size < 0 || size > r.End - r.Pos) throw new GvasException($"Property '{name}' declares an impossible size {size}.");
        bool boolValue = false;
        switch (p.Type)
        {
            case "StructProperty": p.Info.Add(r.String()); p.StructGuid = r.Take(16); break;
            case "ArrayProperty":
            case "SetProperty":
            case "ByteProperty":
            case "EnumProperty": p.Info.Add(r.String()); break;
            case "MapProperty": p.Info.Add(r.String()); p.Info.Add(r.String()); break;
            case "BoolProperty": boolValue = r.Byte() != 0; break;
        }
        byte flag = r.Byte();
        if (flag == 1) p.PropertyGuid = r.Take(16);
        else if (flag != 0) throw new GvasException($"Property '{name}': unexpected GUID flag {flag}.");
        if (p.Type == "BoolProperty") { p.Value = boolValue; return p; }

        int start = r.Pos;
        var sub = new Reader(r.Data, start, start + (int)size);
        try
        {
            p.Value = ReadValue(sub, p, (int)size);
            if (sub.Pos != sub.End) throw new FormatException("payload not fully consumed");
        }
        catch (Exception e) when (e is FormatException or GvasException or IndexOutOfRangeException or ArgumentException or InvalidOperationException)
        {
            p.Value = null;
            p.Raw = r.Data[start..(start + (int)size)];
        }
        r.Pos = start + (int)size;
        return p;
    }

    private static object? ReadValue(Reader r, GvasProperty p, int size)
    {
        switch (p.Type)
        {
            case "StructProperty": return ReadStruct(r, p.Info[0], size);
            case "ByteProperty": return p.Info[0] == "None" ? r.Byte() : r.String();
            case "ArrayProperty": return ReadArray(r, p.Info[0]);
            case "MapProperty":
            case "SetProperty":
            case "TextProperty": throw new FormatException("not decoded");
            default: return ReadScalar(r, p.Type, size);
        }
    }

    private static object ReadScalar(Reader r, string type, int size) => type switch
    {
        "IntProperty" => r.Int32(),
        "UInt32Property" => r.UInt32(),
        "Int64Property" => r.Int64(),
        "UInt64Property" => (ulong)r.Int64(),
        "Int16Property" => (short)r.UInt16(),
        "UInt16Property" => r.UInt16(),
        "Int8Property" => (sbyte)r.Byte(),
        "FloatProperty" => r.Single(),
        "DoubleProperty" => r.Double(),
        "StrProperty" or "NameProperty" or "ObjectProperty" or "EnumProperty" => r.String(),
        "BoolProperty" => r.Byte() != 0,
        _ => throw new FormatException("not decoded"),
    };

    private static GvasStruct ReadStruct(Reader r, string typeName, int size)
    {
        var s = new GvasStruct { TypeName = typeName };
        GvasProperty M(string n, string t, object v) => new() { Name = n, Type = t, Value = v, Synthetic = true };
        switch (typeName)
        {
            case "IntPoint":
                s.Fixed = true; s.Fields.Add(M("X", "IntProperty", r.Int32())); s.Fields.Add(M("Y", "IntProperty", r.Int32())); return s;
            case "Vector2D" or "Vector" or "Rotator" or "Quat" or "Vector4" or "LinearColor":
            {
                string[] names = typeName switch
                {
                    "Vector2D" => ["X", "Y"], "Vector" => ["X", "Y", "Z"], "Rotator" => ["Pitch", "Yaw", "Roll"],
                    "LinearColor" => ["R", "G", "B", "A"], _ => ["X", "Y", "Z", "W"],
                };
                bool dbl = typeName != "LinearColor" && size == names.Length * 8;
                if (size != names.Length * (dbl ? 8 : 4)) throw new FormatException("unexpected struct size");
                s.Fixed = true;
                foreach (var n in names) s.Fields.Add(dbl ? M(n, "DoubleProperty", r.Double()) : M(n, "FloatProperty", r.Single()));
                return s;
            }
            case "Color":
                s.Fixed = true;
                foreach (var n in new[] { "B", "G", "R", "A" }) s.Fields.Add(M(n, "ByteProperty", r.Byte()));
                // enum info "None" keeps them plain bytes
                foreach (var f in s.Fields) f.Info.Add("None");
                return s;
            case "DateTime" or "Timespan":
                s.Fixed = true; s.Fields.Add(M("Ticks", "Int64Property", r.Int64())); return s;
            case "Guid":
                throw new FormatException("kept raw");
        }
        // generic struct: a property list ending in "None"
        while (true)
        {
            var m = ReadProperty(r);
            if (m is null) break;
            s.Fields.Add(m);
        }
        return s;
    }

    private static GvasArray ReadArray(Reader r, string inner)
    {
        var a = new GvasArray { InnerType = inner };
        int count = r.Int32();
        if (count < 0 || count > r.End - r.Pos) throw new FormatException("bad array count");
        if (inner == "StructProperty")
        {
            a.StructName = r.String();
            if (r.String() != "StructProperty") throw new FormatException("bad array struct header");
            long total = r.Int64();
            a.StructTypeName = r.String();
            a.StructGuid = r.Take(16);
            if (r.Byte() != 0) throw new FormatException("struct array property guid");
            int start = r.Pos;
            for (int i = 0; i < count; i++)
                a.Elements.Add(ReadStruct(r, a.StructTypeName, count == 0 ? 0 : (int)(total / count)));
            if (r.Pos - start != total) throw new FormatException("struct array size mismatch");
        }
        else if (inner == "ByteProperty")
            for (int i = 0; i < count; i++) a.Elements.Add(r.Byte());
        else
            for (int i = 0; i < count; i++) a.Elements.Add(ReadScalar(r, inner, 0));
        return a;
    }

    // ---------- writing ----------

    private static void WriteProperty(Writer w, GvasProperty p)
    {
        w.String(p.Name);
        w.String(p.Type);
        if (p.Type == "BoolProperty")
        {
            w.Int64(0); w.Byte((bool)p.Value! ? (byte)1 : (byte)0); WriteGuidFlag(w, p); return;
        }
        var body = new Writer();
        if (p.Raw is not null) body.Bytes(p.Raw);
        else WriteValue(body, p);
        w.Int64(body.Length);
        switch (p.Type)
        {
            case "StructProperty": w.String(p.Info[0]); w.Bytes(p.StructGuid ?? new byte[16]); break;
            case "ArrayProperty": case "SetProperty": case "ByteProperty": case "EnumProperty": w.String(p.Info[0]); break;
            case "MapProperty": w.String(p.Info[0]); w.String(p.Info[1]); break;
        }
        WriteGuidFlag(w, p);
        w.Bytes(body.ToArray());
    }

    private static void WriteGuidFlag(Writer w, GvasProperty p)
    {
        if (p.PropertyGuid is null) w.Byte(0); else { w.Byte(1); w.Bytes(p.PropertyGuid); }
    }

    private static void WriteValue(Writer w, GvasProperty p)
    {
        switch (p.Value)
        {
            case GvasStruct s: WriteStruct(w, s); break;
            case GvasArray a: WriteArray(w, a); break;
            default: WriteScalar(w, p.Value); break;
        }
    }

    private static void WriteScalar(Writer w, object? v)
    {
        switch (v)
        {
            case bool b: w.Byte(b ? (byte)1 : (byte)0); break;
            case sbyte sb: w.Byte((byte)sb); break;
            case byte by: w.Byte(by); break;
            case short sh: w.Bytes(BitConverter.GetBytes(sh)); break;
            case ushort us: w.Bytes(BitConverter.GetBytes(us)); break;
            case int i: w.Int32(i); break;
            case uint ui: w.Bytes(BitConverter.GetBytes(ui)); break;
            case long l: w.Int64(l); break;
            case ulong ul: w.Bytes(BitConverter.GetBytes(ul)); break;
            case float f: w.Bytes(BitConverter.GetBytes(f)); break;
            case double d: w.Bytes(BitConverter.GetBytes(d)); break;
            case string s: w.String(s); break;
            default: throw new GvasException($"Cannot serialize {v?.GetType().Name ?? "null"}.");
        }
    }

    private static void WriteStruct(Writer w, GvasStruct s)
    {
        if (s.Fixed) { foreach (var m in s.Fields) WriteScalar(w, m.Value); return; }
        foreach (var m in s.Fields) WriteProperty(w, m);
        w.String("None");
    }

    private static void WriteArray(Writer w, GvasArray a)
    {
        w.Int32(a.Elements.Count);
        if (a.InnerType == "StructProperty")
        {
            var body = new Writer();
            foreach (var e in a.Elements) WriteStruct(body, (GvasStruct)e!);
            w.String(a.StructName); w.String("StructProperty"); w.Int64(body.Length);
            w.String(a.StructTypeName); w.Bytes(a.StructGuid); w.Byte(0);
            w.Bytes(body.ToArray());
        }
        else foreach (var e in a.Elements) WriteScalar(w, e);
    }

    // ---------- binary helpers ----------

    private sealed class Reader(byte[] data, int pos, int end)
    {
        public byte[] Data { get; } = data;
        public int Pos { get; set; } = pos;
        public int End { get; } = end;

        private int Need(int n)
        {
            if (n < 0 || Pos + n > End) throw new IndexOutOfRangeException("unexpected end of data");
            var at = Pos; Pos += n; return at;
        }
        public void Skip(int n) => Need(n);
        public byte[] Take(int n) { var at = Need(n); return Data[at..(at + n)]; }
        public byte Byte() => Data[Need(1)];
        public ushort UInt16() => BitConverter.ToUInt16(Data, Need(2));
        public int Int32() => BitConverter.ToInt32(Data, Need(4));
        public uint UInt32() => BitConverter.ToUInt32(Data, Need(4));
        public long Int64() => BitConverter.ToInt64(Data, Need(8));
        public float Single() => BitConverter.ToSingle(Data, Need(4));
        public double Double() => BitConverter.ToDouble(Data, Need(8));

        public string String()
        {
            int len = Int32();
            if (len == 0) return "";
            if (len > 0)
            {
                var at = Need(len);
                if (Data[at + len - 1] != 0) throw new FormatException("FString not terminated");
                return Encoding.Latin1.GetString(Data, at, len - 1);
            }
            if (len == int.MinValue) throw new FormatException("bad FString length");
            int chars = -len;
            var u = Need(chars * 2);
            if (Data[u + chars * 2 - 2] != 0 || Data[u + chars * 2 - 1] != 0) throw new FormatException("FString not terminated");
            return Encoding.Unicode.GetString(Data, u, chars * 2 - 2);
        }
    }

    private sealed class Writer
    {
        private readonly MemoryStream _ms = new();
        public long Length => _ms.Length;
        public void Byte(byte b) => _ms.WriteByte(b);
        public void Bytes(byte[] b) => _ms.Write(b, 0, b.Length);
        public void Int32(int v) => Bytes(BitConverter.GetBytes(v));
        public void Int64(long v) => Bytes(BitConverter.GetBytes(v));
        public byte[] ToArray() => _ms.ToArray();

        public void String(string s)
        {
            if (s.Length == 0) { Int32(0); return; }
            if (s.All(c => c < 0x100))
            {
                Int32(s.Length + 1); Bytes(Encoding.Latin1.GetBytes(s)); Byte(0);
            }
            else
            {
                Int32(-(s.Length + 1)); Bytes(Encoding.Unicode.GetBytes(s)); Byte(0); Byte(0);
            }
        }
    }
}
