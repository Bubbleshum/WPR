// WP8 API census: every ARM PE in every WP8 XAP in the library, every import it makes, and whether
// the runtime implements it. Plus the WinRT classes the images name. See the runtime convergence plan.
//
//   wp8census <out-dir> <library-root> [<library-root> ...]
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using WPR.Wp8Native;

string outDir = args[0];
Directory.CreateDirectory(outDir);
string[] roots = args.Skip(1).ToArray();

// A HostStubs to ask "is this implemented": constructed over any WP8 image, nothing runs.
string sample = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, @"WPR\AppData\e94059a2-135c-420e-8e60-bcda5fc3ec30\AngryBirdsRio.exe");
HostStubs.SandboxRoot = Path.Combine(Path.GetTempPath(), "wp8census-sandbox");
ArmEmulator emulator = new(PeImage.Load(sample), Path.GetDirectoryName(sample)!, collectBlockStats: false);
HostStubs stubs = emulator.Stubs;
var factories = (Dictionary<string, long>)typeof(WinRtRuntime).GetField("_factories", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(emulator.WinRt)!;

// System DLLs: provided by the phone, so the runtime must provide them.
static bool IsSystemDll(string dll) =>
    Regex.IsMatch(dll, @"^(api-ms-win-|ext-ms-win-|MSVCR110|MSVCP110|vccorlib110|kernel32|kernelbase|ntdll|ole32|oleaut32|user32|d3d11|dxgi|xaudio2|XAudio2|MMDevAPI|xinput|ws2_32|WS2_32|PhoneAudioSes|d3dcompiler|D3DCompiler|mfplat|MFPlat|mfreadwrite|ADVAPI32|advapi32|bcrypt|crypt32|WININET|wininet|winhttp|iphlpapi|IPHLPAPI|msxml|XmlLite|xmllite|shlwapi|SHLWAPI|combase|rpcrt4|RPCRT4|Windows\.|PhoneDirect|PhoneAppModelHost|MediaSessions|mf\.dll|Mf\.dll)", RegexOptions.IgnoreCase);

var importTitles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);   // dll!fn -> titles
var bundledCalls = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase); // bundled dll -> titles importing it
var classTitles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
var titleSummary = new List<string>();
var seenXaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
Regex className = new(@"^(Windows|Microsoft)\.[A-Z][A-Za-z0-9]*(\.[A-Z][A-Za-z0-9]*)+$");

foreach (string root in roots)
{
    foreach (string xap in Directory.EnumerateFiles(root, "*.xap", SearchOption.AllDirectories))
    {
        if (!seenXaps.Add(Path.GetFileName(xap)))
        {
            continue;
        }

        string title = Path.GetFileNameWithoutExtension(xap);
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(xap); } catch { continue; }
        using (zip)
        {
            var modules = new List<(string Name, PeImage Image, byte[] Raw)>();
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string lower = entry.FullName.ToLowerInvariant();
                if (!lower.EndsWith(".exe") && !lower.EndsWith(".dll") && !lower.EndsWith(".winmd")) continue;
                byte[] raw;
                using (var s = entry.Open()) using (var m = new MemoryStream()) { s.CopyTo(m); raw = m.ToArray(); }
                if (raw.Length < 0x200 || raw[0] != 'M' || raw[1] != 'Z') continue;
                int pe = BitConverter.ToInt32(raw, 0x3C);
                if (pe <= 0 || pe + 6 > raw.Length || BitConverter.ToUInt16(raw, pe + 4) != 0x1C4) continue;
                string tmp = Path.Combine(Path.GetTempPath(), "wp8census.bin");
                File.WriteAllBytes(tmp, raw);
                try { modules.Add((Path.GetFileName(entry.FullName), PeImage.Load(tmp), raw)); } catch { }
            }

            if (modules.Count == 0) continue;
            var bundledNames = new HashSet<string>(modules.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
            int system = 0, missing = 0, bundled = 0;

            foreach (var (name, image, raw) in modules)
            {
                foreach (ImportedFunction import in image.Imports)
                {
                    if (bundledNames.Contains(import.Dll) || !IsSystemDll(import.Dll))
                    {
                        bundled++;
                        (bundledCalls.TryGetValue(import.Dll, out var t) ? t : bundledCalls[import.Dll] = new()).Add(title);
                        continue;
                    }

                    system++;
                    string key = $"{import.Dll}!{import.Name}";
                    (importTitles.TryGetValue(key, out var titles) ? titles : importTitles[key] = new()).Add(title);
                    if (!stubs.IsImplemented(import.Dll, import.Name)) missing++;
                }

                // WinRT class names: UTF-16 literals in the image.
                string text = Encoding.Unicode.GetString(raw);
                foreach (Match m in Regex.Matches(text, @"(Windows|Microsoft)\.[A-Za-z0-9.]{3,120}"))
                {
                    if (className.IsMatch(m.Value) && !m.Value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        (classTitles.TryGetValue(m.Value, out var t) ? t : classTitles[m.Value] = new()).Add(title);
                    }
                }
            }

            titleSummary.Add($"| {title} | {modules.Count} | {string.Join(", ", modules.Select(m => m.Name).Take(6))} | {system} | {missing} | {bundled} |");
        }
    }
}

