using System.Buffers.Binary;
using System.Text;
using Mcmere.Distribution.Contracts;
using Mcmere.Play.Core;

namespace Mcmere.Play.Tests;

public sealed class ServerListTests
{
    [Fact]
    public void CreatesTheUncompressedJavaNbtServerList()
    {
        var result = ServerListFile.Plan(null, "Test", "game.example", null);
        Assert.Equal(Convert.FromHexString("0a0000090007736572766572730a000000010800046e616d650004546573740800026970000c67616d652e6578616d706c650000"), result.Bytes);
        Assert.Equal(new ManagedServerEntry("Test", "game.example"), result.ManagedEntry);
        Assert.Equal(result.Bytes, ServerListFile.Plan(result.Bytes, "Test", "game.example", result.ManagedEntry).Bytes);
    }

    [Fact]
    public void AddingTheServerPreservesOtherEntriesAndUnknownNbtFields()
    {
        var personal = Entry("Personal", "personal.example", extras: true);
        var original = File(personal);
        var result = ServerListFile.Plan(original, "Test", "game.example", null);
        Assert.Equal(File(Entry("Test", "game.example"), personal), result.Bytes);
    }

    [Theory]
    [InlineData("GAME.EXAMPLE:25565")]
    [InlineData("game.example.")]
    public void ExistingVisibleAddressKeepsTheUsersNameAndOptionsWithoutDuplicates(string address)
    {
        var original = File(Entry("My custom name", address, extras: true));
        var result = ServerListFile.Plan(original, "Test", "game.example", null);
        Assert.Same(original, result.Bytes);
        Assert.Null(result.ManagedEntry);
    }

    [Fact]
    public void QuickPlayHiddenEntryBecomesVisibleWithoutLosingItsIcon()
    {
        var result = ServerListFile.Plan(File(Entry("Quick play", "game.example", extras: true, hidden: 1)), "Test", "game.example", null);
        Assert.Equal(File(Entry("Test", "game.example", extras: true, hidden: 0)), result.Bytes);
    }

    [Fact]
    public void EndpointAndManagedNameFollowTheReleaseAndPreservePersonalEntries()
    {
        var personal = Entry("Personal", "personal.example", extras: true);
        var result = ServerListFile.Plan(File(Entry("Old", "old.example", extras: true), personal),
            "New", "new.example:25566", new("Old", "old.example"));
        Assert.Equal(File(Entry("New", "new.example:25566", extras: true), personal), result.Bytes);
        Assert.Equal(new ManagedServerEntry("New", "new.example:25566"), result.ManagedEntry);
    }

    [Fact]
    public void UserRenamingIsPreservedWhenTheManagedAddressChanges()
    {
        var result = ServerListFile.Plan(File(Entry("Custom", "old.example", extras: true)),
            "New", "new.example", new("Old", "old.example"));
        Assert.Equal(File(Entry("Custom", "new.example", extras: true)), result.Bytes);
    }

    [Fact]
    public void UserEditedAddressIsKeptAndAmbiguousEntriesAreNotReassigned()
    {
        var changed = Entry("Old", "personal.example", extras: true);
        Assert.Equal(File(Entry("New", "new.example"), changed),
            ServerListFile.Plan(File(changed), "New", "new.example", new("Old", "old.example")).Bytes);
        var duplicate = Entry("Old", "old.example");
        Assert.Equal(File(Entry("New", "new.example"), duplicate, duplicate),
            ServerListFile.Plan(File(duplicate, duplicate), "New", "new.example", new("Old", "old.example")).Bytes);
    }

    [Fact]
    public void JapaneseAndSupplementaryCharactersUseJavaModifiedUtf8()
    {
        var result = ServerListFile.Plan(null, "世界😀", "game.example", null);
        Assert.Contains("e4b896e7958ceda0bdedb880", Convert.ToHexString(result.Bytes).ToLowerInvariant());
        Assert.Same(result.Bytes, ServerListFile.Plan(result.Bytes, "世界😀", "game.example", result.ManagedEntry).Bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1f8b")]
    [InlineData("0a0000")]
    [InlineData("0a0000090007736572766572730affffffff00")]
    [InlineData("0a0000090007736572766572730a7fffffff00")]
    [InlineData("0a0000090007736572766572730a00000000090007736572766572730a0000000000")]
    [InlineData("0a00000d00017800")]
    public void MalformedNbtIsRejected(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        Assert.Equal("server_list_conflict", Assert.Throws<DistributionException>(() =>
            ServerListFile.Plan(bytes, "Test", "game.example", null)).Code);
        Assert.Equal(hex, Convert.ToHexString(bytes).ToLowerInvariant());
    }

    [Fact]
    public void OversizedAndDeeplyNestedInputIsRejected()
    {
        Assert.Throws<DistributionException>(() => ServerListFile.Plan(new byte[ServerListFile.MaximumBytes + 1], "Test", "game.example", null));
        var nested = new List<byte> { 10, 0, 0 };
        for (var index = 0; index < 66; index++) nested.AddRange([10, 0, 1, (byte)'x']);
        nested.AddRange(new byte[67]);
        Assert.Throws<DistributionException>(() => ServerListFile.Plan(nested.ToArray(), "Test", "game.example", null));
    }

    private static byte[] File(params byte[][] entries)
    {
        using var file = new MemoryStream();
        file.Write([10, 0, 0]);
        // Unknown root long-array tag must survive, even though vanilla ignores it.
        file.Write(Convert.FromHexString("0c00056578747261000000020000000000000001ffffffffffffffff"));
        file.WriteByte(9); Text(file, "servers"); file.WriteByte(10);
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(count, entries.Length); file.Write(count);
        foreach (var entry in entries) file.Write(entry);
        file.WriteByte(0); return file.ToArray();
    }
    private static byte[] Entry(string name, string address, bool extras = false, byte? hidden = null)
    {
        using var entry = new MemoryStream();
        entry.WriteByte(8); Text(entry, "name"); Text(entry, name);
        entry.WriteByte(8); Text(entry, "ip"); Text(entry, address);
        if (extras)
        {
            entry.WriteByte(8); Text(entry, "icon"); Text(entry, "original-icon");
            entry.WriteByte(1); Text(entry, "acceptTextures"); entry.WriteByte(0);
            entry.Write(Convert.FromHexString("0a00076d6f64446174610900046c6973740300000002000000010000000200"));
        }
        if (hidden is byte value) { entry.WriteByte(1); Text(entry, "hidden"); entry.WriteByte(value); }
        entry.WriteByte(0); return entry.ToArray();
    }
    private static void Text(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)bytes.Length));
        stream.Write(length); stream.Write(bytes);
    }
}
