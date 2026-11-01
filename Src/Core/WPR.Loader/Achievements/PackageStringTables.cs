using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using Mono.Cecil;

namespace WPR.Achievements
{
    /// <summary>Display text for one key found in the package's own string tables.</summary>
    public sealed record KeyText(string? Name, string? Description);

    /// <summary>
    /// The package's own string tables (the neutral and English <c>.resources</c> of its
    /// assemblies, and XML string files in its content), searched for entries named after an
    /// achievement key: <c>ACH_&lt;key&gt;_NAME</c>, <c>&lt;key&gt;_DESC</c>,
    /// <c>A_NAME_&lt;key&gt;</c> and the like. A name is taken only when exactly one entry claims
    /// it, so a table that does not describe achievements gives nothing rather than a guess.
    /// </summary>
    public sealed class PackageStringTables
    {
        private static readonly Regex NameRole = new(@"(^|[^A-Za-z])(NAME|TITLE|LABEL)([^A-Za-z]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex HowToRole = new(@"(^|[^A-Za-z])(HOW_?TO|UNACHIEVED|HINT)([^A-Za-z]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DescriptionRole = new(@"(^|[^A-Za-z])(DESC|DESCRIPTION|TEXT|DETAILS?)([^A-Za-z]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly List<KeyValuePair<string, string>> _entries = new();

        public int Count => _entries.Count;

        /// <summary>Reads every table the package carries. Never throws.</summary>
        public static PackageStringTables Read(IPackageFiles package, IEnumerable<(string Path, ModuleDefinition Module)> modules)
        {
            PackageStringTables tables = new();
            foreach ((string path, ModuleDefinition module) in modules)
            {
                // Neutral tables, and satellites of English only: a satellite's folder is its culture.
                string folder = path.Contains('/') ? path[..path.IndexOf('/')] : "";
                if (folder.Length > 0 && !folder.StartsWith("en", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (EmbeddedResource resource in module.Resources.OfType<EmbeddedResource>())
                {
                    if (!resource.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) continue;
                    tables.AddResources(resource);
                }
            }
            foreach (string path in package.Paths)
            {
                if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".resx", StringComparison.OrdinalIgnoreCase)) continue;
                if (path.Equals("WMAppManifest.xml", StringComparison.OrdinalIgnoreCase) || package.Length(path) > 4L * 1024 * 1024) continue;
                if (IsOtherCulture(path)) continue;
                tables.AddXml(package, path);
            }
            return tables;
        }

        /// <summary>For tests: a table from name/value pairs.</summary>
        public static PackageStringTables FromEntries(IEnumerable<KeyValuePair<string, string>> entries)
        {
            PackageStringTables tables = new();
            tables._entries.AddRange(entries);
            return tables;
        }

        /// <summary>The name and description entries naming <paramref name="key"/>, or null when there are none.</summary>
        public KeyText? Find(string key)
        {
            string token = key.Trim();
            // A short or numeric key would match unrelated entries.
            if (token.Length < 4 || token.All(char.IsDigit)) return null;
            Regex mention = new("(^|[^A-Za-z0-9])" + Regex.Escape(token) + "([^A-Za-z0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            List<KeyValuePair<string, string>> candidates = _entries
                .Where(e => mention.IsMatch(e.Key))
                .Where(e => !string.IsNullOrWhiteSpace(e.Value))
                .ToList();
            if (candidates.Count == 0) return null;

            // The entry names with the key removed say what each one is.
            string? One(Regex role) => candidates
                .Where(e => role.IsMatch(mention.Replace(e.Key, "_")))
                .Select(e => e.Value.Trim()).Distinct(StringComparer.Ordinal).ToList() is { Count: 1 } list ? list[0] : null;

            string? name = One(NameRole);
            string? description = One(HowToRole) ?? One(DescriptionRole);
            return name is null && description is null ? null : new KeyText(name, description);
        }

        private static bool IsOtherCulture(string path)
        {
            // A path segment that looks like a culture other than English (fr, de-DE, ja-JP ...).
            foreach (string segment in path.Split('/').SkipLast(1))
            {
                if (Regex.IsMatch(segment, "^[a-z]{2}(-[A-Za-z]{2,4})?$") && !segment.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private void AddResources(EmbeddedResource resource)
        {
            try
            {
                using Stream stream = new MemoryStream(resource.GetResourceData(), writable: false);
                using ResourceReader reader = new(stream);
                List<string> names = new();
                IDictionaryEnumerator e = reader.GetEnumerator();
                while (e.MoveNext()) names.Add((string)e.Key);
                foreach (string name in names)
                {
                    try
                    {
                        reader.GetResourceData(name, out string type, out byte[] data);
                        if (type != "ResourceTypeCode.String") continue;
                        using BinaryReader r = new(new MemoryStream(data), Encoding.UTF8);
                        _entries.Add(new(name, r.ReadString()));
                    }
                    catch (Exception)
                    {
                        // One unreadable entry is not the table.
                    }
                }
            }
            catch (Exception)
            {
                // Not a table this reader understands.
            }
        }

        private void AddXml(IPackageFiles package, string path)
        {
            try
            {
                XDocument doc = XDocument.Parse(Encoding.UTF8.GetString(package.ReadAll(path)).TrimStart('﻿'));
                foreach (XElement element in doc.Descendants())
                {
                    if (element.HasElements) continue;
                    string? id = (string?)element.Attribute("name") ?? (string?)element.Attribute("id") ?? (string?)element.Attribute("key");
                    string? value = element.Value;
                    if (string.IsNullOrWhiteSpace(value)) value = (string?)element.Attribute("value") ?? (string?)element.Attribute("text");
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrWhiteSpace(value)) _entries.Add(new(id, value!));
                }
            }
            catch (Exception)
            {
                // Not XML, or not one this reads.
            }
        }
    }
}
