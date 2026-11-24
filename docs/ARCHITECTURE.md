# Architecture

## Vue d'ensemble

```text
                    ┌──────────────────────────── Poiesis.Editor (exe) ───────────────────────────┐
                    │ GLFW · Vulkan · Dear ImGui docking                                          │
                    │ Panneaux : Scène · Inspecteur · Viewport · Console · IA · Entités en direct │
                    └───────────────┬─────────────────────────────────────────┬───────────────────┘
                                    │                                         │
                     Poiesis.Editor.Core (sans UI)                     Poiesis.Render (Vulkan)
                     document, sélection, undo/redo,                   RenderFrame instancié,
                     Play/Stop, hot reload                             viewport hors écran
                       │                     │
         Poiesis.Engine.Hosting        Poiesis.AI
         GameModuleHost (ALC)          fournisseurs · boucle d'agent · outils
         PlaySession (pas fixe)        workflow feature (worktree, garde-fous, diff)
                       │
         Poiesis.Engine.Scene ─── nœuds, NodeTypeRegistry, SceneSerializer, ValueCodec, IGameModule
                       │
         Poiesis.Engine.Core ──── ECS (World, ComponentStore, Query), Schedule, FixedStepClock
                       │
                 BRUTAL (NuGet Ahwoo.Brutal.*)

  games/Orionum/src/Orionum.Game (DLL rechargée à chaud) ── implémente IGameModule, référence Poiesis.Engine.Scene
  Poiesis.Headless (exe) ── charge un module, joue une scène sans fenêtre, écrit des métriques JSON
```

Règle de dépendance : chaque couche ne connaît que celles du dessous. Le moteur ne sait rien de l'éditeur ; l'IA ne sait rien du moteur (elle manipule des fichiers et lance des commandes) ; le jeu ne connaît que `Poiesis.Engine.Scene` et `Poiesis.Engine.Core`.

## Nœuds et ECS

- Un **nœud** (`Node`) est du contenu édité : il a un `Id` stable, un nom, des enfants et des propriétés publiques. Les propriétés sont sérialisées et affichées dans l'inspecteur, sans attribut à écrire.
- Un type de nœud porte un nom stable (`[NodeType("orionum.fleet")]`), qui sert de discriminant dans les fichiers.
- Au **Play**, `SceneBaker` parcourt l'arbre et appelle `Node.Bake` : chaque nœud crée ses entités ECS (une flotte de 6 croiseurs crée 6 entités). Le monde est neuf à chaque Play ; la scène n'est jamais modifiée par la partie.
- Le **monde ECS** stocke chaque type de composant dans un sparse set trié par slot d'entité. L'itération suit donc l'ordre des slots, quel que soit l'ordre d'ajout, et la sim reste déterministe. `World.Clone()` copie les tableaux : c'est la base du snapshot de Play et des projections tactiques d'Orionum.
- Les **systèmes** (`ISystem`) s'exécutent dans l'ordre de la `Schedule`, à pas fixe (`FixedStepClock`, 20 Hz). Au-delà de 8 pas par image, l'horloge lâche le retard plutôt que de s'emballer.

## Module de jeu et hot reload

Un jeu est une assembly qui implémente `IGameModule` :

1. `Load` : enregistrer ses données (catalogue de vaisseaux, etc.) ;
2. `InitializeWorld` : installer les ressources du monde avant la cuisson ;
3. `BuildSchedule` : ajouter ses systèmes ;
4. `Unload` : tout lâcher.

`GameModuleHost` charge la DLL **depuis un flux** dans un `AssemblyLoadContext` collectible. Le fichier n'est donc pas verrouillé et le build suivant peut l'écraser. `Poiesis.Engine.*` et `Brutal.*` restent dans le contexte par défaut, partagés entre l'éditeur et le jeu.

Au rechargement, `EditorSession.HotReload` :

