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
  Poiesis.Render/          rendu Vulkan 1.3 (contexte, swapchain, viewport, maillages)
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
