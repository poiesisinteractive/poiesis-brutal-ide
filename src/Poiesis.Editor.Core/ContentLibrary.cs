using System.Text.Json.Nodes;
using Poiesis.Engine.Scene;

namespace Poiesis.Editor;

public enum ContentKind { Folder, Scene, Data, Other }

/// <summary>Un fichier ou un dossier de <c>content/</c>, avec ses enfants pour un dossier.</summary>
public sealed record ContentEntry(string Path, string Name, ContentKind Kind, IReadOnlyList<ContentEntry> Children);

/// <summary>
/// Les opérations sur les fichiers de contenu du projet, pour le navigateur de contenu. Les chemins sont relatifs à
/// <c>content/</c>, avec des <c>/</c>. Déplacer ou renommer une scène (ou un dossier) met à jour toutes les scènes qui
/// l'instancient, et la scène de départ du projet.
/// </summary>
/// <remarks>
/// Les données (vaisseaux, compositions) sont désignées par leur champ <c>id</c>, pas par leur nom de fichier : les
/// renommer ne casse aucune référence.
/// </remarks>
public sealed class ContentLibrary(ProjectConfig project)
{
    public string Root => project.ContentRoot;

    public string Abs(string relative) => Path.GetFullPath(Path.Combine(Root, relative));

    public string Rel(string absolute) => Path.GetRelativePath(Root, absolute).Replace('\\', '/');

    public static ContentKind KindOf(string path) =>
        path.EndsWith(".scene.json", StringComparison.Ordinal) ? ContentKind.Scene
        : path.EndsWith(".json", StringComparison.Ordinal) ? ContentKind.Data
        : ContentKind.Other;

    /// <summary>L'arborescence de <c>content/</c> : dossiers d'abord, puis fichiers, par ordre alphabétique.</summary>
    public ContentEntry Tree()
    {
        Directory.CreateDirectory(Root);
        return Entry(Root);
    }

    ContentEntry Entry(string dir)
    {
        var children = new List<ContentEntry>();
        foreach (var d in Directory.EnumerateDirectories(dir).Where(d => !Path.GetFileName(d).StartsWith('.')).Order(StringComparer.Ordinal))
            children.Add(Entry(d));
        foreach (var f in Directory.EnumerateFiles(dir).Where(f => !Path.GetFileName(f).StartsWith('.')).Order(StringComparer.Ordinal))
            children.Add(new ContentEntry(Rel(f), Path.GetFileName(f), KindOf(f), []));
        var rel = dir == Root ? "" : Rel(dir);
        return new ContentEntry(rel, dir == Root ? "content" : Path.GetFileName(dir), ContentKind.Folder, children);
    }