1. arrête la partie, sérialise la scène, et lâche la scène, la sélection et l'historique (ils retiennent des types de l'ancienne assembly) ;
2. décharge l'ancien module et vérifie par `WeakReference` qu'il a bien été collecté ;
3. charge le nouveau, relit la scène avec les nouveaux types et restaure la sélection par `Id`.

Quatre pièges, tous rencontrés ; les trois premiers sont couverts par `Hot_reload_keeps_the_edited_scene_and_unloads_the_old_assembly` :

- **System.Text.Json** garde les types qu'il sérialise par réflexion dans des caches statiques. Les valeurs des scènes et des données du jeu passent donc par `ValueCodec`, qui ne garde rien.
- **Les temporaires du JIT** : en Debug, et en Tier 0 même en Release, un cadre de pile garde ses temporaires vivants jusqu'à la fin de la méthode. Le code qui touche des objets du jeu est isolé dans des méthodes `NoInlining`, séparées de la boucle de GC.
- **Une `WeakReference` vers l'objet `Assembly`** suffit à le retenir. On observe l'`AssemblyLoadContext`, pas l'assembly.
- **Le déchargement prend du temps** : finaliseur du `LoaderAllocator`, puis ses poignées, sur plusieurs cycles de GC, d'autant plus que le tas est gros. Dans l'éditeur, dix passes ne suffisaient pas, et l'analyse du tas (`dotnet-dump`, `gcroot`) a montré qu'aucune racine ne retenait l'ancien contexte. `GameModuleHost` fait jusqu'à 40 passes, et l'éditeur revérifie trois secondes après chaque rechargement avant de signaler une fuite.

Règles pour le code du jeu, écrit à la main ou par l'IA : pas d'événement statique sur un type du moteur, pas de thread qui survit à `Unload`, pas de cache global.

## IA « feature d'abord »

```text
 dev : « Ajoute des boucliers à 6 faces aux croiseurs »
   │
   ▼
 FeatureSession ── FeatureWorkspace.CreateAsync ─▶ git worktree add -b feature/boucliers-a-6-faces
   │
   ├─▶ AgentLoop (IChatProvider au choix) ◀──▶ outils : list_files · read_file · write_file · build · test · run_headless
   │        (chemins confinés au worktree ; src/Poiesis.* et .git protégés)
   │
   ├─▶ garde-fous : build ─▶ test ─▶ (headless)
   │        échec ─▶ la sortie repart à l'agent, jusqu'à 3 fois
   │
   └─▶ FeatureProposal : diff (code + JSON) · état des garde-fous · transcription
            │
            ├─ Accepter ─▶ git merge --no-ff, worktree retiré ─▶ hot reload dans l'éditeur
            ├─ Refuser  ─▶ worktree retiré, branche gardée
            └─ Reprendre la main ─▶ le dev ouvre le worktree dans son IDE de code
```

- **Conversation neutre** (`ChatMessage`, `TextPart`, `ToolCallPart`, `ToolResultPart`, `OpaquePart`) traduite par chaque fournisseur. `OpaquePart` transporte ce qui doit revenir à l'identique au tour suivant, comme les blocs de réflexion signés d'Anthropic.
- **`AgentLoop`** vérifie la raison d'arrêt avant d'exécuter un outil : une réponse tronquée ou déclinée n'exécute rien. Tous les résultats d'un tour repartent dans un seul message.
- **Fournisseurs** : `AnthropicProvider` (SDK officiel `Anthropic`, endpoint beta pour le repli côté serveur) et `OpenAiCompatibleProvider` (OpenAI, Gemini, Ollama, LM Studio). Les réglages par défaut sont dans `ProviderSettings.Defaults`.

## Sim de masse (moteur)

