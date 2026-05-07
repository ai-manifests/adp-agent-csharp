using Adp.Agent.Anchor;
using Xunit;

namespace Adp.Agent.Anchor.Tests;

public class SchedulerTests
{
    [Fact]
    public async Task MockBlockchainStore_roundtrips_a_published_record()
    {
        var store = new MockBlockchainStore();
        var record = new CalibrationRecord(
            AgentId: "did:adp:test-runner-v2",
            Domain: "code.correctness",
            Value: 0.7812,
            SampleSize: 42,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            JournalHash: "a1b2c3");

        var tx = await store.PublishCalibrationAsync(record);
        Assert.StartsWith("0xmock", tx);

        var retrieved = await store.GetCalibrationAsync("did:adp:test-runner-v2", "code.correctness");
        Assert.NotNull(retrieved);
        Assert.Equal(0.7812, retrieved!.Value);
        Assert.Equal(42, retrieved.SampleSize);
        Assert.Equal(record.JournalHash, retrieved.JournalHash);
    }

    [Fact]
    public async Task MockBlockchainStore_returns_null_for_unknown_agent()
    {
        var store = new MockBlockchainStore();
        var result = await store.GetCalibrationAsync("did:adp:nobody", "code.correctness");
        Assert.Null(result);
    }

    [Fact]
    public async Task MockBlockchainStore_overwrites_on_republish()
    {
        var store = new MockBlockchainStore();
        var record1 = new CalibrationRecord("did:adp:a", "d", 0.5, 10, 0, "h1");
        var record2 = record1 with { Value = 0.7, SampleSize = 20, JournalHash = "h2" };

        await store.PublishCalibrationAsync(record1);
        await store.PublishCalibrationAsync(record2);

        var retrieved = await store.GetCalibrationAsync("did:adp:a", "d");
        Assert.NotNull(retrieved);
        Assert.Equal(0.7, retrieved!.Value);
        Assert.Equal("h2", retrieved.JournalHash);
        Assert.Equal(2, store.PublishCount);
    }

    [Fact]
    public void BlockchainStoreFactory_returns_mock_for_mock_target()
    {
        var cfg = new CalibrationAnchorConfig { Enabled = true, Target = "mock" };
        var store = BlockchainStoreFactory.Create(cfg);
        Assert.IsType<MockBlockchainStore>(store);
    }

    [Fact]
    public void BlockchainStoreFactory_returns_null_when_disabled()
    {
        var cfg = new CalibrationAnchorConfig { Enabled = false, Target = "mock" };
        Assert.Null(BlockchainStoreFactory.Create(cfg));
    }

    [Fact]
    public void BlockchainStoreFactory_returns_null_for_neo_without_rpc_url()
    {
        var cfg = new CalibrationAnchorConfig
        {
            Enabled = true,
            Target = "neo-custom",
            // RpcUrl and ContractHash intentionally missing
        };
        Assert.Null(BlockchainStoreFactory.Create(cfg));
    }
}
