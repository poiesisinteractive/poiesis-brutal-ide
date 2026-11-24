using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Poiesis.Editor;

/// <summary>
/// Ouvre un fichier à une ligne (ou un dossier) dans l'éditeur de code. Par défaut VS Code (<c>code --goto</c>) ;
/// réglable dans <c>&lt;projet&gt;/.poiesis/settings.json</c> :
/// <code>{ "codeEditor": { "command": "code", "goto": "--goto {file}:{line}:{column}", "open": "{folder}" } }</code>
/// </summary>
public sealed class CodeEditor(ProjectConfig project, LogBuffer log)
{
    public string Command { get; private set; } = "code";
    public string GotoArgs { get; private set; } = "--goto {file}:{line}:{column}";
    public string OpenArgs { get; private set; } = "{folder}";

    public static CodeEditor Load(ProjectConfig project, LogBuffer log)
    {
        var editor = new CodeEditor(project, log);
        var path = Path.Combine(project.Root, ".poiesis", "settings.json");
        if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["codeEditor"] is JsonObject o)
        {
            editor.Command = o["command"]?.GetValue<string>() ?? editor.Command;
            editor.GotoArgs = o["goto"]?.GetValue<string>() ?? editor.GotoArgs;
            editor.OpenArgs = o["open"]?.GetValue<string>() ?? editor.OpenArgs;
        }
        return editor;
    }

    public void Open(SourceLocation location)
    {
        var file = Path.IsPathRooted(location.File) ? location.File : project.Abs(location.File);
        Launch(Expand(GotoArgs, file, location.Line, location.Column), $"{Path.GetFileName(file)}:{location.Line}");
    }

    /// <summary>Ouvre le dossier du jeu (son projet C# et son contenu).</summary>
    public void OpenProject() => Launch(Expand(OpenArgs, project.Root, 1, 1), project.Root);

    static List<string> Expand(string template, string path, int line, int column) =>
        Split(template).Select(a => a
            .Replace("{file}", path).Replace("{folder}", path)
            .Replace("{line}", line.ToString()).Replace("{column}", column.ToString())).ToList();

    /// <summary>Découpe les arguments sur les espaces, sans casser les jetons ({file} contient des espaces sous Windows).</summary>
    static IEnumerable<string> Split(string template) => template.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    void Launch(List<string> args, string what)
    {
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                // « code » est un script code.cmd : il faut passer par cmd.exe pour le trouver dans le PATH.
                psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(Command);
            }
            else
            {
                psi = new ProcessStartInfo(Command) { UseShellExecute = false };
            }
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var process = Process.Start(psi);
            log.Info("code", $"Ouvert dans {Command} : {what}");
        }
        catch (Exception ex)
        {
            log.Error("code", $"Impossible de lancer « {Command} » ({ex.Message}). Installez VS Code et sa commande « code », ou réglez .poiesis/settings.json.");
        }
    }
}
