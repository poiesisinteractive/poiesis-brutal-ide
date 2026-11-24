namespace Poiesis.Editor.Api;

/// <summary>
/// L'API d'extension résumée pour l'agent IA (contexte des features « outil d'éditeur »). À tenir à jour avec l'API :
/// c'est ce que l'agent sait d'elle avant de lire le code.
/// </summary>
public static class EditorApiReference
{
    public const string Summary = """
        Outils d'éditeur (API d'extension, assembly Poiesis.Editor.Api, déjà référencée par le jeu) :
        - Une classe publique de la DLL du jeu, avec un constructeur sans paramètre, implémente IEditorExtension :
          `public void Register(EditorRegistry editor)`. Elle est trouvée au chargement du module et rechargée à chaud avec lui.
          Rangez-la dans le dossier Editor/ du projet du jeu (namespace <Jeu>.Editor).
        - EditorRegistry :
          Panel(titre, Action<IEditorContext> draw, PanelPlacement placement = Side | Center | Bottom) : un panneau ancrable ;
          Inspector<TNode>(titre, Action<TNode, IEditorContext>) : une section sous les propriétés d'un type de nœud ;
          Gizmo<TNode>(Action<TNode, IGizmoContext>) : dessins et poignées dans le viewport pour le nœud sélectionné ;
          MenuItem("Menu/Commande", Action<IEditorContext>) : une commande du menu Outils ;
          Thumbnail("*.ext.json", Func<string chemin, IEditorContext, World?>) : la vignette d'un type de fichier de contenu.
        - IEditorContext : Scene (racine), Selection, ContentRoot, Preview (monde d'aperçu cuit, ressources du jeu dedans),
          Game (monde de la partie, ou null) ; modifications annulables : Set(nœud, "Propriété", valeur), Rename, Add, Remove,
          Group(libellé, Action) pour n'en faire qu'une commande ; PropertyField(nœud, "Propriété") pour le widget standard ;
          View3D(id, World, float2 taille, DrawContext? overlay) pour un aperçu 3D ; RequestReload() après avoir écrit des
          données que le jeu lit au chargement ; GetSetting/SetSetting (réglages texte persistants) ; Log, Warn.
        - IGizmoContext : Draw (DrawContext : Line, Circle, Label en coordonnées du monde) et
          Handle(id, position, couleur, out float3 glissée) : vrai pendant le glissement ; appelez alors Set (une seule
          commande au relâchement).
        - On dessine avec Dear ImGui (using Brutal.ImGuiApi; ImGui.Text, ImGui.Button, ImGui.BeginTable…). Les chaînes
          littérales passées à ImGui s'écrivent en UTF-8 : "texte"u8 (l'analyseur BRUTALCORE0011 refuse sinon) ; les chaînes
          interpolées $"..." vont telles quelles.
        - Règles : ne jamais toucher src/Poiesis.* ; rien de statique qui survive au rechargement (événement, thread, cache
          global) ; toute modification de la scène passe par le contexte ; une exception désactive l'extension jusqu'au
          prochain rechargement. Testez la logique (calculs, données) dans le projet de tests du jeu, sans fenêtre.
        """;
}
