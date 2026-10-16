using GameProfiles.Config;

namespace GameProfiles.Tests;

public sealed class VdfWriterTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void RoundTrip_LocalConfig_IsByteForByte(string newLine)
    {
        var text = Fixtures.Text("localconfig.vdf", newLine);
        Assert.Equal(text, Vdf.Serialize(Vdf.Parse(text), newLine));
    }

    [Fact]
    public void RoundTrip_LoginUsers()
    {
        var text = Fixtures.Text("loginusers.vdf", "\n").TrimEnd('\n') + "\n";
        Assert.Equal(text, Vdf.Serialize(Vdf.Parse(text)));
    }

    [Fact]
    public void Escapes_QuotesBackslashesNewlinesTabs()
    {
        var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = "\"C:\\Tools\\GameProfilesApp.exe\" run x -- %command%",
            ["b"] = "line1\nline2\tend",
            ["key \"q\""] = "",
        };
        var text = Vdf.Serialize(root);
        Assert.Contains("\"a\"\t\t\"\\\"C:\\\\Tools\\\\GameProfilesApp.exe\\\" run x -- %command%\"", text);
        Assert.Contains("\"b\"\t\t\"line1\\nline2\\tend\"", text);

        var back = Vdf.Parse(text);
        Assert.Equal(root["a"], back.Str("a"));
        Assert.Equal(root["b"], back.Str("b"));
        Assert.Equal("", back.Str("key \"q\""));
    }

    [Fact]
    public void Nested_TabIndentedWithBracesOnOwnLines()
    {
        var inner = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["k"] = "v" };
        var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["Root"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["Inner"] = inner, ["x"] = "1" },
        };
        Assert.Equal("\"Root\"\n{\n\t\"Inner\"\n\t{\n\t\t\"k\"\t\t\"v\"\n\t}\n\t\"x\"\t\t\"1\"\n}\n", Vdf.Serialize(root));
    }

    [Fact]
    public void Edit_KeepsKeyOrderAndCase_AppendsNewKeys()
    {
        var text = Fixtures.Text("localconfig.vdf", "\n").Replace("\"apps\"", "\"Apps\"");
        var root = Vdf.Parse(text);
        var apps = root.Child("UserLocalConfigStore")!.Child("software")!.Child("VALVE")!.Child("steam")!.Child("apps")!;
        apps.Child("1091500")!["LaunchOptions"] = "-dx12";
        apps.GetOrAddChild("999")["LaunchOptions"] = "new";

        var output = Vdf.Serialize(root);
        Assert.Contains("\"Apps\"", output);
        Assert.DoesNotContain("\"apps\"", output);
        Assert.Equal(new[] { "1091500", "1245620", "570", "999" }, apps.Keys.ToArray());

        // only the edited line differs, and the new app is appended at the end of Apps
        const string lastApp = "\"LaunchOptions\"\t\t\"\"\n\t\t\t\t\t}\n";
        var expected = text
            .Replace("\"LaunchOptions\"\t\t\"-skipStartScreen\"", "\"LaunchOptions\"\t\t\"-dx12\"")
            .Replace(lastApp, lastApp + "\t\t\t\t\t\"999\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"new\"\n\t\t\t\t\t}\n");
        Assert.Equal(expected, output);
    }

    [Fact]
    public void GetOrAddChild_FindsAnyCase()
    {
        var root = Vdf.Parse("\"A\"\n{\n\t\"Apps\"\n\t{\n\t}\n}\n");
        var a = root.Child("a")!;
        var apps = a.GetOrAddChild("apps");
        Assert.Same(a.Child("Apps"), apps);
        Assert.Single(a.Keys);
        Assert.Equal("Apps", a.Keys.Single());
    }

    [Fact]
    public void Save_WritesAtomicallyWithoutBom()
    {
        using var tmp = new TempDir();
        var path = tmp.File("sub", "x.vdf");
        var root = Vdf.Parse("\"a\"\n{\n\t\"b\"\t\t\"c\"\n}\n");
        Vdf.Save(path, root);
        Vdf.Save(path, root); // replace path
        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("\"a\"\n{\n\t\"b\"\t\t\"c\"\n}\n", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }
}