- **`SpatialHash`** : grille uniforme reconstruite à chaque pas, sans allocation. Les candidats sont triés par cellule puis par indice : une requête rend toujours les mêmes candidats dans le même ordre.
- **`Jobs`** : boucles parallèles sur le `JobScheduler` de BRUTAL, en tranches fixes. Le découpage ne dépend que du nombre d'éléments, jamais du nombre de threads. Le délégué du jeu est lâché à la fin de chaque boucle, pour ne pas bloquer le hot reload.
- **Règle de déterminisme**, appliquée par Orionum :
  - une phase parallèle n'écrit que dans ses propres éléments ;
  - elle lit les autres entités dans une photo prise en début de pas ;
  - ses effets croisés (tirs, coups) sont rangés par tranche puis appliqués dans l'ordre des tranches ;
  - l'aléa est une fonction pure de (graine, pas, entité, sel).

  Résultat : même bataille au bit près sur 1 ou 4 threads (test `Same_seed_gives_the_same_battle_whatever_the_number_of_threads`).
- **Données hors ECS** : retirer une entité d'un stockage coûte O(n), parce que l'ordre est préservé. Les objets éphémères en masse (projectiles, effets) vivent donc dans des tables de ressources compactées une fois par pas. L'ECS garde les vaisseaux.
- **`ICloneableResource`** : les ressources qui changent pendant la partie sont copiées par `World.Clone()`. C'est ce qui rend les instantanés exacts.

## Débogage en jeu

- **`Schedule`** chronomètre chaque système (dernier pas, moyenne glissante, pic, historique de 240 pas). L'éditeur l'affiche dans le profileur, le runner headless dans son rapport JSON.
- **`PlaySession`** : `Run(n)` (pas à pas), `TakeSnapshot()` et `Restore()` (copie profonde du monde).
- **`IWorldVisualizer`** (facultatif, côté jeu) : le jeu dessine dans un `DrawContext` des traits, des étiquettes et des lignes de HUD. Ses aides de débogage sont rangées par canaux que l'éditeur allume ou éteint. Le viewport ajoute les traits à son image, et projette les étiquettes et le HUD en surimpression.
- **Inspecteur d'entités** : `IComponentStore.GetBoxed` et `SetBoxed`. L'éditeur modifie une copie boxée, structures imbriquées comprises, puis la réécrit.

## Joueur, export et analyse (J2b)

```text
Poiesis.Runtime ── ImGuiHost · EditorTheme · Splash · WorldView · ViewportTextures
   ▲        ▲
   │        └── Poiesis.Player (exe) : game.json → GameModuleHost → PlaySession → WorldView, HUD, crédits
   └─────────── Poiesis.Editor (exe) : ViewportPanel = WorldView + grille, gizmos, sélection
Poiesis.Editor.Core ── Exporter (dotnet publish du joueur, DLL du jeu, contenu, zip)
                    ── BuildService, TestService, CodeEditor (diagnostics et échecs → code --goto)
Poiesis.Engine.Hosting ── Series (N graines, IGameReporter) ◄── Poiesis.Headless --runs, panneau Analyse
```

- **Export** : le joueur est publié en autonome pour la plateforme visée, puis son exécutable prend le nom du jeu. L'hôte .NET garde le chemin de sa DLL, on peut donc le renommer ; renommer l'assembly aurait aussi renommé les projets référencés. La DLL du jeu est compilée à part, sans le moteur, fourni par le joueur. `game.json` dit quoi charger et jouer.
- **Obligations BRUTAL** dans le jeu exporté : logo au démarrage (splash), crédits (F1), `NOTICE.txt` livré. L'export échoue si `NOTICE.txt` manque.
- **Séries** : `Series.SetSeed` change toute propriété entière `Seed` de la scène (convention, comme `Position` et `Heading`). Chaque partie tourne jusqu'à `Finished` ou une durée maximale. `SeriesResult` agrège les victoires et les mesures, et `Summary()` les met en texte pour l'IA.
- **Analyse par l'IA** : une requête simple, sans outils, au fournisseur choisi dans le panneau IA. Elle contient les résultats, la question et toutes les données du jeu (`content/**/*.json` hors scènes), pour que les propositions désignent des fichiers et des champs. La réponse peut devenir une demande de feature, qui passe alors par le workflow habituel : branche, garde-fous, diff.

## Réutilisation et extensions (J3a)

### Scènes imbriquées

