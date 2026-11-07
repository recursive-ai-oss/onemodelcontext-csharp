using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RecursiveAI.OneModelContext.Extensions;
using RecursiveAI.OneModelContext.Extensions.Skills;
using RecursiveAI.OneModelContext.JsonRpc;
using RecursiveAI.OneModelContext.Protocol;
using Xunit;

namespace RecursiveAI.OneModelContext.Tests;

/// <summary>The Skills extension (Beta): publishing, listing, getting, directory reads, verification and the frontmatter reader.</summary>
public class SkillsTests
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

  private static SkillDefinition Pdf() => SkillDefinition.FromFiles(
    "pdf-processing",
    [
      new("SKILL.md", Utf8("---\nname: pdf-processing\ndescription: Extract, fill, and assemble PDF documents\n---\n\n# PDF processing\n")),
      new("templates/invoice.md", Utf8("# Invoice\n\nCustomer:\nAmount:\n")),
      new("templates/regional/eu.md", Utf8("EU\n")),
      new("assets/logo.png", [0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF, 0xFE]),
    ]);

  private static SkillDefinition Refunds() => SkillDefinition.FromFiles(
    "acme/billing/refunds",
    [new("SKILL.md", Utf8("---\nname: refunds\ndescription: Process customer refunds\nlicense: Apache-2.0\nmetadata:\n  author: acme\n  version: \"1.0\"\n---\nBody\n"))]);

  private static async Task<(ContextClient Client, Func<Task> Stop)> ConnectAsync(ContextServer server, EraPreference era, string version, bool declare = true)
  {
    var (a, b) = PipeEnd.CreatePair();
    var stop = new CancellationTokenSource();
    var serving = server.RunAsync(ContextTransport.Stream(a), stop.Token);
    var client = await ContextClient.ConnectAsync(
      ContextTransport.Stream(b),
      new ContextClientOptions { Era = era, SupportedVersions = [version], EraCache = null, Extensions = declare ? [SkillsExtension.Client()] : [] },
      Ct);
    return (client, async () =>
    {
      await client.DisposeAsync();
      await stop.CancelAsync();
      try
      {
        await serving.WaitAsync(TimeSpan.FromSeconds(10), Ct);
      }
      catch (OperationCanceledException)
      {
        // Stopping the server ends its loop by cancellation.
      }
    });
  }

  private static ContextServer NewServer(SkillsServerOptions? options = null)
  {
    var server = new ContextServer("skills", "1.0");
    server.UseSkills(options);
    server.AddSkill(Pdf());
    server.AddSkill(Refunds());
    return server;
  }

  [Fact]
  public async Task The_extension_is_advertised_only_when_the_host_opts_in()
  {
    var plain = new ContextServer("plain", "1.0");
    Assert.Null(plain.Capabilities.Extensions);
    var (client, stop) = await ConnectAsync(plain, EraPreference.Modern, ProtocolVersions.V20260728);
    Assert.False(client.ServerSupportsExtension(SkillsExtension.Id));
    await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.ListSkillsAsync(Ct));
    await stop();

    (client, stop) = await ConnectAsync(NewServer(), EraPreference.Modern, ProtocolVersions.V20260728);
    Assert.True(client.ServerSupportsExtension(SkillsExtension.Id));
    Assert.True(client.ServerExtensions[SkillsExtension.Id].GetProperty("directoryRead").GetBoolean());
    await stop();
  }

  [Fact]
  public async Task A_client_that_did_not_declare_the_extension_can_still_use_what_the_server_offers()
  {
    var (client, stop) = await ConnectAsync(NewServer(), EraPreference.Modern, ProtocolVersions.V20260728, declare: false);
    Assert.True(client.ServerSupportsExtension(SkillsExtension.Id));
    Assert.Equal(2, (await client.ListSkillsAsync(Ct)).Count);
    var read = await client.ReadResourceAsync("skill://pdf-processing/SKILL.md", Ct);
    Assert.Contains("PDF processing", ((TextResourceContents)read.Contents[0]).Text, StringComparison.Ordinal);
    await stop();
  }

  [Theory]
  [InlineData(ProtocolVersions.V20251125)]
  [InlineData(ProtocolVersions.V20250326)]
  public async Task Legacy_revisions_see_the_skill_files_as_resources_only(string version)
  {
    var (client, stop) = await ConnectAsync(NewServer(), EraPreference.Legacy, version);
    Assert.False(client.ServerSupportsExtension(SkillsExtension.Id));
    var listed = await client.ListResourcesAsync(cancellationToken: Ct);
    Assert.Contains(listed, r => r.Uri == "skill://acme/billing/refunds/SKILL.md" && r.MimeType == "text/markdown" && r.Name == "refunds");
    var text = await client.ReadResourceAsync("skill://pdf-processing/templates/invoice.md", Ct);
    Assert.Equal("# Invoice\n\nCustomer:\nAmount:\n", ((TextResourceContents)text.Contents[0]).Text);
    await stop();
  }

  [Fact]
  public async Task Skills_are_listed_with_their_frontmatter_and_a_complete_manifest()
  {
    var (client, stop) = await ConnectAsync(NewServer(), EraPreference.Modern, ProtocolVersions.V20260728);
    var skills = await client.ListSkillsAsync(Ct);

    Assert.Equal<string>(["skill://acme/billing/refunds/SKILL.md", "skill://pdf-processing/SKILL.md"], skills.Select(s => s.Uri));
    var pdf = skills.Single(s => s.Name == "pdf-processing");
    Assert.Equal("Extract, fill, and assemble PDF documents", pdf.Description);
    Assert.False(pdf.IsDynamic);
    Assert.Equal<string>(
      ["skill://pdf-processing/SKILL.md", "skill://pdf-processing/assets/logo.png", "skill://pdf-processing/templates/invoice.md", "skill://pdf-processing/templates/regional/eu.md"],
      pdf.Resources!.Select(r => r.Uri).Order(StringComparer.Ordinal));
    var invoice = pdf.Resources!.Single(r => r.Uri.EndsWith("invoice.md", StringComparison.Ordinal));
    Assert.Equal(Utf8("# Invoice\n\nCustomer:\nAmount:\n").Length, invoice.Size);
    Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(Utf8("# Invoice\n\nCustomer:\nAmount:\n"))), invoice.Digest);

    // The frontmatter passes through as written, including nested values and extra fields.
    var refunds = skills.Single(s => s.Name == "refunds");
    Assert.Equal("Apache-2.0", refunds.Frontmatter.GetProperty("license").GetString());
    Assert.Equal("1.0", refunds.Frontmatter.GetProperty("metadata").GetProperty("version").GetString());
    await stop();
  }

  [Fact]
  public async Task A_listing_is_paged_and_an_entry_is_never_split()
  {
    var (client, stop) = await ConnectAsync(NewServer(new SkillsServerOptions { PageSize = 1 }), EraPreference.Modern, ProtocolVersions.V20260728);
    var skills = await client.ListSkillsAsync(Ct);
    Assert.Equal(2, skills.Count);
    Assert.Equal(4, skills.Single(s => s.Name == "pdf-processing").Resources!.Count);

    var firstPage = await client.CallExtensionAsync(SkillsExtension.ListMethod, default, Ct);
    Assert.Equal(1, firstPage.GetProperty("skills").GetArrayLength());
    Assert.True(firstPage.TryGetProperty("nextCursor", out _));
    Assert.Equal("complete", firstPage.GetProperty("resultType").GetString());
    Assert.True(firstPage.TryGetProperty("ttlMs", out _));
    Assert.True(firstPage.TryGetProperty("cacheScope", out _));
    await stop();
  }

  [Fact]
  public async Task A_skill_can_be_got_by_uri_and_loaded_with_verification()
  {
    var (client, stop) = await ConnectAsync(NewServer(), EraPreference.Modern, ProtocolVersions.V20260728);
    var entry = await client.GetSkillAsync("skill://pdf-processing/SKILL.md", Ct);
    var loaded = await client.LoadSkillAsync(entry, Ct);
    Assert.StartsWith("---\nname: pdf-processing", loaded.Text, StringComparison.Ordinal);

    Assert.Equal("# Invoice\n\nCustomer:\nAmount:\n", Encoding.UTF8.GetString(await client.ReadSkillFileAsync(entry, "templates/invoice.md", Ct)));
    Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF, 0xFE }, await client.ReadSkillFileAsync(entry, "skill://pdf-processing/assets/logo.png", Ct));

    var unknown = await Assert.ThrowsAsync<ProtocolException>(async () => await client.GetSkillAsync("skill://nothing/SKILL.md", Ct));
    Assert.Equal(JsonRpcErrorCodes.InvalidParams, unknown.Code);
    var notSkillMd = await Assert.ThrowsAsync<ProtocolException>(async () => await client.GetSkillAsync("skill://pdf-processing/templates/invoice.md", Ct));
    Assert.Equal(JsonRpcErrorCodes.InvalidParams, notSkillMd.Code);
    await stop();
  }

  [Fact]
  public async Task A_file_the_entry_does_not_list_is_refused_without_a_request()
  {
    var (client, stop) = await ConnectAsync(NewServer(), EraPreference.Modern, ProtocolVersions.V20260728);
    var entry = await client.GetSkillAsync("skill://pdf-processing/SKILL.md", Ct);
    var other = await Assert.ThrowsAsync<SkillVerificationException>(async () => await client.ReadSkillFileAsync(entry, "skill://acme/billing/refunds/SKILL.md", Ct));
    Assert.Equal(SkillVerificationFailure.UnlistedFile, other.Failure);
    var climbing = await Assert.ThrowsAsync<SkillVerificationException>(async () => await client.ReadSkillFileAsync(entry, "../secret.md", Ct));
    Assert.Equal(SkillVerificationFailure.UnlistedFile, climbing.Failure);
    await stop();
  }

  [Fact]
  public async Task Directories_are_listed_one_level_at_a_time()
  {
    var (client, stop) = await ConnectAsync(NewServer(), EraPreference.Modern, ProtocolVersions.V20260728);
    var root = await client.ReadSkillDirectoryAsync("skill://pdf-processing", Ct);
    Assert.Equal<string>(["skill://pdf-processing/SKILL.md", "skill://pdf-processing/assets", "skill://pdf-processing/templates"], root.Select(r => r.Uri).Order(StringComparer.Ordinal));
    Assert.Equal(SkillsExtension.DirectoryMimeType, root.Single(r => r.Uri == "skill://pdf-processing/templates").MimeType);
    Assert.Equal("text/markdown", root.Single(r => r.Uri == "skill://pdf-processing/SKILL.md").MimeType);

    var templates = await client.ReadSkillDirectoryAsync("skill://pdf-processing/templates", Ct);
    Assert.Equal<string>(["skill://pdf-processing/templates/invoice.md", "skill://pdf-processing/templates/regional"], templates.Select(r => r.Uri));

    // The organizational prefix above a skill is a directory too.
    var prefix = await client.ReadSkillDirectoryAsync("skill://acme/billing", Ct);
    Assert.Equal<string>(["skill://acme/billing/refunds"], prefix.Select(r => r.Uri));

    foreach (var bad in (string[])["skill://pdf-processing/SKILL.md", "skill://nothing", "skill://pdf-processing/templates/"])
    {
      var error = await Assert.ThrowsAsync<ProtocolException>(async () => await client.ReadSkillDirectoryAsync(bad, Ct));
      Assert.Equal(JsonRpcErrorCodes.InvalidParams, error.Code);
    }

    await stop();
  }

  [Fact]
  public async Task Directory_reads_page_and_can_be_turned_off()
  {
    var (client, stop) = await ConnectAsync(NewServer(new SkillsServerOptions { PageSize = 1 }), EraPreference.Modern, ProtocolVersions.V20260728);
    Assert.Equal(3, (await client.ReadSkillDirectoryAsync("skill://pdf-processing", Ct)).Count);
    await stop();

    (client, stop) = await ConnectAsync(NewServer(new SkillsServerOptions { DirectoryRead = false }), EraPreference.Modern, ProtocolVersions.V20260728);
    Assert.False(client.ServerExtensions[SkillsExtension.Id].GetProperty("directoryRead").GetBoolean());
    await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.ReadSkillDirectoryAsync("skill://pdf-processing", Ct));
    var error = await Assert.ThrowsAsync<ProtocolException>(async () => await client.CallExtensionAsync(SkillsExtension.DirectoryReadMethod, Json.Parse("""{"uri":"skill://pdf-processing"}"""), Ct));
    Assert.Equal(JsonRpcErrorCodes.MethodNotFound, error.Code);
    await stop();
  }

  [Fact]
  public void Publishing_checks_names_paths_and_limits()
  {
    Assert.Throws<ArgumentException>(() => SkillDefinition.FromFiles("other-name", [new("SKILL.md", Utf8("---\nname: pdf\ndescription: d\n---\n"))]));
    Assert.Throws<ArgumentException>(() => SkillDefinition.FromFiles("Bad_Name", [new("SKILL.md", Utf8("---\nname: Bad_Name\ndescription: d\n---\n"))]));
    Assert.Throws<ArgumentException>(() => SkillDefinition.FromFiles("a--b", [new("SKILL.md", Utf8("---\nname: a--b\ndescription: d\n---\n"))]));
    Assert.Throws<ArgumentException>(() => SkillDefinition.FromFiles("x", [new("SKILL.md", Utf8("---\nname: x\n---\n"))]));
    Assert.Throws<ArgumentException>(() => SkillDefinition.FromFiles("x", [new("SKILL.md", Utf8("no frontmatter"))]));
    Assert.Throws<ArgumentException>(() => SkillDefinition.FromFiles("x", [new("readme.md", Utf8("x"))]));
    Assert.Throws<ArgumentException>(() => SkillDefinition.FromFiles("x", [new("SKILL.md", Utf8("---\nname: x\ndescription: d\n---\n")), new("../escape.md", Utf8("x"))]));
    Assert.Throws<ArgumentException>(() => SkillDefinition.FromFiles("x", [new("SKILL.md", Utf8("---\nname: x\ndescription: d\n---\n")), new("a b.md", Utf8("x"))]));
    Assert.Throws<ArgumentException>(() => SkillDefinition.FromFiles("x", [new("SKILL.md", Utf8("---\nname: x\ndescription: d\n---\n")), new("a//b.md", Utf8("x"))]));

    var tooMany = new List<KeyValuePair<string, byte[]>> { new("SKILL.md", Utf8("---\nname: x\ndescription: d\n---\n")) };
    for (var i = 0; i < SkillsExtension.MaxFilesPerSkill; i++)
    {
      tooMany.Add(new("f" + i + ".txt", []));
    }

    Assert.Throws<ArgumentException>(() => SkillDefinition.FromFiles("x", tooMany));
    Assert.True(SkillDefinition.IsValidName("a-b-9"));
    Assert.False(SkillDefinition.IsValidName("-a"));
    Assert.False(SkillDefinition.IsValidName("a-"));
    Assert.False(SkillDefinition.IsValidName(new string('a', 65)));
  }

  [Fact]
  public void Publishing_a_conflicting_file_or_the_same_skill_twice_is_refused()
  {
    var server = new ContextServer("s", "1");
    server.AddSkill(Pdf());
    Assert.Throws<InvalidOperationException>(() => server.AddSkill(Pdf()));
    Assert.Throws<InvalidOperationException>(() => server.UseSkills());

    // A file whose path is a directory of another skill cannot be published, and the server stays as it was.
    server.AddSkill(Refunds());
    var clash = SkillDefinition.FromFiles("acme", [new("SKILL.md", Utf8("---\nname: acme\ndescription: d\n---\n")), new("billing", Utf8("x"))]);
    Assert.Throws<InvalidOperationException>(() => server.AddSkill(clash));
  }

  [Fact]
  public async Task A_directory_on_disk_is_published_without_following_links()
  {
    var root = Path.Combine(Path.GetTempPath(), "skills-" + Guid.NewGuid().ToString("N"));
    var skillDir = Directory.CreateDirectory(Path.Combine(root, "git-workflow"));
    try
    {
      await File.WriteAllTextAsync(Path.Combine(skillDir.FullName, "SKILL.md"), "---\nname: git-workflow\ndescription: Follow the team's Git conventions\n---\nUse small commits.\n", Ct);
      Directory.CreateDirectory(Path.Combine(skillDir.FullName, "references"));
      await File.WriteAllTextAsync(Path.Combine(skillDir.FullName, "references", "GUIDE.md"), "guide\n", Ct);
      await File.WriteAllTextAsync(Path.Combine(root, "secret.txt"), "secret", Ct);
      File.CreateSymbolicLink(Path.Combine(skillDir.FullName, "link.txt"), Path.Combine(root, "secret.txt"));

      var skill = SkillDefinition.FromDirectory(skillDir.FullName, "team");
      Assert.Equal("team/git-workflow", skill.Path);
      Assert.Equal<string>(["SKILL.md", "references/GUIDE.md"], skill.Files.Select(f => f.Path));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  // ---- a server that does not tell the truth, to check what the client refuses ----

  private sealed class Lies
  {
    public string SkillMdText { get; set; } = "---\nname: demo\ndescription: A demo\n---\nBody\n";

    public string EntryFrontmatter { get; set; } = """{"name":"demo","description":"A demo"}""";

    public long? EntrySize { get; set; }

    public string? EntryDigest { get; set; }

    public string FileText { get; set; } = "supporting\n";

    public string EntryResources { get; set; } = string.Empty;
  }

  private static ContextServer LyingServer(Lies lies)
  {
    var server = new ContextServer("liar", "1.0");
    var skillMdBytes = Utf8("---\nname: demo\ndescription: A demo\n---\nBody\n");
    var fileBytes = Utf8("supporting\n");
    server.Resources.Add(
      new ResourceDefinition("skill://demo/SKILL.md", "demo") { MimeType = "text/markdown" },
      (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = "text/markdown", Text = lies.SkillMdText }] }));
    server.Resources.Add(
      new ResourceDefinition("skill://demo/notes.md", "notes.md") { MimeType = "text/markdown" },
      (uri, _) => ValueTask.FromResult(new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = "text/markdown", Text = lies.FileText }] }));

    var registration = new ExtensionRegistration(SkillsExtension.Id, Json.Parse("""{"directoryRead":false}"""), [ProtocolVersions.V20260728], official: true)
      .Request(SkillsExtension.ListMethod, (_, _) => ValueTask.FromResult(Json.Parse("{\"resultType\":\"complete\",\"skills\":[" + Entry() + "],\"ttlMs\":0,\"cacheScope\":\"private\"}")))
      .Request(SkillsExtension.GetMethod, (_, _) => ValueTask.FromResult(Json.Parse("{\"resultType\":\"complete\",\"skill\":" + Entry() + ",\"ttlMs\":0,\"cacheScope\":\"private\"}")));
    server.Extensions.Add(registration);
    return server;

    string Entry() =>
      "{\"uri\":\"skill://demo/SKILL.md\",\"frontmatter\":" + lies.EntryFrontmatter + ",\"resources\":"
      + (lies.EntryResources.Length > 0
        ? lies.EntryResources
        : "[{\"uri\":\"skill://demo/SKILL.md\",\"digest\":\"" + SkillsExtension.DigestOf(skillMdBytes) + "\",\"size\":" + skillMdBytes.Length + "},"
          + "{\"uri\":\"skill://demo/notes.md\",\"digest\":\"" + (lies.EntryDigest ?? SkillsExtension.DigestOf(fileBytes)) + "\",\"size\":" + (lies.EntrySize ?? fileBytes.Length) + "}]")
      + "}";
  }

  [Fact]
  public async Task Content_that_does_not_match_the_digest_is_refused()
  {
    var lies = new Lies { FileText = "tampered!\n" };
    lies.EntrySize = Utf8("tampered!\n").Length;
    var (client, stop) = await ConnectAsync(LyingServer(lies), EraPreference.Modern, ProtocolVersions.V20260728);
    var entry = (await client.ListSkillsAsync(Ct)).Single();
    var error = await Assert.ThrowsAsync<SkillVerificationException>(async () => await client.ReadSkillFileAsync(entry, "notes.md", Ct));
    Assert.Equal(SkillVerificationFailure.DigestMismatch, error.Failure);
    await stop();
  }

  [Fact]
  public async Task Content_of_the_wrong_length_is_refused_before_the_digest_is_computed()
  {
    var lies = new Lies { EntrySize = 5 };
    var (client, stop) = await ConnectAsync(LyingServer(lies), EraPreference.Modern, ProtocolVersions.V20260728);
    var entry = (await client.ListSkillsAsync(Ct)).Single();
    var error = await Assert.ThrowsAsync<SkillVerificationException>(async () => await client.ReadSkillFileAsync(entry, "notes.md", Ct));
    Assert.Equal(SkillVerificationFailure.SizeMismatch, error.Failure);
    await stop();
  }

  [Fact]
  public async Task A_changed_frontmatter_stops_the_load()
  {
    // The file's bytes match the digest, but the entry describes a different skill than the file declares.
    var lies = new Lies { EntryFrontmatter = """{"name":"demo","description":"A demo","allowed-tools":"Bash"}""" };
    var (client, stop) = await ConnectAsync(LyingServer(lies), EraPreference.Modern, ProtocolVersions.V20260728);
    var entry = (await client.ListSkillsAsync(Ct)).Single();
    var error = await Assert.ThrowsAsync<SkillVerificationException>(async () => await client.LoadSkillAsync(entry, Ct));
    Assert.Equal(SkillVerificationFailure.FrontmatterMismatch, error.Failure);
    await stop();
  }

  [Fact]
  public async Task An_honest_entry_loads_and_a_dynamic_skill_is_read_without_checks()
  {
    var (client, stop) = await ConnectAsync(LyingServer(new Lies()), EraPreference.Modern, ProtocolVersions.V20260728);
    var entry = (await client.ListSkillsAsync(Ct)).Single();
    Assert.Equal("Body", (await client.LoadSkillAsync(entry, Ct)).Text.Split('\n')[4]);
    await stop();

    (client, stop) = await ConnectAsync(LyingServer(new Lies { EntryResources = "\"dynamic\"" }), EraPreference.Modern, ProtocolVersions.V20260728);
    entry = (await client.ListSkillsAsync(Ct)).Single();
    Assert.True(entry.IsDynamic);
    Assert.Equal("supporting\n", Encoding.UTF8.GetString(await client.ReadSkillFileAsync(entry, "notes.md", Ct)));
    await stop();
  }

  [Theory]
  [InlineData("")]
  [InlineData("{}")]
  [InlineData("\"nothing\"")]
  public async Task An_entry_without_a_valid_resources_member_is_not_loadable(string resources)
  {
    // An empty string means "use the server's honest manifest"; the others are invalid shapes.
    var lies = new Lies { EntryResources = resources.Length == 0 ? string.Empty : resources };
    var (client, stop) = await ConnectAsync(LyingServer(lies), EraPreference.Modern, ProtocolVersions.V20260728);
    var listed = await client.ListSkillsAsync(Ct);
    Assert.Equal(resources.Length == 0 ? 1 : 0, listed.Count);
    if (resources.Length > 0)
    {
      var error = await Assert.ThrowsAsync<SkillVerificationException>(async () => await client.GetSkillAsync("skill://demo/SKILL.md", Ct));
      Assert.Equal(SkillVerificationFailure.InvalidEntry, error.Failure);
    }

    await stop();
  }

  // ---- the frontmatter reader ----

  private static string Yaml(string text) => SkillYaml.Parse(text).GetRawText();

  [Fact]
  public void Frontmatter_scalars_collections_and_comments_are_read()
  {
    Assert.Equal("""{"name":"x","description":"A: b","n":3,"f":1.5,"t":true,"z":null}""", Yaml("name: x\ndescription: \"A: b\"\nn: 3\nf: 1.5\nt: true\nz: ~ # nothing\n"));
    var quoted = SkillYaml.Parse("a: 'it''s'\nb: \"say \\\"hi\\\"\\n\"\n");
    Assert.Equal("it's", quoted.GetProperty("a").GetString());
    Assert.Equal("say \"hi\"\n", quoted.GetProperty("b").GetString());
    Assert.Equal("""{"metadata":{"author":"acme","version":"1.0"},"tags":["a","b"]}""", Yaml("metadata:\n  author: acme\n  version: \"1.0\"\ntags:\n  - a\n  - b\n"));
    Assert.Equal("""{"flow":["a","b",3],"map":{"k":"v"}}""", Yaml("flow: [a, b, 3]\nmap: {k: v}\n"));
    Assert.Equal("""{"allowed-tools":"Bash(git:*) Read"}""", Yaml("allowed-tools: Bash(git:*) Read\n"));
  }

  [Fact]
  public void Frontmatter_block_scalars_keep_or_fold_their_lines()
  {
    Assert.Equal("""{"d":"line one\nline two\n"}""", Yaml("d: |\n  line one\n  line two\n"));
    Assert.Equal("""{"d":"line one line two\n"}""", Yaml("d: >\n  line one\n  line two\n"));
    Assert.Equal("""{"d":"keep"}""", Yaml("d: |-\n  keep\n"));
    Assert.Equal("""{"d":"a\n\nb\n"}""", Yaml("d: |\n  a\n\n  b\n"));
  }

  [Theory]
  [InlineData("a: &anchor x\nb: *anchor\n")]
  [InlineData("a: !!str x\n")]
  [InlineData("? complex\n: key\n")]
  [InlineData("a: 1\na: 2\n")]
  [InlineData("a: [1, 2\n")]
  [InlineData("a: \"unterminated\n")]
  [InlineData("- just\n- a list\n")]
  [InlineData("a:\n\t- tab\n")]
  [InlineData("<<: {a: 1}\n")]
  public void Frontmatter_outside_the_supported_subset_is_refused_not_guessed(string yaml)
  {
    Assert.Throws<FormatException>(() => SkillYaml.Parse(yaml));
  }

  [Fact]
  public void Frontmatter_must_open_and_close_with_markers_and_stay_small()
  {
    Assert.Throws<FormatException>(() => SkillYaml.ParseFrontmatter(Utf8("name: x\n")));
    Assert.Throws<FormatException>(() => SkillYaml.ParseFrontmatter(Utf8("---\nname: x\n")));
    Assert.Throws<FormatException>(() => SkillYaml.ParseFrontmatter([0xFF, 0xFE, 0x00]));
    Assert.Throws<FormatException>(() => SkillYaml.ParseFrontmatter(Utf8("---\nname: " + new string('x', SkillYaml.MaxBytes) + "\n---\n")));
    Assert.Equal("""{"name":"x"}""", SkillYaml.ParseFrontmatter(Utf8("---\r\nname: x\r\n---\r\nbody")).GetRawText());
    Assert.Equal("""{"name":"x"}""", SkillYaml.ParseFrontmatter([0xEF, 0xBB, 0xBF, .. Utf8("---\nname: x\n---\n")]).GetRawText());

    var deep = string.Concat(Enumerable.Range(0, 64).Select(i => new string(' ', i * 2) + "k" + i + ":\n")) + new string(' ', 128) + "v: 1\n";
    Assert.Throws<FormatException>(() => SkillYaml.Parse(deep));
  }
}
