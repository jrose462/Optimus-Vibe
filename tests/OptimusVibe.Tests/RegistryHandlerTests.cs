using OptimusVibe.Config;
using OptimusVibe.Handlers;
using Microsoft.Win32;

namespace OptimusVibe.Tests;

public sealed class RegistryHandlerTests : IDisposable
{
    private const string Parent = @"Software\OptimusVibeTests";
    private readonly string _sub = Parent + "\\" + Guid.NewGuid().ToString("N");
    private readonly RegistryHandler _h = new();
    private readonly FileDef _def = new() { Id = "reg", Handler = "registry" };

    private string KeyPath => @"HKCU\" + _sub;

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_sub, throwOnMissingSubKey: false);
        using var parent = Registry.CurrentUser.OpenSubKey(Parent);
        if (parent is { SubKeyCount: 0, ValueCount: 0 })
            Registry.CurrentUser.DeleteSubKey(Parent, throwOnMissingSubKey: false);
    }

    private RegistryKey Seed()
    {
        var k = Registry.CurrentUser.CreateSubKey(_sub, writable: true);
        k.SetValue("Dword", 5, RegistryValueKind.DWord);
        k.SetValue("BigDword", unchecked((int)0xFFFFFFF0), RegistryValueKind.DWord);
        k.SetValue("Qword", 1L << 40, RegistryValueKind.QWord);
        k.SetValue("Str", "1", RegistryValueKind.String);
        k.SetValue("Bin", new byte[] { 0x0A, 0xFF, 0x00 }, RegistryValueKind.Binary);
        return k;
    }

    private static Dictionary<string, string> V(params (string Key, string Value)[] kv) =>
        kv.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void Read_FormatsEachKind()
    {
        using (Seed()) { }
        var r = _h.Read(_def, KeyPath, ["Dword", "BigDword", "Qword", "Str", "Bin", "Missing"]);
        Assert.Equal("5", r["Dword"]);
        Assert.Equal("4294967280", r["BigDword"]);
        Assert.Equal((1L << 40).ToString(), r["Qword"]);
        Assert.Equal("1", r["Str"]);
        Assert.Equal("0AFF00", r["Bin"]);
        Assert.Null(r["Missing"]);
    }

    [Fact]
    public void Read_MissingKey_ReturnsNulls_AndExistsFalse()
    {
        Assert.False(_h.Exists(KeyPath));
        Assert.Null(_h.Read(_def, KeyPath, ["a"])["a"]);
        Assert.Null(_h.Snapshot(KeyPath));
    }

    [Fact]
    public void Write_PreservesExistingKinds()
    {
        using (Seed()) { }
        _h.Write(_def, KeyPath, V(("Dword", "7"), ("Qword", "0x10"), ("Str", "2"), ("Bin", "01 02,03"), ("BigDword", "4294967295")));

        using var k = Registry.CurrentUser.OpenSubKey(_sub)!;
        Assert.Equal(RegistryValueKind.DWord, k.GetValueKind("Dword"));
        Assert.Equal(7, k.GetValue("Dword"));
        Assert.Equal(RegistryValueKind.QWord, k.GetValueKind("Qword"));
        Assert.Equal(16L, k.GetValue("Qword"));
        Assert.Equal(RegistryValueKind.String, k.GetValueKind("Str"));
        Assert.Equal("2", k.GetValue("Str"));
        Assert.Equal(RegistryValueKind.Binary, k.GetValueKind("Bin"));
        Assert.Equal(new byte[] { 1, 2, 3 }, k.GetValue("Bin"));
        Assert.Equal(-1, k.GetValue("BigDword"));
        Assert.Equal("4294967295", _h.Read(_def, KeyPath, ["BigDword"])["BigDword"]);
    }

    [Fact]
    public void Write_BoolToDword()
    {
        using (Seed()) { }
        _h.Write(_def, KeyPath, V(("Dword", "true")));
        Assert.Equal("1", _h.Read(_def, KeyPath, ["Dword"])["Dword"]);
    }

    [Fact]
    public void Write_NewValues_IntegerBecomesDword_OtherwiseString_AndCreatesKey()
    {
        Assert.False(_h.Exists(KeyPath));
        _h.Write(_def, KeyPath, V(("NewInt", "42"), ("NewStr", "High"), ("NewFloat", "1.5"), ("(default)", "dflt")));
        Assert.True(_h.Exists(KeyPath));

        using var k = Registry.CurrentUser.OpenSubKey(_sub)!;
        Assert.Equal(RegistryValueKind.DWord, k.GetValueKind("NewInt"));
        Assert.Equal(42, k.GetValue("NewInt"));
        Assert.Equal(RegistryValueKind.String, k.GetValueKind("NewStr"));
        Assert.Equal(RegistryValueKind.String, k.GetValueKind("NewFloat"));
        Assert.Equal("dflt", k.GetValue(""));
        Assert.Equal("dflt", _h.Read(_def, KeyPath, ["(default)"])["(default)"]);
    }

    [Fact]
    public void Write_NewValue_LargerThanDword_IsNotTruncated()
    {
        _h.Write(_def, KeyPath, V(("Big", "5000000000")));
        Assert.Equal("5000000000", _h.Read(_def, KeyPath, ["Big"])["Big"]);
    }

    [Fact]
    public void SnapshotRestore_RestoresValuesAndKinds_AndRemovesExtras()
    {
        using (Seed()) { }
        var snap = _h.Snapshot(KeyPath);
        Assert.NotNull(snap);

        _h.Write(_def, KeyPath, V(("Dword", "99"), ("Str", "changed"), ("Bin", "FFFF"), ("Extra", "123")));
        using (var k = Registry.CurrentUser.OpenSubKey(_sub, writable: true)!) k.DeleteValue("Qword");

        _h.RestoreSnapshot(KeyPath, snap);

        using var r = Registry.CurrentUser.OpenSubKey(_sub)!;
        Assert.DoesNotContain("Extra", r.GetValueNames());
        Assert.Equal(5, r.GetValue("Dword"));
        Assert.Equal(RegistryValueKind.DWord, r.GetValueKind("Dword"));
        Assert.Equal(1L << 40, r.GetValue("Qword"));
        Assert.Equal(RegistryValueKind.QWord, r.GetValueKind("Qword"));
        Assert.Equal("1", r.GetValue("Str"));
        Assert.Equal(RegistryValueKind.String, r.GetValueKind("Str"));
        Assert.Equal(new byte[] { 0x0A, 0xFF, 0x00 }, r.GetValue("Bin"));
        Assert.Equal(unchecked((int)0xFFFFFFF0), r.GetValue("BigDword"));
    }

    [Fact]
    public void SnapshotRestore_RecreatesDeletedKey()
    {
        using (Seed()) { }
        var snap = _h.Snapshot(KeyPath);
        Registry.CurrentUser.DeleteSubKeyTree(_sub);
        _h.RestoreSnapshot(KeyPath, snap);
        Assert.Equal("5", _h.Read(_def, KeyPath, ["Dword"])["Dword"]);
    }

    [Fact]
    public void RestoreSnapshot_Null_DeletesKey()
    {
        _h.Write(_def, KeyPath, V(("A", "1")));
        Assert.True(_h.Exists(KeyPath));
        _h.RestoreSnapshot(KeyPath, null);
        Assert.False(_h.Exists(KeyPath));
        _h.RestoreSnapshot(KeyPath, null); // already gone: no-op
    }

    [Fact]
    public void RestoreSnapshot_Null_KeepsKeyThatHasSubkeys()
    {
        _h.Write(_def, KeyPath, V(("A", "1")));
        Registry.CurrentUser.CreateSubKey(_sub + @"\Child")!.Dispose();
        _h.RestoreSnapshot(KeyPath, null);
        Assert.True(_h.Exists(KeyPath));
    }

    [Fact]
    public void Format_FollowsDataType_WhenKindDisagrees()
    {
        Assert.Equal("0102", RegistryHandler.Format(new byte[] { 1, 2 }, RegistryValueKind.None));
        Assert.Equal("0102", RegistryHandler.Format(new byte[] { 1, 2 }, RegistryValueKind.DWord));
        Assert.Equal("5", RegistryHandler.Format(5, RegistryValueKind.DWord));
    }

    [Fact]
    public void SnapshotRestore_RegNoneValue_RoundTrips()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(_sub, writable: true))
            k.SetValue("NoneVal", new byte[] { 0x01, 0x02 }, RegistryValueKind.None);

        var snap = _h.Snapshot(KeyPath);
        Assert.NotNull(snap);
        Registry.CurrentUser.DeleteSubKeyTree(_sub);
        _h.RestoreSnapshot(KeyPath, snap);

        using var r = Registry.CurrentUser.OpenSubKey(_sub)!;
        Assert.Equal(RegistryValueKind.None, r.GetValueKind("NoneVal"));
        Assert.Equal(new byte[] { 0x01, 0x02 }, r.GetValue("NoneVal"));
    }

    [Theory]
    [InlineData(@"HKCU\Software\X", @"Software\X")]
    [InlineData(@"HKEY_CURRENT_USER/Software/X/", @"Software\X")]
    [InlineData(@"HKLM\SOFTWARE\Y", @"SOFTWARE\Y")]
    public void SplitPath_ParsesHives(string path, string sub)
    {
        Assert.Equal(sub, RegistryHandler.SplitPath(path).SubKey);
    }

    [Fact]
    public void SplitPath_UnknownHive_Throws() =>
        Assert.Throws<ConfigException>(() => RegistryHandler.SplitPath(@"HKCR\foo"));

    [Fact]
    public void Flatten_KeepsValueNamesVerbatim()
    {
        var flat = _h.Flatten(new Dictionary<string, object?> { ["Graphics.Quality"] = 3, ["(default)"] = "x" });
        Assert.Equal("3", flat["Graphics.Quality"]);
        Assert.Equal("x", flat["(default)"]);
    }
}