    IEnumerable<string> SceneFiles() =>
        Directory.Exists(Root) ? Directory.EnumerateFiles(Root, "*.scene.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal) : [];

    static bool Covers(string target, string reference) =>
        reference == target || reference.StartsWith(target + "/", StringComparison.Ordinal);

    /// <summary>Les scènes qui instancient <paramref name="path"/> (ou une scène de ce dossier).</summary>
    public IReadOnlyList<string> FindReferences(string path)
    {
        path = path.Replace('\\', '/').TrimEnd('/');
        var result = new List<string>();
        foreach (var file in SceneFiles())
        {
            var rel = Rel(file);
            if (Covers(path, rel))
                continue;   // une scène du dossier lui-même
            JsonNode? doc;
            try
            {
                doc = JsonNode.Parse(File.ReadAllText(file));
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }
            if (SceneSerializer.InstanceReferences(doc).Any(r => Covers(path, r)))
                result.Add(rel);
        }
        return result;
    }

    /// <summary>
    /// Déplace ou renomme un fichier ou un dossier, puis réécrit les références : champs <c>instance</c> des scènes et
    /// scène de départ du projet. Rend les fichiers réécrits (relatifs à <c>content/</c>, ou au projet pour sa config).
    /// </summary>
    public IReadOnlyList<string> Move(string from, string to)
    {
        from = from.Replace('\\', '/').TrimEnd('/');
        to = to.Replace('\\', '/').TrimEnd('/');
        if (from.Length == 0)
            throw new InvalidOperationException("On ne déplace pas content/ lui-même.");
        if (Covers(from, to))
            throw new InvalidOperationException($"On ne déplace pas {from} dans lui-même.");
        var src = Abs(from);
        var dst = Abs(to);
        if (File.Exists(dst) || Directory.Exists(dst))
            throw new IOException($"{to} existe déjà");
        bool isDir = Directory.Exists(src);
        if (!isDir && !File.Exists(src))
            throw new FileNotFoundException($"{from} n'existe pas", src);
        if (!isDir && KindOf(from) == ContentKind.Scene && KindOf(to) != ContentKind.Scene)
            throw new InvalidOperationException("Une scène garde l'extension .scene.json.");

        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (isDir)
            Directory.Move(src, dst);
        else
            File.Move(src, dst);

        var changed = new List<string>();
        foreach (var file in SceneFiles())
        {
            JsonNode? doc;
            try
            {
                doc = JsonNode.Parse(File.ReadAllText(file));
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }
            if (Rewrite(doc, from, to))
            {
                File.WriteAllText(file, SceneSerializer.ToJsonString(doc!) + "\n");
                changed.Add(Rel(file));
            }
        }

        // La scène de départ du projet (chemin relatif au projet : « content/… »).
        if (project.StartScene is { } start)
        {
            var startRel = Path.GetRelativePath(Root, project.Abs(start)).Replace('\\', '/');
            if (Covers(from, startRel))
            {
                var updated = "content/" + to + startRel[from.Length..];
                var configPath = Path.Combine(project.Root, "poiesis.project.json");
                var config = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
                config["startScene"] = updated;
                File.WriteAllText(configPath, config.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }) + "\n");
                project.StartScene = updated;
                changed.Add("../poiesis.project.json");
            }
        }
        return changed;
    }

    /// <summary>Remplace le préfixe <paramref name="from"/> par <paramref name="to"/> dans un chemin qui en dépend.</summary>
    public static string? Remap(string reference, string from, string to) =>
        Covers(from, reference) ? to + reference[from.Length..] : null;

    static bool Rewrite(JsonNode? node, string from, string to)
    {
        bool changed = false;
        if (node is JsonObject obj)
        {
            if (obj["instance"] is JsonValue v && v.TryGetValue<string>(out var path) && Remap(path, from, to) is { } updated)
            {
                obj["instance"] = updated;
                changed = true;
            }
            if (obj["children"] is JsonArray children)
                foreach (var child in children)
                    changed |= Rewrite(child, from, to);
        }
        return changed;
    }

    public string CreateFolder(string parent, string name)
    {
        var rel = Join(parent, name);
        if (Directory.Exists(Abs(rel)) || File.Exists(Abs(rel)))
            throw new IOException($"{rel} existe déjà");
        Directory.CreateDirectory(Abs(rel));
        return rel;
    }

    /// <summary>Crée une scène vide dont la racine est <paramref name="rootJson"/> (un nœud déjà écrit par le sérialiseur).</summary>
    public string CreateScene(string parent, string name, string rootJson)
    {
        if (!name.EndsWith(".scene.json", StringComparison.Ordinal))
            name += ".scene.json";
        var rel = Join(parent, name);
        if (File.Exists(Abs(rel)))
            throw new IOException($"{rel} existe déjà");
        Directory.CreateDirectory(Path.GetDirectoryName(Abs(rel))!);
        File.WriteAllText(Abs(rel), rootJson + "\n");
        return rel;
    }

    /// <summary>Copie un fichier à côté de lui (« nom (copie).scene.json »). Une scène copiée reçoit de nouveaux identifiants.</summary>
    public string Duplicate(string path)
    {
        var src = Abs(path);
        if (!File.Exists(src))
            throw new FileNotFoundException($"{path} n'existe pas (seuls les fichiers se dupliquent)", src);
        var dir = Path.GetDirectoryName(path.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
        var file = Path.GetFileName(path);
        // Extension double (« .scene.json », « .fleet.json ») : le type de contenu reste reconnaissable.
        var parts = file.Split('.');
        var ext = parts.Length >= 3 ? "." + parts[^2] + "." + parts[^1] : Path.GetExtension(file);
        var stem = file[..^ext.Length];
        string rel;
        int n = 1;
        do
            rel = Join(dir, n == 1 ? $"{stem}-copie{ext}" : $"{stem}-copie{n}{ext}");
        while (File.Exists(Abs(rel)) && n++ < 1000);

        if (KindOf(path) == ContentKind.Scene && JsonNode.Parse(File.ReadAllText(src)) is JsonObject scene)
        {
            SceneSerializer.RegenerateIds(scene);
            File.WriteAllText(Abs(rel), SceneSerializer.ToJsonString(scene) + "\n");
        }
        else
        {
            File.Copy(src, Abs(rel));
        }
        return rel;
    }

    public void Delete(string path)
    {
        if (path.Replace('\\', '/').Trim('/').Length == 0)
            throw new InvalidOperationException("On ne supprime pas content/ lui-même.");
        var abs = Abs(path);
        if (Directory.Exists(abs))
            Directory.Delete(abs, recursive: true);
        else
            File.Delete(abs);
    }

    static string Join(string parent, string name)
    {
        if (name.Contains('/') || name.Contains('\\') || name is "" or "." or "..")
            throw new ArgumentException($"Nom invalide : « {name} »");
        parent = parent.Replace('\\', '/').Trim('/');
        return parent.Length == 0 ? name : parent + "/" + name;
    }
}
