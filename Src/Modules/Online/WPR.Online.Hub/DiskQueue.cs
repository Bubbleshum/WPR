using System.Text.Json;

namespace WPR.Online.Hub;

/// <summary>
/// A folder of JSON files, one per queued item.
/// </summary>
/// <remarks>
/// <para><b>One file per item is what makes this safe across processes without a lock.</b> On
/// Android the <c>:game</c> process enqueues while the launcher drains, and a single queue file
/// would need a cross-process lock that external storage does not reliably honour. Here a writer
/// only ever creates a new file (written to a temp name, then moved into place, so a reader never
/// sees half of one) and a reader only ever deletes the file it just sent. The worst race is two
/// flushes sending the same item, which the hub treats as a duplicate.</para>
///
/// <para>Names start with a sortable timestamp so items drain oldest first.</para>
/// </remarks>
internal sealed class DiskQueue<T> where T : class
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _directory;
    private readonly int _capacity;

    /// <param name="capacity">Oldest items are dropped past this many, so a game crashing in a loop
    /// with nobody to send to cannot fill the disk.</param>
    public DiskQueue(string directory, int capacity)
    {
        _directory = directory;
        _capacity = capacity;
    }

    public string Enqueue(T item, string? prefix = null)
    {
        Directory.CreateDirectory(_directory);
        string name = $"{prefix}{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json";
        string path = Path.Combine(_directory, name);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(item, Json));
        File.Move(tmp, path, overwrite: true);

        Trim(prefix);
        return path;
    }

    /// <summary>Oldest first. Unreadable files are deleted rather than retried for ever.</summary>
    public List<(string Path, T Item)> ReadAll(string? prefix = null)
    {
        List<(string, T)> items = new();
        foreach (string path in Files(prefix))
        {
            try
            {
                T? item = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
                if (item is null) { Delete(path); continue; }
                items.Add((path, item));
            }
            catch (JsonException)
            {
                Delete(path);
            }
            catch (IOException)
            {
                // Being written or deleted by the other process right now: next flush.
            }
        }
        return items;
    }

    public void Clear()
    {
        foreach (string path in Files(null)) Delete(path);
    }

    public static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Trim(string? prefix)
    {
        List<string> files = Files(prefix);
        for (int i = 0; i < files.Count - _capacity; i++) Delete(files[i]);
    }

    private List<string> Files(string? prefix)
    {
        if (!Directory.Exists(_directory)) return new List<string>();
        List<string> files = Directory.GetFiles(_directory, (prefix ?? "") + "*.json").ToList();
        files.Sort(StringComparer.Ordinal);
        return files;
    }
}
