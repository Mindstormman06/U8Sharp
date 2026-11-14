using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace U8Sharp;

public sealed record U8Entry(string Path, byte[] Data);

public sealed class U8File
{
    public const uint Magic = 0x55AA382D;
    public const int DefaultAlignment = 0x20;

    private const int HeaderSize = 0x20;
    private const int NodeSize = 0x0C;

    public List<U8Entry> Entries { get; } = [];

    public string RootName { get; set; } = "";

    public int Alignment { get; set; } = DefaultAlignment;

    public int Count => Entries.Count;

    public U8Entry? this[string path]
    {
        get
        {
            int at = IndexOf(path);
            return at >= 0 ? Entries[at] : null;
        }
    }

    public bool Contains(string path) => IndexOf(path) >= 0;

    public bool TryGet(string path, [NotNullWhen(true)] out U8Entry? entry)
    {
        entry = this[path];
        return entry is not null;
    }

    public void Set(string path, byte[] data)
    {
        int at = IndexOf(path);
        if (at >= 0) Entries[at] = new U8Entry(path, data);
        else Entries.Add(new U8Entry(path, data));
    }

    public bool Remove(string path)
    {
        int at = IndexOf(path);
        if (at < 0) return false;
        Entries.RemoveAt(at);
        return true;
    }

    public int IndexOf(string path) => Entries.FindIndex(e => e.Path == path);

    public static bool IsU8(ReadOnlySpan<byte> data)
        => data.Length >= HeaderSize && BinaryPrimitives.ReadUInt32BigEndian(data) == Magic;

    // ---- reading ----------------------------------------------------------

    public static U8File FromFile(string path) => FromBinary(File.ReadAllBytes(path));

    public static U8File FromBinary(ReadOnlySpan<byte> data)
    {
        if (!IsU8(data))
            throw new InvalidDataException("Not a U8 archive.");

        int root = (int)BinaryPrimitives.ReadUInt32BigEndian(data[0x04..]);
        if (data[root] != 1)
            throw new InvalidDataException("U8 root node is not a directory.");

        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(root + 0x08)..]);
        int strings = root + count * NodeSize;

        U8File archive = new() { RootName = NameAt(data, strings, root) };
        Stack<(int End, string Path)> dirs = [];

        for (int i = 1; i < count; i++)
        {
            while (dirs.Count > 0 && i >= dirs.Peek().End) dirs.Pop();

            int node = root + i * NodeSize;
            string name = NameAt(data, strings, node);
            string path = dirs.Count > 0 ? dirs.Peek().Path + "/" + name : name;
            uint a = BinaryPrimitives.ReadUInt32BigEndian(data[(node + 0x04)..]);
            uint b = BinaryPrimitives.ReadUInt32BigEndian(data[(node + 0x08)..]);

            if (data[node] != 0)
            {
                if (b <= i || b > count)
                    throw new InvalidDataException($"U8 directory '{path}' has an invalid end index {b}.");
                dirs.Push(((int)b, path));
                continue;
            }

            if ((long)a + b > data.Length)
                throw new InvalidDataException($"U8 file '{path}' runs outside the archive.");
            archive.Entries.Add(new U8Entry(path, data.Slice((int)a, (int)b).ToArray()));
        }

        return archive;
    }

    private static string NameAt(ReadOnlySpan<byte> data, int strings, int node)
    {
        int offset = (int)(BinaryPrimitives.ReadUInt32BigEndian(data[node..]) & 0xFFFFFF);
        ReadOnlySpan<byte> rest = data[(strings + offset)..];
        int end = rest.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? rest : rest[..end]);
    }

    // ---- writing ----------------------------------------------------------

    public void WriteTo(string path) => File.WriteAllBytes(path, Write());

    public void Write(Stream output) => output.Write(Write());

    public byte[] Write()
    {
        Dir tree = new(RootName);
        foreach (U8Entry entry in Entries) tree.Add(entry);

        List<Node> nodes = [];
        tree.Flatten(nodes, -1);

        using MemoryStream names = new();
        int[] nameOffsets = new int[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            nameOffsets[i] = (int)names.Length;
            names.Write(Encoding.UTF8.GetBytes(nodes[i].Name));
            names.WriteByte(0);
        }

        int tableSize = nodes.Count * NodeSize + (int)names.Length;
        int dataStart = Align(HeaderSize + tableSize, Alignment);

        Dictionary<U8Entry, int> offsets = new(ReferenceEqualityComparer.Instance);
        int at = dataStart;
        foreach (U8Entry entry in Entries.OrderBy(e => e.Path, PathComparer.Instance))
        {
            at = Align(at, Alignment);
            offsets[entry] = at;
            at += entry.Data.Length;
        }

        byte[] output = new byte[at];
        Span<byte> span = output;

        BinaryPrimitives.WriteUInt32BigEndian(span, Magic);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x04..], HeaderSize);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x08..], (uint)tableSize);
        BinaryPrimitives.WriteUInt32BigEndian(span[0x0C..], (uint)dataStart);

        for (int i = 0; i < nodes.Count; i++)
        {
            Node n = nodes[i];
            Span<byte> node = span[(HeaderSize + i * NodeSize)..];
            BinaryPrimitives.WriteUInt32BigEndian(node, (n.Entry is null ? 0x01000000u : 0) | (uint)nameOffsets[i]);
            if (n.Entry is null)
            {
                BinaryPrimitives.WriteUInt32BigEndian(node[0x04..], (uint)Math.Max(n.Parent, 0));
                BinaryPrimitives.WriteUInt32BigEndian(node[0x08..], (uint)n.End);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(node[0x04..], (uint)offsets[n.Entry]);
                BinaryPrimitives.WriteUInt32BigEndian(node[0x08..], (uint)n.Entry.Data.Length);
                n.Entry.Data.CopyTo(span[offsets[n.Entry]..]);
            }
        }

        names.ToArray().CopyTo(span[(HeaderSize + nodes.Count * NodeSize)..]);
        return output;
    }

    private static int Align(int value, int to) => (value + to - 1) / to * to;

    private sealed class Node(string name, U8Entry? entry, int parent)
    {
        public string Name { get; } = name;
        public U8Entry? Entry { get; } = entry;
        public int Parent { get; } = parent;
        public int End { get; set; }
    }

    private sealed class Dir(string name)
    {
        private readonly List<(string Name, U8Entry Entry)> _files = [];
        private readonly Dictionary<string, Dir> _dirs = new(StringComparer.Ordinal);

        public string Name { get; } = name;

        public void Add(U8Entry entry)
        {
            string[] parts = entry.Path.Split('/');
            Dir dir = this;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!dir._dirs.TryGetValue(parts[i], out Dir? child))
                    dir._dirs[parts[i]] = child = new Dir(parts[i]);
                dir = child;
            }
            dir._files.Add((parts[^1], entry));
        }

        public void Flatten(List<Node> nodes, int parent)
        {
            Node self = new(Name, null, parent);
            int index = nodes.Count;
            nodes.Add(self);

            foreach ((string name, U8Entry entry) in _files.OrderBy(f => f.Name, PathComparer.Instance))
                nodes.Add(new Node(name, entry, index));
            foreach (Dir dir in _dirs.Values.OrderBy(d => d.Name, PathComparer.Instance))
                dir.Flatten(nodes, index);

            self.End = nodes.Count;
        }
    }

    private sealed class PathComparer : IComparer<string>
    {
        public static readonly PathComparer Instance = new();

        public int Compare(string? x, string? y) => string.CompareOrdinal(x?.ToUpperInvariant(), y?.ToUpperInvariant());
    }
}
