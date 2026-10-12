using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.GatewayAdmin.Tests;

/// <summary>An in-memory disk with ACLs, owners and reparse points: no test touches Program Files or any real user folder.</summary>
internal sealed class FakeFs : IAdminFileSystem
{
    public sealed class Item
    {
        public bool IsDirectory { get; set; }
        public bool IsReparse { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public string Owner { get; set; } = AdminAcl.Administrators;
        public bool HasDacl { get; set; } = true;
        public bool SecurityUnreadable { get; set; }
        public List<AclEntry> Acl { get; set; } = AdminOnly();
        public DateTime LastWrite { get; set; } = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    }

    public Dictionary<string, Item> Items { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every path the code asked to change, in order: "ensure", "temp", "replace", "delete", "write".</summary>
    public List<string> Changes { get; } = new();

    /// <summary>Runs after a file is opened for reading (the moment a source could be changed under the helper).</summary>
    public Action<string>? AfterOpen { get; set; }

    /// <summary>Flips a byte of every temporary file as it is closed (a copy that went wrong).</summary>
    public bool CorruptTemps { get; set; }

    public int Opens { get; private set; }

    private int _tick;

    public static List<AclEntry> AdminOnly() => new()
    {
        new AclEntry(AdminAcl.System, true, 0x1F01FF),
        new AclEntry(AdminAcl.Administrators, true, 0x1F01FF),
        new AclEntry(AdminAcl.Users, true, AdminAcl.ReadExecuteMask),
    };

    public Item AddDir(string path, string? owner = null, List<AclEntry>? acl = null)
    {
        var item = new Item { IsDirectory = true, Owner = owner ?? AdminAcl.Administrators, Acl = acl ?? AdminOnly() };
        Items[Path.TrimEndingDirectorySeparator(path)] = item;
        return item;
    }

    public Item AddFile(string path, byte[] data, string? owner = null, List<AclEntry>? acl = null)
    {
        // Each write gets a new time, as a real file system gives, because the inspector's hash cache is keyed on it.
        var item = new Item { Data = data, Owner = owner ?? AdminAcl.Administrators, Acl = acl ?? AdminOnly(), LastWrite = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).AddSeconds(++_tick) };
        Items[path] = item;
        return item;
    }

    public FsNode Stat(string path)
    {
        if (!Items.TryGetValue(Path.TrimEndingDirectorySeparator(path), out var item))
        {
            return FsNode.Missing(path);
        }

        NodeSecurity? security = item.IsReparse || item.SecurityUnreadable ? null : new NodeSecurity(item.Owner, item.HasDacl, item.Acl.ToArray());
        return new FsNode(path, true, item.IsDirectory, item.IsReparse, item.Data.LongLength, item.LastWrite, security);
    }

    public Stream OpenReadExclusive(string path)
    {
        if (!Items.TryGetValue(path, out var item) || item.IsDirectory)
        {
            throw new FileNotFoundException(path);
        }

        if (item.IsReparse)
        {
            throw new IOException("The file that was opened is not the file that was asked for.");
        }

        Opens++;
        var stream = new MemoryStream(item.Data.ToArray(), writable: false);
        AfterOpen?.Invoke(path);
        return stream;
    }

    public void EnsureAdminFolder(string path)
    {
        Changes.Add("ensure:" + path);
        var missing = new Stack<string>();
        for (var current = Path.TrimEndingDirectorySeparator(path); !Items.ContainsKey(current); current = Path.GetDirectoryName(current)!)
        {
            missing.Push(current);
        }

        while (missing.Count > 0)
        {
            AddDir(missing.Pop());
        }
    }

    public Stream CreateTemp(string folder, out string tempPath)
    {
        tempPath = Path.Combine(folder, ".tmp-" + (Changes.Count + 1));
        Changes.Add("temp:" + tempPath);
        var captured = tempPath;
        return new TempStream(this, captured);
    }

    public void Replace(string tempPath, string finalPath)
    {
        Changes.Add("replace:" + finalPath);
        if (ReplaceFails.Contains(finalPath))
        {
            throw new IOException("in use");
        }

        var item = Items[tempPath];
        Items.Remove(tempPath);
        Items[finalPath] = item;
    }

    public HashSet<string> ReplaceFails { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void DeleteFile(string path)
    {
        Changes.Add("delete:" + path);
        Items.Remove(path);
    }

    public void WriteText(string path, string text)
    {
        Changes.Add("write:" + path);
        AddFile(path, System.Text.Encoding.UTF8.GetBytes(text));
    }

    public string? ReadText(string path, int maxBytes)
    {
        if (!Items.TryGetValue(path, out var item) || item.IsDirectory || item.Data.Length > maxBytes)
        {
            return null;
        }

        return System.Text.Encoding.UTF8.GetString(item.Data);
    }

    public string Text(string path) => System.Text.Encoding.UTF8.GetString(Items[path].Data);

    private sealed class TempStream(FakeFs fs, string path) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var data = ToArray();
                if (fs.CorruptTemps && data.Length > 0)
                {
                    data[0] ^= 0xFF;
                }

                fs.AddFile(path, data);
            }

            base.Dispose(disposing);
        }
    }
}
