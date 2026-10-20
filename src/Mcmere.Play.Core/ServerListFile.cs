using System.Buffers.Binary;
using System.Text;
using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public sealed record ManagedServerEntry(string Name, string Address);
public sealed record ServerListUpdate(byte[] Bytes, ManagedServerEntry? ManagedEntry);

public static class ServerListFile
{
    public const int MaximumBytes = 8 * 1024 * 1024;

    public static ServerListUpdate Plan(byte[]? original, string name, string address, ManagedServerEntry? previous)
    {
        ManifestValidation.Endpoint(address);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) throw Conflict();
        if (original is { Length: > MaximumBytes }) throw Conflict();
        var rootReader = new Reader(original ?? [10, 0, 0, 0]);
        var root = rootReader.Tag();
        rootReader.RequireEnd();
        if (root.Type != 10) throw Conflict();
        var fields = Fields(root.Payload);
        var listTag = Unique(fields, "servers");
        if (listTag is not null && listTag.Type != 9) throw Conflict();
        var servers = listTag is null ? [] : Entries(listTag.Payload);
        var matches = servers.Where(entry => SameAddress(Value(entry, "ip"), address)).ToArray();
        var target = matches.FirstOrDefault(entry => !Hidden(entry)) ?? matches.FirstOrDefault();
        var owned = FindOwned(servers, previous);
        ManagedServerEntry? managed;
        if (target is not null && !Hidden(target) && !ReferenceEquals(target, owned))
            return new(original!, previous);
        if (target is not null)
        {
            if (Hidden(target)) Put(target, StringTag("name", name));
            else if (Value(target, "name") == previous?.Name) Put(target, StringTag("name", name));
            Put(target, StringTag("ip", address));
            if (Hidden(target)) Put(target, Create(1, "hidden", [0]));
            managed = new(name, address);
        }
        else if (owned is not null)
        {
            if (Value(owned, "name") == previous?.Name) Put(owned, StringTag("name", name));
            Put(owned, StringTag("ip", address));
            if (Hidden(owned)) Put(owned, Create(1, "hidden", [0]));
            managed = new(name, address);
        }
        else
        {
            servers.Insert(0, [StringTag("name", name), StringTag("ip", address)]);
            managed = new(name, address);
        }
        using var list = new MemoryStream();
        list.WriteByte(10);
        WriteInt(list, servers.Count);
        foreach (var server in servers) list.Write(Compound(server));
        Put(fields, listTag is null ? Create(9, "servers", list.ToArray()) : listTag with { Payload = list.ToArray() });
        using var output = new MemoryStream();
        output.Write(root.Header);
        output.Write(Compound(fields));
        if (output.Length > MaximumBytes) throw Conflict();
        var bytes = output.ToArray();
        return original is not null && bytes.AsSpan().SequenceEqual(original) ? new(original, previous) : new(bytes, managed);
    }

    private static List<Tag>? FindOwned(List<List<Tag>> entries, ManagedServerEntry? previous)
    {
        if (previous is null) return null;
        var addresses = entries.Where(entry => SameAddress(Value(entry, "ip"), previous.Address)).ToArray();
        var exact = addresses.Where(entry => Value(entry, "name") == previous.Name).ToArray();
        return exact.Length == 1 ? exact[0] : addresses.Length == 1 ? addresses[0] : null;
    }
    private static bool SameAddress(string? first, string second) => first is not null && EndpointKey(first) == EndpointKey(second);
    private static string EndpointKey(string value)
    {
        if (Uri.TryCreate("minecraft://" + value, UriKind.Absolute, out var uri))
            return uri.IdnHost.TrimEnd('.').ToLowerInvariant() + ":" + (uri.Port < 0 ? 25565 : uri.Port);
        return value.ToLowerInvariant();
    }
    private static string? Value(List<Tag> fields, string name)
    {
        var tag = Unique(fields, name);
        if (tag is null) return null;
        if (tag.Type != 8) throw Conflict();
        var reader = new Reader(tag.Payload);
        var value = reader.Text();
        reader.RequireEnd();
        return value;
    }
    private static bool Hidden(List<Tag> fields)
    {
        var tag = Unique(fields, "hidden");
        if (tag is null) return false;
        if (tag.Type != 1) throw Conflict();
        return tag.Payload[0] != 0;
    }
    private static Tag? Unique(List<Tag> fields, string name)
    {
        var matches = fields.Where(tag => tag.Name == name).ToArray();
        if (matches.Length > 1) throw Conflict();
        return matches.FirstOrDefault();
    }
    private static void Put(List<Tag> fields, Tag tag)
    {
        _ = Unique(fields, tag.Name);
        var index = fields.FindIndex(item => item.Name == tag.Name);
        if (index < 0) fields.Add(tag); else fields[index] = tag;
    }
    private static List<Tag> Fields(byte[] payload)
    {
        var reader = new Reader(payload);
        var result = new List<Tag>();
        while (reader.Peek() != 0) result.Add(reader.Tag());
        reader.Byte();
        reader.RequireEnd();
        return result;
    }
    private static List<List<Tag>> Entries(byte[] payload)
    {
        var reader = new Reader(payload);
        var type = reader.Byte();
        var count = reader.Count();
        if (count > 4096 || type != 10 && !(type == 0 && count == 0)) throw Conflict();
        var entries = new List<List<Tag>>();
        for (var index = 0; index < count; index++)
        {
            var start = reader.Position;
            reader.Skip(10);
            entries.Add(Fields(payload[start..reader.Position]));
        }
        reader.RequireEnd();
        return entries;
    }
    private static byte[] Compound(List<Tag> fields)
    {
        using var stream = new MemoryStream();
        foreach (var tag in fields) { stream.Write(tag.Header); stream.Write(tag.Payload); }
        stream.WriteByte(0);
        return stream.ToArray();
    }
    private static Tag StringTag(string name, string value) => Create(8, name, TextBytes(value));
    private static Tag Create(byte type, string name, byte[] payload) => new(type, name, [type, .. TextBytes(name)], payload);
    private sealed record Tag(byte Type, string Name, byte[] Header, byte[] Payload);
    private static byte[] TextBytes(string value)
    {
        using var stream = new MemoryStream();
        stream.Write([0, 0]);
        // NBT strings use Java DataInput/DataOutput modified UTF-8, including UTF-16 surrogate pairs.
        foreach (var ch in value)
        {
            if (ch is > '\0' and <= '\x7f') stream.WriteByte((byte)ch);
            else if (ch <= '\x7ff') stream.Write([(byte)(0xc0 | ch >> 6), (byte)(0x80 | ch & 0x3f)]);
            else stream.Write([(byte)(0xe0 | ch >> 12), (byte)(0x80 | ch >> 6 & 0x3f), (byte)(0x80 | ch & 0x3f)]);
        }
        if (stream.Length - 2 > ushort.MaxValue) throw Conflict();
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)(bytes.Length - 2));
        return bytes;
    }
    private static void WriteInt(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
    private static DistributionException Conflict() =>
        new("server_list_conflict", "Minecraftのサーバー一覧（servers.dat）を安全に更新できません。元のファイルを保持しています。");

    private sealed class Reader(byte[] data)
    {
        public int Position { get; private set; }
        private int _nodes;
        public byte Peek() => Position < data.Length ? data[Position] : throw Conflict();
        public byte Byte() { var value = Peek(); Position++; return value; }
        public void RequireEnd() { if (Position != data.Length) throw Conflict(); }
        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > data.Length - Position) throw Conflict();
            var span = data.AsSpan(Position, count); Position += count; return span;
        }
        public int Count()
        {
            var count = BinaryPrimitives.ReadInt32BigEndian(Take(4));
            if (count < 0) throw Conflict();
            return count;
        }
        public string Text()
        {
            var bytes = Take(BinaryPrimitives.ReadUInt16BigEndian(Take(2)));
            var text = new StringBuilder(bytes.Length);
            for (var index = 0; index < bytes.Length;)
            {
                var first = bytes[index++];
                if (first < 0x80) { text.Append((char)first); continue; }
                var length = (first & 0xe0) == 0xc0 ? 2 : (first & 0xf0) == 0xe0 ? 3 : 0;
                if (length == 0 || bytes.Length - index < length - 1) throw Conflict();
                var value = first & (length == 2 ? 0x1f : 0x0f);
                for (var part = 1; part < length; part++)
                {
                    var next = bytes[index++];
                    if ((next & 0xc0) != 0x80) throw Conflict();
                    value = value << 6 | next & 0x3f;
                }
                text.Append((char)value);
            }
            return text.ToString();
        }
        public Tag Tag()
        {
            var start = Position;
            var type = Byte();
            var name = Text();
            var payload = Position;
            Skip(type);
            return new(type, name, data[start..payload], data[payload..Position]);
        }
        public void Skip(byte type, int depth = 0)
        {
            if (depth > 64 || ++_nodes > 100000) throw Conflict();
            switch (type)
            {
                case 1: Take(1); break;
                case 2: Take(2); break;
                case 3: case 5: Take(4); break;
                case 4: case 6: Take(8); break;
                case 7: Take(Count()); break;
                case 8: Text(); break;
                case 9:
                    var elementType = Byte(); var count = Count();
                    if (count > 100000 || elementType > 12 || elementType == 0 && count != 0) throw Conflict();
                    for (var index = 0; index < count; index++) Skip(elementType, depth + 1);
                    break;
                case 10:
                    while (true)
                    {
                        var child = Byte(); if (child == 0) break;
                        Text(); Skip(child, depth + 1);
                    }
                    break;
                case 11: case 12:
                    var size = type == 11 ? 4 : 8;
                    var elements = Count();
                    if (elements > (data.Length - Position) / size) throw Conflict();
                    Take(elements * size);
                    break;
                default: throw Conflict();
            }
        }
    }
}