Un nœud peut être l'instance d'une autre scène. Dans le fichier, l'instance ne garde que le chemin de sa source et ce qui en diffère :

```json
{
  "instance": "fleets/coalition.line.scene.json",
  "id": "ee5661f2-…",
  "name": "Coalition",
  "overrides": { "<id du nœud dans la source>": { "name": "…", "props": { "position": [0, 0, -60000] } } }
}
```

- **Lecture** : `SceneSerializer` (qui connaît désormais `content/`) charge la source, applique les surcharges et marque la racine (`Node.InstanceOf`). Les autres nœuds reçoivent un identifiant dérivé de celui de l'instance et du leur dans la source (`InstanceNodeId`, MD5 des deux) : deux instances ne partagent aucun identifiant, et un nœud garde le sien d'un chargement à l'autre (sélection, entités, diffs).
- **Écriture** : on recharge la source et on compare, propriété par propriété, en JSON. Modifier la source modifie donc toutes les instances, sauf ce qu'elles surchargent. Une surcharge dont le nœud a disparu de la source est ignorée.
- **Structure verrouillée** : on ne change pas les enfants d'une instance (`Node.HasEditableChildren`), seulement leurs propriétés. « Rendre local » détache l'instance.
- **Boucles** : une scène qui s'instancierait elle-même, directement ou non, est refusée à la lecture, et l'éditeur refuse de l'instancier ou de la coller (`DependsOn`).
- **Cuisson** : une instance est un arbre de nœuds ordinaire une fois lue. Le moteur, le joueur et le headless n'ont rien de plus à faire.

### Contenu et presse-papiers

- **`ContentLibrary`** (Editor.Core) : arborescence de `content/`, création, duplication (avec de nouveaux identifiants pour une scène), suppression, déplacement. Un déplacement réécrit les champs `instance` de toutes les scènes et la scène de départ du projet. Les données sont désignées par leur `id`, pas par leur fichier : les renommer ne casse rien.
- **Copier-coller** : le presse-papiers du système reçoit `{"kind": "poiesis.nodes", "nodes": [...]}`. Coller régénère les identifiants (pas les clés de surcharge) et place les nœuds après la sélection, ou dans le premier ancêtre dont on peut changer les enfants.

### API d'extension

```text
Orionum.Game.dll ──▶ Poiesis.Editor.Api (IEditorExtension, EditorRegistry, IEditorContext, IGizmoContext)
                         ▲ partagée avec l'hôte (comme Poiesis.Engine.* et Brutal.*)
Poiesis.Editor : ExtensionHost ── trouve les IEditorExtension au chargement du module, les oublie avant le déchargement
```

- **Enregistrement** : `Panel(titre, draw)`, `Inspector<TNode>(titre, draw)`, `Gizmo<TNode>(draw)`, `MenuItem("Menu/Commande", run)`. On dessine avec Dear ImGui, comme l'éditeur.
- **Contexte** : scène, sélection, monde d'aperçu (avec les ressources du jeu) ou de la partie, et modifications annulables : `Set`, `Rename`, `Add`, `Remove`, `Group` (une commande pour plusieurs modifications), `PropertyField` (le widget de l'inspecteur).
- **Gizmos** : un `DrawContext` (lignes, cercles, étiquettes) et des poignées (`Handle`) qu'on fait glisser dans le plan face à la caméra. Pendant le glissement, `Set` s'applique en direct ; au relâchement, tout devient une seule commande. Les poignées passent avant la caméra et la sélection.
- **Rechargement à chaud** : `GameModuleHost.Unloading` vide le registre, les erreurs mémorisées et le glissement en cours, avant le déchargement. Aucune référence vers un délégué du jeu ne survit (vérifié : génération 2, ancienne DLL déchargée).
- **Erreurs** : une extension qui lève une exception est coupée jusqu'au prochain rechargement, et la console dit laquelle.
- **Livraison** : le jeu exporté référence l'API ; le joueur la livre, sans s'en servir. Les bibliothèques natives d'ImGui ne sont pas copiées à côté du jeu.

