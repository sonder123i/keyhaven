using System.Reflection;
using AiGateway.ProxyKeys;
using AiGateway.RunLog;

namespace AiGateway.Tests;

public class RunLogTests
{
    private const string RealKey = "sk-real-RUNLOG-0123456789";
    private const string ProxyPlaintext = "sk-aigw-RUNLOG-forged-0123456789";
    private const string BodyMarker = "BODY-MARKER-must-never-be-logged";

    private static RunLogEntry Entry(int n) =>
        new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero).AddSeconds(n), null, "p", n.ToString(), 200, TimeSpan.FromMilliseconds(5), RunLogOutcome.Completed);

    [Fact]
    public void Keeps_only_the_latest_500_entries_newest_first()
    {
        var ring = new RunLogRing();

        for (var n = 1; n <= 501; n++)
        {
            ring.Append(Entry(n));
        }

        var snapshot = ring.Snapshot();
        Assert.Equal(500, snapshot.Count);
        Assert.Equal(("501", "2"), (snapshot[0].Model, snapshot[^1].Model));
    }

    [Fact]
    public void A_snapshot_is_a_frozen_copy()
    {
        var ring = new RunLogRing();
        ring.Append(Entry(1));
        var before = ring.Snapshot();

        ring.Append(Entry(2));

        Assert.Single(before);
        Assert.Equal(2, ring.Snapshot().Count);
    }

    [Fact]
    public void Concurrent_appends_never_corrupt_the_ring()
    {
        var ring = new RunLogRing();

        Parallel.For(0, 4000, n => ring.Append(Entry(n % 60)));

        Assert.Equal(500, ring.Snapshot().Count);
    }

    [Fact]
    public void The_public_surface_exposes_no_way_to_write_or_to_read_secrets()
    {
        var entryMembers = typeof(RunLogEntry).GetMembers(BindingFlags.Public | BindingFlags.Instance).Select(m => m.Name);
        Assert.DoesNotContain(entryMembers, n => n == "Plaintext");
        Assert.DoesNotContain(entryMembers, n => n == "Key");
        Assert.DoesNotContain(entryMembers, n => n == "Sha256");

        Assert.False(typeof(RunLogRing).IsPublic);
        var members = typeof(IRunLog).GetMembers().Select(m => m.Name).Where(n => !n.StartsWith("get_", StringComparison.Ordinal));
        Assert.Equal(["Capacity", "Snapshot"], members.OrderBy(n => n));
        Assert.DoesNotContain(typeof(IRunLog).GetMembers(), m => m.Name.StartsWith("set_", StringComparison.Ordinal));
    }

    [Fact]
    public void The_run_log_code_never_touches_exceptions_or_free_text()
    {
        var files = RepoPaths.SourceFiles(Path.Combine(RepoPaths.Src, "AiGateway", "RunLog"))
            .Append(Path.Combine(RepoPaths.Src, "AiGateway", "Hosting", "RunRequestState.cs"));

        Assert.All(files, f =>
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotContain("Exception", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Message", text, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void An_entry_carries_only_structured_safe_fields()
    {
        var entry = new RunLogEntry(
            DateTimeOffset.UtcNow,
            new ProxyKeyFingerprint("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            "deepseek", "deepseek-chat", 429, TimeSpan.FromMilliseconds(120), RunLogOutcome.UpstreamInterrupted);

        var printed = string.Join('|', entry.ToString(), entry.Fingerprint?.Value, entry.Provider, entry.Model);

        Assert.DoesNotContain(RealKey, printed);
        Assert.DoesNotContain(ProxyPlaintext, printed);
        Assert.DoesNotContain(BodyMarker, printed);
        Assert.Equal("01234567", entry.Fingerprint?.Value);
        Assert.Equal(RunLogOutcome.UpstreamInterrupted, entry.Outcome);
    }
}
