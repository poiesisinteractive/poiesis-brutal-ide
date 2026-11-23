# Poiesis Brutal IDE

Un éditeur de jeux à la manière de Godot ou Unity, construit sur [BRUTAL](https://github.com/ahwootech/BRUTAL), le framework C# d'Ahwoo. On y décrit une fonctionnalité de jeu, et l'IA l'écrit. Outil interne Poiesis ; premier jeu : *Orionum Battles*.

BRUTAL fournit l'interop, la mémoire, les maths, les jobs et les bindings natifs (Vulkan, GLFW, Dear ImGui, Jolt…), sans éditeur, ni scene graph, ni renderer. Ce dépôt ajoute ce qui manque : un moteur (nœuds et ECS, sim à pas fixe, rendu Vulkan) et son éditeur.

- Décisions : [docs/DECISIONS.md](docs/DECISIONS.md)
- Architecture : [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- Jalons : [docs/ROADMAP.md](docs/ROADMAP.md)

## État

Squelette (J0) : tout compile et 20 tests passent. Le moteur, le chargement à chaud, le modèle de l'éditeur et le workflow IA fonctionnent sans fenêtre. Restent au jalon 1 : le rendu Vulkan, l'interface ImGui et le branchement des panneaux. L'exécutable de l'éditeur ouvre pour l'instant une fenêtre vide.

## Prérequis

- [SDK .NET 10](https://dotnet.microsoft.com/download)
- Windows x64 et un GPU Vulkan 1.3 (pilotes à jour). Les paquets `Ahwoo.Brutal.*` viennent de NuGet.
- Git (le workflow IA crée des worktrees).
- Pour l'IA, au choix : `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `GEMINI_API_KEY`, ou un serveur local Ollama ou LM Studio.

## Commandes

```sh
dotnet build
dotnet test
```

Jouer la bataille d'exemple sans fenêtre (600 s simulées) :

```sh
dotnet build -c Release
dotnet run --project src/Poiesis.Headless -c Release -- games/Orionum/src/Orionum.Game/bin/Release/net10.0/Orionum.Game.dll games/Orionum/content games/Orionum/content/scenarios/line-battle.scene.json 600
```

Lancer l'éditeur sur Orionum :

```sh
dotnet run --project src/Poiesis.Editor -- games/Orionum games/Orionum/src/Orionum.Game/bin/Debug/net10.0/Orionum.Game.dll games/Orionum/content/scenarios/line-battle.scene.json
```

## Licence

BRUTAL est distribué sous la [BRUTAL License v1.0](https://github.com/ahwootech/BRUTAL/blob/main/LICENSE.txt). L'éditeur et chaque jeu distribué qui l'utilise doivent créditer Ahwoo, afficher le logo BRUTAL au démarrage et livrer le `NOTICE.txt` de BRUTAL.
