using Brutal.Numerics;
using Poiesis.Engine.Debug;
using Poiesis.Engine.Ecs;
using Poiesis.Engine.Scene;

namespace Poiesis.Editor.Api;

/// <summary>
/// Une extension de l'éditeur, fournie par le jeu : toute classe concrète de la DLL du jeu qui implémente cette
/// interface (avec un constructeur sans paramètre) est créée au chargement du module, et appelée pour s'enregistrer.
/// </summary>
/// <remarks>
/// <para>
/// Les extensions sont rechargées à chaud avec la DLL : l'éditeur oublie tout ce qu'elles ont enregistré avant de
/// décharger le module. Elles ne doivent donc rien laisser derrière elles (événement statique, thread, cache global).
/// </para>
/// <para>
/// On dessine avec Dear ImGui (<c>Brutal.ImGuiApi.ImGui</c>), comme l'éditeur. Toute modification de la scène passe par
/// <see cref="IEditorContext"/>, pour être annulable. Une extension qui lève une exception est désactivée jusqu'au
/// prochain rechargement, et l'erreur s'affiche dans la console.
/// </para>
/// </remarks>
public interface IEditorExtension
{
    void Register(EditorRegistry editor);
}

/// <summary>Ce qu'une extension ajoute à l'éditeur.</summary>
public sealed class EditorRegistry
{
    readonly List<PanelExtension> _panels = new();
    readonly List<InspectorExtension> _inspectors = new();
    readonly List<GizmoExtension> _gizmos = new();
    readonly List<MenuExtension> _menus = new();

    public IReadOnlyList<PanelExtension> Panels => _panels;
    public IReadOnlyList<InspectorExtension> Inspectors => _inspectors;
    public IReadOnlyList<GizmoExtension> Gizmos => _gizmos;
    public IReadOnlyList<MenuExtension> Menus => _menus;

    /// <summary>Un panneau ancrable. <paramref name="draw"/> dessine son contenu, entre <c>Begin</c> et <c>End</c>.</summary>
    public void Panel(string title, Action<IEditorContext> draw) => _panels.Add(new PanelExtension(title, draw));

    /// <summary>Une section de l'inspecteur, sous les propriétés des nœuds de type <typeparamref name="TNode"/>.</summary>
    public void Inspector<TNode>(string title, Action<TNode, IEditorContext> draw) where TNode : Node =>
        _inspectors.Add(new InspectorExtension(typeof(TNode), title, (n, c) => draw((TNode)n, c)));

    /// <summary>
    /// Un gizmo dans le viewport, en mode édition, quand un nœud de type <typeparamref name="TNode"/> est sélectionné :
    /// dessins (lignes, cercles, étiquettes) et poignées qu'on fait glisser.
    /// </summary>
    public void Gizmo<TNode>(Action<TNode, IGizmoContext> draw) where TNode : Node =>
        _gizmos.Add(new GizmoExtension(typeof(TNode), (n, c) => draw((TNode)n, c)));

    /// <summary>Une commande du menu Outils. Le chemin est découpé par « / » en sous-menus (ex. « Orionum/Aligner les flottes »).</summary>
    public void MenuItem(string path, Action<IEditorContext> run) => _menus.Add(new MenuExtension(path, run));
}

public sealed record PanelExtension(string Title, Action<IEditorContext> Draw);

public sealed record InspectorExtension(Type NodeType, string Title, Action<Node, IEditorContext> Draw);

public sealed record GizmoExtension(Type NodeType, Action<Node, IGizmoContext> Draw);

public sealed record MenuExtension(string Path, Action<IEditorContext> Run);

/// <summary>L'éditeur, vu d'une extension.</summary>
public interface IEditorContext
{
    /// <summary>La racine de la scène ouverte.</summary>
    Node Scene { get; }

    Node? Selection { get; set; }

    /// <summary>Dossier des contenus du jeu.</summary>
    string ContentRoot { get; }

    /// <summary>Le monde d'aperçu (la scène cuite, sans sim), avec les ressources du jeu ; <c>null</c> si la cuisson a échoué.</summary>
    World? Preview { get; }

    /// <summary>Le monde de la partie en cours, ou <c>null</c> en mode édition.</summary>
    World? Game { get; }

    /// <summary>
    /// Change une propriété d'un nœud, de façon annulable. Pendant le glissement d'une poignée, les changements
    /// s'appliquent en direct et deviennent une seule commande au relâchement.
    /// </summary>
    void Set(Node node, string property, object? value);

    void Rename(Node node, string name);

    /// <summary>Ajoute un enfant (refusé dans un nœud qui vient d'une scène instanciée).</summary>
    void Add(Node parent, Node child);

    void Remove(Node node);

    /// <summary>Regroupe les modifications faites par <paramref name="edits"/> en une seule commande annulable.</summary>
    void Group(string label, Action edits);

    /// <summary>Le widget standard de l'inspecteur pour une propriété (avec surcharge d'instance et annulation).</summary>
    void PropertyField(Node node, string property);

    void Log(string message);

    void Warn(string message);
}

/// <summary>Le viewport, vu d'un gizmo.</summary>
public interface IGizmoContext : IEditorContext
{
    /// <summary>Lignes, cercles et étiquettes en coordonnées du monde, dessinés cette image.</summary>
    DrawContext Draw { get; }

    /// <summary>
    /// Une poignée à <paramref name="position"/>. Rend vrai pendant qu'on la fait glisser, avec
    /// <paramref name="dragged"/>, la position sous la souris dans le plan face à la caméra qui passe par la poignée.
    /// L'identifiant distingue les poignées d'un même gizmo.
    /// </summary>
    bool Handle(string id, float3 position, float4 color, out float3 dragged);
}
