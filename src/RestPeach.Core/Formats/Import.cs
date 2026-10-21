using System.Text.Json.Nodes;
using RestPeach.Core.Model;
using RestPeach.Core.Storage;
using RestPeach.Core.Text;

namespace RestPeach.Core.Formats;

/// <summary>A folder of an imported collection, before it is written to disk.</summary>
public sealed class ImportedFolder
{
    public string Name { get; set; } = "";
    public FolderConfig Config { get; set; } = new();
    public List<ImportedFolder> Folders { get; } = new();
    public List<ApiRequest> Requests { get; } = new();

    public int CountRequests() => Requests.Count + Folders.Sum(f => f.CountRequests());
}

/// <summary>What an importer understood: a tree of requests, environments to merge, and what it could not keep.</summary>
public sealed class ImportResult
{
    /// <summary>"Postman 2.1", "Insomnia 4", "OpenAPI 3.1", "cURL".</summary>
    public string Format { get; set; } = "";
    public ImportedFolder Root { get; set; } = new();
    public List<ApiEnvironment> Environments { get; } = new();
    /// <summary>Things that were dropped or approximated (scripts, unsupported auth…), shown after importing.</summary>
    public List<string> Warnings { get; } = new();
}

public sealed record ImportSummary(string FolderRel, int Requests, int Folders, int EnvironmentsCreated, int EnvironmentsMerged);

/// <summary>Detects the format of a file (or pasted text) and runs the matching importer.</summary>
public static class Importer
{
    public static ImportResult Import(string text, string fileName = "")
    {
        text = text.TrimStart('﻿');
        if (Curl.LooksLikeCurl(text))
        {
            var req = Curl.Parse(text) ?? throw new InvalidDataException("cURL: no URL found.");
            var single = new ImportResult { Format = "cURL", Root = { Name = Path.GetFileNameWithoutExtension(fileName) is { Length: > 0 } n ? n : "cURL" } };
            single.Root.Requests.Add(req);
            return single;
        }

        JsonNode? doc;
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            doc = CanonicalJson.Parse(text);
        else
            doc = Yaml.Parse(text);
        if (doc is not JsonObject o) throw new InvalidDataException("Unrecognized file: expected a JSON or YAML document.");

        if (o.Obj("info")?.Str("schema") is { } schema && schema.Contains("getpostman.com", StringComparison.OrdinalIgnoreCase))
            return PostmanImporter.Import(o);
        if (o.Str("_postman_variable_scope") is "environment" or "globals" && PostmanImporter.ReadEnvironment(o) is { } env)
        {
            var envOnly = new ImportResult { Format = "Postman environment" };
            envOnly.Environments.Add(env);
            return envOnly;
        }
        if (o.Str("_type") == "export" || o.ContainsKey("__export_format"))
            return InsomniaImporter.ImportV4(o);
        if (o.Str("type").StartsWith("collection.insomnia.rest", StringComparison.OrdinalIgnoreCase))
            return InsomniaImporter.ImportV5(o);
        if (o.Str("openapi").StartsWith('3'))
            return OpenApiImporter.Import(o);
        if (o.ContainsKey("swagger"))
            throw new InvalidDataException("Swagger 2.0 is not supported: convert it to OpenAPI 3 first.");
        if (o.ContainsKey("item") && o.ContainsKey("info"))
            return PostmanImporter.Import(o);
        throw new InvalidDataException("Unrecognized format. Supported: Postman 2.1, Insomnia 4/5, OpenAPI 3.x, cURL.");
    }

    /// <summary>
    /// Writes <paramref name="result"/> under <paramref name="parentRel"/> as a new folder named after the collection
    /// (never overwriting anything). Environments that already exist only receive the variables they lack.
    /// </summary>
    public static ImportSummary Write(Workspace ws, ImportResult result, string parentRel)
    {
        int requests = 0, folders = 0, created = 0, merged = 0;
        string WriteFolder(ImportedFolder f, string parent)
        {
            var rel = ws.CreateFolder(parent, f.Name.Length > 0 ? f.Name : "Import");
            folders++;
            if (!f.Config.IsEmpty) ws.SaveFolder(rel, f.Config);
            foreach (var r in f.Requests) { ws.CreateRequest(rel, r); requests++; }
            foreach (var sub in f.Folders) WriteFolder(sub, rel);
            return rel;
        }

        // A single request (cURL) goes straight into the target folder; an environment file creates no folder at all.
        string top;
        if (result.Root.CountRequests() == 0 && result.Root.Config.IsEmpty) top = parentRel;
        else if (result.Root.Folders.Count == 0 && result.Root.Requests.Count == 1 && result.Root.Config.IsEmpty)
        {
            ws.CreateRequest(parentRel, result.Root.Requests[0]);
            requests = 1;
            top = parentRel;
        }
        else top = WriteFolder(result.Root, parentRel);

        var existing = ws.ListEnvironments().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var env in result.Environments)
        {
            var stem = Workspace.SafeFileName(env.Stem.Length > 0 ? env.Stem : env.Name).ToLowerInvariant().Replace(' ', '-');
            if (existing.Contains(stem))
            {
                var current = ws.LoadEnvironment(stem);
                foreach (var (k, v) in env.Variables) current.Variables.TryAdd(k, v);
                foreach (var (k, v) in env.Secrets) current.Secrets.TryAdd(k, v);
                ws.SaveEnvironment(current);
                merged++;
            }
            else
            {
                env.Stem = stem;
                ws.SaveEnvironment(env);
                existing.Add(stem);
                created++;
            }
        }
        return new ImportSummary(top, requests, folders, created, merged);
    }

    /// <summary>Insomnia "{{ _.name }}" and Postman "{{name}}" both become "{{name}}".</summary>
    internal static string Vars(string s) =>
        string.IsNullOrEmpty(s) ? s : System.Text.RegularExpressions.Regex.Replace(s, @"\{\{\s*_\.([\w.\-]+)\s*\}\}", "{{$1}}");
}