bool Implemented(string key) => stubs.IsImplemented(key[..key.IndexOf('!')], key[(key.IndexOf('!') + 1)..]);

var sb = new StringBuilder();
sb.AppendLine("# WP8 API census");
sb.AppendLine();
sb.AppendLine($"{titleSummary.Count} titles with ARM code. System imports: {importTitles.Count} distinct, {importTitles.Keys.Count(Implemented)} implemented, {importTitles.Keys.Count(k => !Implemented(k))} not.");
sb.AppendLine();
sb.AppendLine("## Per title");
sb.AppendLine("| title | ARM modules | modules | system import refs | of which unimplemented | refs into bundled/other DLLs |");
sb.AppendLine("|---|---|---|---|---|---|");
titleSummary.ForEach(l => sb.AppendLine(l));
sb.AppendLine();
sb.AppendLine("## Unimplemented system imports, by titles");
sb.AppendLine("| titles | dll | function |");
sb.AppendLine("|---|---|---|");
foreach (var (key, titles) in importTitles.Where(p => !Implemented(p.Key)).OrderByDescending(p => p.Value.Count).ThenBy(p => p.Key))
{
    int bang = key.IndexOf('!');
    sb.AppendLine($"| {titles.Count} | {key[..bang]} | `{key[(bang + 1)..]}` |");
}

sb.AppendLine();
sb.AppendLine("## Unimplemented system imports, by DLL");
foreach (var group in importTitles.Where(p => !Implemented(p.Key)).GroupBy(p => p.Key[..p.Key.IndexOf('!')], StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()))
{
    sb.AppendLine($"- {group.Key}: {group.Count()} functions");
}

sb.AppendLine();
sb.AppendLine("## Imports into non-system DLLs (need a module loader)");
sb.AppendLine("| titles | dll |");
sb.AppendLine("|---|---|");
foreach (var (dll, titles) in bundledCalls.OrderByDescending(p => p.Value.Count))
{
    sb.AppendLine($"| {titles.Count} | {dll} |");
}

sb.AppendLine();
sb.AppendLine("## WinRT classes named in the images");
sb.AppendLine("| titles | class | factory registered |");
sb.AppendLine("|---|---|---|");
foreach (var (cls, titles) in classTitles.Where(p => p.Value.Count >= 1).OrderByDescending(p => p.Value.Count).Take(400))
{
    sb.AppendLine($"| {titles.Count} | {cls} | {(factories.ContainsKey(cls) ? "yes" : "")} |");
}

File.WriteAllText(Path.Combine(outDir, "wp8-census.md"), sb.ToString());
File.WriteAllLines(Path.Combine(outDir, "all-imports.tsv"),
    importTitles.OrderByDescending(p => p.Value.Count)
        .Select(p => $"{p.Value.Count}	{(Implemented(p.Key) ? "yes" : "no")}	{p.Key}"));
File.WriteAllLines(Path.Combine(outDir, "unimplemented.tsv"),
    importTitles.Where(p => !Implemented(p.Key)).OrderByDescending(p => p.Value.Count)
        .Select(p => $"{p.Value.Count}\t{p.Key}\t{string.Join(";", p.Value.OrderBy(x => x))}"));
Console.WriteLine($"{titleSummary.Count} titles; {importTitles.Count} system imports, {importTitles.Keys.Count(k => !Implemented(k))} unimplemented; {bundledCalls.Count} non-system DLLs; {classTitles.Count} WinRT class names");