## Contenu 3D et outils de contenu (J3b)

```text
Poiesis.Engine.Core    MeshData · TextureData · MeshAsset · MeshLibrary (ressource du monde) · Renderable.Asset
Poiesis.Engine.Assets  ModelImporter (glTF → MeshData, meshoptimizer, PNG/JPEG/KTX2) · ModelExporter (.glb, KTX2 Basis)
Poiesis.Render         SceneRenderer : UV, une texture par maillage (blanche par défaut), ReleaseMesh différé, RenderToPixels
Poiesis.Runtime        WorldView : téléverse les maillages du jeu à la première utilisation, libère après 120 images sans usage
Poiesis.Editor         PreviewView (View3D des extensions) · ThumbnailService (vignettes) · ContentPanel en vignettes
Orionum                HullDesign · HullGenerator · Catalog.ShipMesh · HullDesigner (extension)
```

- **Maillages du jeu** : le jeu les fabrique (générateur) ou les importe (glTF), les range dans une `MeshLibrary` qu'il installe dans chaque monde, et les désigne par numéro dans `Renderable.Asset`. Le rendu ne connaît que des `MeshAsset`, sans type du jeu : rien ne retient l'assembly au rechargement. Un maillage régénéré est un nouvel objet, téléversé à son tour ; l'ancien est libéré quand plus personne ne le dessine, après les images en vol.
- **Forme et taille** : le maillage est ramené au cube unité en gardant son origine au centre (`NormalizeToUnitBox(keepOrigin)`), et `Renderable.Size` lui rend sa taille en mètres. L'origine du vaisseau reste celle du maillage, donc les tourelles, placées par rapport à elle, tombent sur la coque.
- **Import glTF** : la scène du fichier, nœuds et transformations compris, est fusionnée en un maillage ; meshoptimizer réordonne triangles et sommets ; la texture de couleur de base est décodée en RGBA. Une texture KTX2 intégrée au `.glb` est transcodée par libktx (le chargeur de Brutal.Texture ne transcode qu'à la lecture d'un fichier). Une seule texture par modèle pour l'instant.
- **Concepteur de coques** : `HullDesign` (champ `design` d'un `*.ship.json`, dimensions en fractions de la longueur) → `HullGenerator` : sections octogonales lissées en facettes, proue, superstructures, tuyères, quille, coordonnées de texture par projection sur la face, texture de blindage procédurale. `HullProfile` donne demi-largeur et demi-hauteur le long de l'axe, pour poser les tourelles (`SnapHardpoints`). Le panneau écrit `design` et les emplacements des tourelles dans le JSON, puis demande un rechargement (`RequestReload`), jamais pendant son propre code.
- **Aperçu 3D** (`IEditorContext.View3D`) : un second rendu hors écran, une caméra orbitale, un aperçu par image. Le monde fourni par l'extension n'est gardé que jusqu'au rendu de l'image.
- **Vignettes** : un troisième rendu, 192 × 192, relu (`RenderToPixels`) et enregistré en PNG dans `.poiesis/thumbnails/`, sous une clé faite du fichier, de sa date et de la génération du module. Les scènes sont cuites avec le module ; les modèles importés ; le reste est demandé au jeu (`EditorRegistry.Thumbnail("*.ship.json", …)`). Une vignette par image au plus.
- **IA et outils d'éditeur** : le contexte de l'agent contient `EditorApiReference.Summary` et la liste des extensions existantes ; un test vérifie que ce résumé cite chaque membre public de l'API. Le prompt système dit qu'un outil d'éditeur est une extension du jeu, jamais un changement de `src/Poiesis.*`.

## Édition avancée et IA sur la scène (J4a)

- **Sélection** : `EditorSession.SelectedNodes` (le principal en dernier, `Selection` reste le principal pour les panneaux qui n'en voient qu'un). `SelectedRoots()` écarte la racine, les nœuds d'instance et ceux dont un ancêtre est déjà sélectionné : c'est le groupe qu'on supprime, duplique ou coupe.
- **Transformations** (`SceneTransforms`, `TransformDrag`, Editor.Core) : par convention, `Position` (float3), `Heading` (degrés) et la propriété `[ScaleHandle]`. Un glissement garde les valeurs de départ, applique en direct, puis rend une seule commande arrondie (mètre, dixième de degré, centième). Rotation et échelle d'un groupe se font autour de la moyenne des positions. Aligner et répartir rendent aussi une seule commande.
- **Viewport** : modes W, E, R, repère local tiré du cap du nœud principal, magnétisme absolu sur la grille en repère global (le centre du groupe tombe sur une ligne), relatif en repère local ; rectangle de sélection sur les entités projetées ; caméras enregistrées par scène.
- **IA sur la scène** (`ScenePlan`) :

```text
panneau IA, onglet Scène ── AgentLoop(fournisseur, ScenePlan.Tools()) ── copie de la scène (Working)
   get_scene · list_node_types · list_scenes · add_node · instantiate_scene · set_property · rename_node · remove_node
        └─ chaque outil valide (type, bornes, références de contenu) et note une SceneOp
EditorSession.SetPlan ── cuisson de la copie ── viewport : fantômes (ajouts, nouvelles places), rouge (suppressions)
Appliquer ── ScenePlan.Apply(ops cochées) ── une CompositeCommand sur la vraie scène (opérations sautées signalées)
```

  L'agent n'a pas accès aux fichiers : seulement à la scène ouverte, par ces outils. La copie et la proposition tiennent des nœuds du jeu : elles sont oubliées au rechargement, et le rechargement attend la fin d'un agent en cours.

## Effets, son et HUD (J4b)

- **HDR et bloom** (`PostProcess`, Poiesis.Render) : `SceneRenderer` dessine dans une cible RGBA16F, puis `PostProcess.Apply` : préfiltre (seuil doux) vers le niveau 1, descentes successives (4 échantillons), remontées en tente ajoutées au niveau du dessus, enfin composition (scène + bloom × intensité, exposition, courbe douce) dans la cible RGBA8 qu'ImGui affiche. `RenderFrame.Post` règle bloom, seuil et exposition.
- **Halos** : `RenderFrame.Glow` → sprites instanciés face caméra (vecteurs droite et haut de la caméra dans les push constants), mélange additif, sans écriture de profondeur. Le jeu les demande par `DrawContext.Glow`, comme ses lignes.
- **Effets d'Orionum** : la sim garde les impacts (`ShieldHits` avec la direction dans le repère du vaisseau, `HullHits`) avec une durée de vie, vieillis à chaque pas ; `Effects.Draw` en fait des lignes et des halos, sans rien garder entre deux images.
- **Son** :

```text
sim (code série) ── SoundEvents.Play(cue, position, volume) ── anneau de 1024 événements (ressource du monde)
hôte (éditeur, joueur) ── AudioEngine.Update(monde, curseur, Listener) ── VoicePolicy (voix, intervalle) ── gain × panoramique
   └─ banques content/audio/*.cues.json ── Synthesizer.Render (ou fichier) ── MIX_Audio préparé une fois ── 32 pistes SDL_mixer
```

  `CueBank`, `Synthesizer` et `VoicePolicy` sont dans le moteur, sans SDL, donc testés seuls ; `Poiesis.Audio` ne fait que jouer. Le panneau Sons écrit le JSON puis recharge les banques.
- **HUD** : `IGameHud.DrawHud(World, HudContext)` ; le contexte donne le rectangle de la vue, la projection monde → écran, l'entité choisie (modifiable par le HUD), l'heure, la vitesse et `InEditor`. Le joueur s'en sert à la place de `DrawOverlay` ; l'éditeur le dessine pendant le Play.
- **Joueur** : écrans Menu, En jeu, Pause, Options, Fin ; les scénarios viennent de `content/scenarios` ; options dans `user/options.json`.

## Rendu (Poiesis.Render)

- **`VulkanContext`** : instance, surface, device (GPU dédié de préférence, sinon intégré, sinon logiciel), file unique dessin et présentation, allocateur VMA, envois ponctuels (`Immediate`).
- **`Swapchain`** : deux images en vol. La `VkRenderPass` est classique, parce que le backend Vulkan d'ImGui en veut une. La swapchain est reconstruite au redimensionnement. Elle peut aussi capturer en PNG l'image présentée (`--capture`, F12).
- **`SceneRenderer`** : le viewport 3D, en rendu dynamique Vulkan 1.3, dans une cible couleur et profondeur hors écran. Il y a une cible par image en vol, pour ne jamais réécrire une texture qu'ImGui lit encore. Un pipeline dessine les maillages instanciés éclairés : un appel par maillage, toutes ses instances d'un coup. Un autre dessine les lignes (grille, gizmos). Viewport et scissor sont dynamiques, donc un redimensionnement ne recrée aucun pipeline. Les shaders GLSL sont compilés au démarrage par ShaderC.
- **`RenderFrame`** : ce qu'on demande pour une image (caméra, instances groupées par maillage, lignes). Le jeu et l'éditeur remplissent, le rendu dessine.
- **Conventions** : matrices de Brutal.Numerics en lignes (comme System.Numerics), lues telles quelles en colonnes par GLSL (`M * v` côté shader équivaut à `v * M` côté C#). Viewport de hauteur négative pour garder y vers le haut.
- **Composant `Renderable`** (moteur) : forme de base (boîte, sphère, deux coques procédurales), dimensions en mètres, couleur. L'éditeur dessine n'importe quel jeu sans le connaître ; les vrais modèles viennent au jalon 3.

## Interface de l'éditeur (Poiesis.Editor)

- **`ImGuiHost`** : Dear ImGui (branche docking de BRUTAL) sur ses backends GLFW et Vulkan, police système par FreeType, thème, disposition sauvegardée dans le projet.
- **`EditorApp`** : barre de menus (avec Play, Pause, Arrêter, vitesse et Compiler au centre, comme Godot), raccourcis globaux, disposition par défaut (DockBuilder), splash BRUTAL, compilation puis rechargement, script `--do`.
- **Panneaux** : `ViewportPanel`, `ScenePanel`, `InspectorPanel` (inspecteur générique `PropertyEditor`), `ConsolePanel`, `EntitiesPanel`, `DataPanel`, `AiPanel`. Chacun lit et modifie l'`EditorSession`, sans état métier propre.
- **Modifications** : toute modification validée est une commande annulable. Pendant un glissement (inspecteur ou gizmo), la valeur est appliquée en direct, puis devient une seule commande au relâchement.
- **Aperçu** : en édition, la scène est recuite dans un monde neuf à chaque révision du document. On voit donc exactement ce que donnera le Play. En jeu, le viewport montre le monde de la partie. La correspondance entité → nœud (`BakeContext.OwnerOf`) sert à la sélection au clic.

## Arborescence

```text
src/
  Poiesis.Engine.Core/     ECS, systèmes, pas fixe
  Poiesis.Engine.Scene/    nœuds, sérialisation, contrat IGameModule
  Poiesis.Engine.Hosting/  chargement à chaud, session de jeu
  Poiesis.Render/          rendu Vulkan 1.3 (contexte, swapchain, viewport, maillages, HDR et bloom)
  Poiesis.Audio/           lecture des sons (SDL3_mixer)
  Poiesis.AI/              fournisseurs, agent, workflow feature
  Poiesis.Editor.Core/     modèle de l'éditeur (session, commandes, build, projet, journal)
  Poiesis.Editor/          exécutable de l'IDE (ImGui, panneaux, splash)
  Poiesis.Headless/        runner sans fenêtre
games/Orionum/
  poiesis.project.json     description du projet pour l'éditeur
  src/Orionum.Game/        gameplay (DLL rechargée à chaud)
  content/                 scènes et définitions JSON
  tests/                   tests de bout en bout (chargement, cuisson, bataille, hot reload)
tests/                     tests du moteur et de l'IA
```
