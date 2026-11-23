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

Trois pièges, tous rencontrés et couverts par `Hot_reload_keeps_the_edited_scene_and_unloads_the_old_assembly` :

- **System.Text.Json** garde les types qu'il sérialise par réflexion dans des caches statiques. Les valeurs des scènes et des données du jeu passent donc par `ValueCodec`, qui ne garde rien.
- **Les temporaires du JIT** : en Debug, et en Tier 0 même en Release, un cadre de pile garde ses temporaires vivants jusqu'à la fin de la méthode. Le code qui touche des objets du jeu est isolé dans des méthodes `NoInlining`, séparées de la boucle de GC.
- **Une `WeakReference` vers l'objet `Assembly`** suffit à le retenir. On observe l'`AssemblyLoadContext`, pas l'assembly.

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

## Arborescence

```text
src/
  Poiesis.Engine.Core/     ECS, systèmes, pas fixe
  Poiesis.Engine.Scene/    nœuds, sérialisation, contrat IGameModule
  Poiesis.Engine.Hosting/  chargement à chaud, session de jeu
  Poiesis.Render/          contrat de rendu (backend Vulkan : jalon 1)
  Poiesis.AI/              fournisseurs, agent, workflow feature
  Poiesis.Editor.Core/     modèle de l'éditeur
  Poiesis.Editor/          exécutable de l'IDE
  Poiesis.Headless/        runner sans fenêtre
games/Orionum/
  src/Orionum.Game/        gameplay (DLL rechargée à chaud)
  content/                 scènes et définitions JSON
  tests/                   tests de bout en bout (chargement, cuisson, bataille, hot reload)
tests/                     tests du moteur et de l'IA
```
