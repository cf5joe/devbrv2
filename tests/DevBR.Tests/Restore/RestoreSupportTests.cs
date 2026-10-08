using System.Text;
using System.Text.Json.Nodes;
using DevBR.Application.Machine;
using DevBR.Infrastructure.State;
using DevBR.Restore;

namespace DevBR.Tests.Restore;

public sealed class RestoreSupportTests
{
    private static string Edit(string original, string merged)
        => Encoding.UTF8.GetString(JsoncEditor.Apply(Encoding.UTF8.GetBytes(original), JsonNode.Parse(merged)!) ?? throw new InvalidOperationException("Edit was not possible."));

    [Fact]
    public void Jsonc_edits_keep_comments_and_layout()
    {
        const string original = """
            {
                // Editor
                "editor.fontSize": 16, // keep me
                /* block */
                "files.eol": "\n",
                "mcp": { "servers": { "local": { "command": "node" } } },
            }
            """;

        var edited = Edit(original, """
            { "editor.fontSize": 16, "files.eol": "\n", "mcp": { "servers": { "local": { "command": "node" }, "git": { "command": "uvx", "args": ["mcp-server-git"] } } }, "workbench.colorTheme": "Dark" }
            """);

        Assert.Contains("// Editor", edited, StringComparison.Ordinal);
        Assert.Contains("// keep me", edited, StringComparison.Ordinal);
        Assert.Contains("/* block */", edited, StringComparison.Ordinal);
        Assert.Contains("\"workbench.colorTheme\": \"Dark\"", edited, StringComparison.Ordinal);
        Assert.Contains("    \"workbench.colorTheme\"", edited, StringComparison.Ordinal); // the file's own four-space indent
        Assert.Contains("mcp-server-git", edited, StringComparison.Ordinal);
    }

    [Fact]
    public void Jsonc_edits_replace_changed_values_and_handle_empty_objects_and_bom()
    {
        Assert.Equal("{\n  \"a\": 2\n}", Edit("{\n  \"a\": 1\n}", """{ "a": 2 }"""));
        Assert.Equal(JsonNode.Parse("""{ "a": 1 }"""), JsonNode.Parse(Edit("{}", """{ "a": 1 }""")), JsonNode.DeepEquals);

        var bom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{ \"a\": 1 }")).ToArray();
        var result = JsoncEditor.Apply(bom, JsonNode.Parse("""{ "a": 1, "b": true }""")!)!;
        Assert.True(result.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
    }

    [Fact]
    public void Jsonc_edits_refuse_what_they_cannot_reproduce()
    {
        // Merges never remove keys; a "merge result" without one cannot be produced by span edits.
        Assert.Null(JsoncEditor.Apply("""{ "a": 1, "b": 2 }"""u8.ToArray(), JsonNode.Parse("""{ "a": 1 }""")!));
        Assert.Null(JsoncEditor.Apply("[1, 2]"u8.ToArray(), JsonNode.Parse("[1, 2, 3]")!));
    }

    [Fact]
    public void Approved_effects_permit_applying_and_undoing_only_the_approved_change()
    {
        var created = RestoreEffects.Key("SetEnvironmentVariable", "ENV:Machine:JAVA_HOME", "NoConflict", "Elevated", null, RestoreEffects.Sha256(@"C:\jdk"), "absent");
        var replaced = RestoreEffects.Key("SetEnvironmentVariable", "ENV:Machine:GOPATH", "UseBackup", "Elevated", null, RestoreEffects.Sha256(@"C:\go"),
            "value:" + RestoreEffects.Sha256(@"D:\go")[..16]);
        var path = RestoreEffects.Key("AppendPathEntry", "PATH (Machine)", "NoConflict", "Elevated", null, null, @"C:\jdk\bin");
        var user = RestoreEffects.Key("SetEnvironmentVariable", "ENV:Machine:USERLEVEL", "NoConflict", "User", null, RestoreEffects.Sha256("x"), "absent");
        var approved = new HashSet<string>([created, replaced, path, user], StringComparer.Ordinal);

        Assert.True(RestoreEffects.Permits(approved, new("JAVA_HOME", null, @"C:\jdk", false)));
        Assert.True(RestoreEffects.Permits(approved, new("JAVA_HOME", @"C:\jdk", null, false)));          // undo: remove what was created
        Assert.False(RestoreEffects.Permits(approved, new("JAVA_HOME", @"C:\other", null, false)));       // not ours to remove
        Assert.True(RestoreEffects.Permits(approved, new("GOPATH", @"C:\go", @"D:\go", false)));          // undo: back to the recorded value
        Assert.False(RestoreEffects.Permits(approved, new("GOPATH", @"C:\go", @"E:\anything", false)));
        Assert.False(RestoreEffects.Permits(approved, new("USERLEVEL", null, "x", false)));               // not an elevated effect

        Assert.True(RestoreEffects.Permits(approved, new("Path", @"C:\Windows;", @"C:\Windows;C:\jdk\bin", true)));
        Assert.True(RestoreEffects.Permits(approved, new("Path", @"C:\Windows;C:\jdk\bin;C:\Later", @"C:\Windows;C:\Later", true)));
        Assert.False(RestoreEffects.Permits(approved, new("Path", @"C:\Windows", @"C:\jdk\bin", true)));
        Assert.False(RestoreEffects.Permits(approved, new("Path", @"C:\Windows", null, true)));
    }

    [Fact]
    public void Rollback_copies_are_encrypted_and_round_trip()
    {
        using var temp = new TempDirectory();
        var store = new ProtectedRollbackStore(temp.Combine("rollback"));
        var job = Guid.NewGuid();
        var content = Encoding.UTF8.GetBytes(new string('s', 3 << 20) + "SECRET-MARKER");

        var id = store.Save(job, new MemoryStream(content));
        var onDisk = File.ReadAllBytes(Directory.GetFiles(temp.Combine("rollback"), "*.bin", SearchOption.AllDirectories).Single());
        Assert.DoesNotContain("SECRET-MARKER", Encoding.UTF8.GetString(onDisk), StringComparison.Ordinal);

        using (var restored = store.Open(job, id))
        {
            Assert.False(restored.CanSeek); // streamed chunk by chunk, never buffered whole
            using var copy = new MemoryStream();
            restored.CopyTo(copy, 4096);
            Assert.Equal(content, copy.ToArray());
        }

        // A truncated copy is reported as damaged rather than returning partial content silently.
        var file = Directory.GetFiles(temp.Combine("rollback"), "*.bin", SearchOption.AllDirectories).Single();
        File.WriteAllBytes(file, onDisk[..(onDisk.Length - 10)]);
        using (var truncated = store.Open(job, id))
        {
            Assert.Throws<InvalidDataException>(() => truncated.CopyTo(Stream.Null));
        }

        Assert.Throws<ArgumentException>(() => store.Open(job, @"..\..\escape"));

        store.DeleteJob(job);
        Assert.Empty(Directory.GetFiles(temp.Combine("rollback"), "*", SearchOption.AllDirectories));
    }
}
