# Décisions (grill du 2026-11-23)

Poiesis Brutal IDE est un éditeur de jeux à la manière de Godot ou Unity, construit sur [BRUTAL](https://github.com/ahwootech/BRUTAL), le framework C# d'Ahwoo. BRUTAL ne fournit volontairement ni éditeur, ni scene graph, ni renderer : ce dépôt contient donc **le moteur et l'éditeur**.

## Produit

| Sujet | Décision |
| --- | --- |
| Public | **Outil interne Poiesis.** On tranche fort, on ignore les cas généraux, on livre vite. Pas de stabilité d'API publique à garantir. |
| Jeu de référence | **Orionum Battles** (`poiesisinteractive/brutal-orionum-battles-poc`) : combat de flottes temps réel en 3D. C'est lui qui arbitre le périmètre. |
| Sort du POC | **Réécriture sur le nouveau moteur.** La parité avec le proto TypeScript est abandonnée ; le TS et ses outils de comparaison sortent du POC. Le POC sert de référence de gameplay et de performance, pas de code à garder. |
| Équipe | **Solo ou deux personnes, avec l'IA.** D'où un premier jalon très étroit. |
| Plateforme | **Windows** pour l'éditeur et les jeux exportés. Linux reste possible (BRUTAL le supporte, le CI tourne sous Linux) ; macOS et consoles plus tard, ou jamais. |

## Moteur

| Sujet | Décision |
| --- | --- |
| Dimension | **3D d'abord.** |
| Modèle d'objets | **Hybride : nœuds en façade, ECS dessous.** L'arbre de nœuds sert à créer le contenu ; au Play, les nœuds sont cuits en entités ECS. |
| Ce qui est un nœud | **Seulement le contenu créé dans l'éditeur** : scénario, flottes, escadres, astres, prefab de vaisseau. Ce qui naît en jeu (projectiles, drones) n'a pas de nœud ; un inspecteur d'entités en direct le montre. Une bataille à 2 000 coques reste un arbre de quelques dizaines de nœuds. |
| Rendu | **Vulkan 1.3 dès le départ** (Brutal.Vulkan, Vulkan.Abstractions, VMA, ShaderC). Le renderer OpenGL du POC n'est pas repris. |
| Données | **Texte lisible : JSON**, diffable dans git, lisible et modifiable par l'IA. Scènes en `*.scene.json`, définitions en `*.ship.json` etc. Compilation en binaire à l'export si le chargement le demande. |
| Sim | **Pas fixe à 20 Hz**, déterministe sur une même machine (ordre d'itération stable, application des effets après la phase de choix). |

## Éditeur

| Sujet | Décision |
| --- | --- |
| UI | **Dear ImGui, branche docking** (Brutal.ImGui, backends GLFW et Vulkan). L'éditeur et le moteur sont dans le même process. |
| Play mode | **Dans le process de l'éditeur.** La scène est cuite dans un monde neuf ; au Stop, on jette le monde, la scène n'a pas bougé. |
| Code du jeu | **Une DLL de gameplay rechargée à chaud** (`AssemblyLoadContext` collectible). La scène est sérialisée avant le rechargement et relue après. |
| Contenu du jalon 1 | Scénarios de bataille et définitions de vaisseaux et d'armes. Les modèles procéduraux, les effets, le son et l'UI de jeu sont voulus mais viennent ensuite (voir [ROADMAP.md](ROADMAP.md)). |

## IA « feature d'abord »

| Sujet | Décision |
| --- | --- |
| Principe | **Le dev décrit une fonctionnalité, l'IA la réalise.** L'accent est sur la feature, pas sur le code ; le dev peut toujours ouvrir le code et reprendre la main. |
| Fournisseurs | **Multi-fournisseurs** : Anthropic (Claude), OpenAI, Gemini, et modèles locaux (Ollama, LM Studio). |
| Isolation | **Une branche git par feature**, dans un worktree sous `.poiesis/worktrees/`. Le projet principal n'est pas touché avant acceptation. |
| Garde-fous | **Build et tests obligatoires**, plus **sim headless** quand la feature touche l'équilibrage ou les performances. Si un garde-fou échoue, l'agent reçoit la sortie et corrige (trois tentatives). |
| Validation | **Un diff à accepter ou refuser**, code et données ensemble. Accepter fusionne la branche ; refuser retire le worktree et garde la branche. |

## Choix faits pendant le squelette (à contester)

| Sujet | Choix | Pourquoi |
| --- | --- | --- |
| Accès aux fournisseurs | Anthropic par son **SDK officiel C#** ; OpenAI, Gemini, Ollama et LM Studio par **un seul client HTTP Chat Completions** | Les quatre derniers parlent la même API (Gemini via sa passerelle compatible OpenAI). Un client natif Gemini viendra si cette passerelle limite. |
| Modèle Claude par défaut | `claude-opus-5-5`, effort `high`, **repli côté serveur activé** (`fallbacks: "default"`) | Si un filtre de sécurité décline une demande, le serveur la rejoue sur un modèle de repli au lieu d'arrêter l'agent. Désactivable. |
| Clés d'API | Jamais dans le projet : seulement le **nom de la variable d'environnement** | Les fichiers du projet passent par git et par l'IA. |
| Sérialisation | **Codec maison** (`ValueCodec`) plutôt que `JsonSerializer` | La réflexion de System.Text.Json garde les types du jeu dans des caches statiques et empêche le déchargement au hot reload (constaté et testé). |
| ECS | **Sparse sets triés par slot**, composants en structs, `World.Clone()` par copie de tableaux | Ordre d'itération déterministe ; clone bon marché pour les projections tactiques du POC et le snapshot du Play. Les archétypes viendront si le profilage le demande. |
| Partie en cours au hot reload | **Arrêtée** au jalon 1 | Migrer l'état ECS entre deux versions des composants est un chantier en soi. |
| Agent et moteur | L'agent **n'écrit pas** dans `src/Poiesis.*` ni dans `.git` | Le moteur évolue à la main ; l'agent signale ce qui manque. |

## Choix faits pendant le jalon 1 (2026-11-24)

| Sujet | Choix | Pourquoi |
| --- | --- | --- |
| Vérification | **Vulkan logiciel (lavapipe) sous Xvfb**, avec capture PNG et souris simulée | L'éditeur se lance et se teste dans le cloud et en CI, sans GPU ni écran. Ça ne remplace pas un essai sur Windows avec un vrai GPU (voir ROADMAP, J1.5). |
| Passe de rendu | `VkRenderPass` classique pour la fenêtre, rendu dynamique pour le viewport | Le backend Vulkan d'ImGui fourni par BRUTAL exige une render pass ; le viewport, lui, profite de Vulkan 1.3. |
| Barre d'outils | **Dans la barre de menus**, au centre | Comme Godot : toujours visible, sans panneau à placer. |
| Vue lointaine | **Taille apparente minimale** de 22 px par coque | À 130 km, un croiseur de 600 m fait moins d'un pixel. La vue devient tactique, comme les icônes du POC. |
| Projet de jeu | **`poiesis.project.json`** à la racine du jeu | L'éditeur sait quoi compiler, quoi charger, quoi tester (garde-fous de l'IA) et quelle scène ouvrir. |
| Données de jeu | **Édition générique des JSON** dans le panneau Données, puis rechargement du module | L'éditeur reste indépendant du jeu ; le module relit ses définitions au chargement. |
| Références de contenu | Attributs **`[ContentRef]`** et **`[Range]`** sur les propriétés de nœud | L'inspecteur propose la liste des classes de vaisseau et des bornes, sans code d'éditeur propre au jeu. |
| Échec de l'agent | **Le worktree et la branche sont supprimés** ; un titre déjà pris reçoit un suffixe | Constaté en test : une erreur du fournisseur laissait une branche orpheline qui bloquait la relance. |
| Requêtes HTTP | **Corps avec `Content-Length`**, pas de transfert par morceaux | Certains serveurs locaux compatibles OpenAI ne lisent pas les corps envoyés par morceaux. |

## Grill du 2026-11-24 : un vrai éditeur

| Sujet | Décision |
| --- | --- |
| Stratégie | **Le jeu tire l'éditeur** : chaque jalon d'Orionum livre les outils d'éditeur dont il a besoin. Pas de jalon « éditeur seul ». |
| Indispensable | **Les quatre familles** : contenu et réutilisation, édition de scène avancée, débogage en jeu, code et build. Réparties du J2 au J4 (voir ROADMAP). |
| Outils propres au jeu | **Extensions fournies par le jeu** : le moteur et l'éditeur restent génériques ; le jeu ajoute ses panneaux, inspecteurs et gizmos par une API d'extension rechargée à chaud. L'IA peut les écrire. |
| Réutilisation | **Scènes imbriquées, à la Godot** : toute scène s'instancie dans une autre, avec des surcharges. Pas de type « prefab » à part. |
| IA dans l'éditeur | **Trois rôles en plus du workflow feature** : agir sur la scène ouverte, écrire des outils d'éditeur, analyser une partie. |
| IA sur la scène | **Aperçu puis validation** : fantômes dans le viewport et liste dans le panneau IA, puis une seule commande annulable. Pas de branche git pour ça. |
| Retour arrière | **Instantanés manuels** (prendre, revenir) au J2. La frise complète vient plus tard. |
| Éditeur de code | **VS Code** (`code --goto fichier:ligne:colonne`). |
| Export | **Windows autonome dès le J2**, pour faire tester la bataille sans l'éditeur. |

## Questions encore ouvertes

1. **Audio** : FMOD (bindings BRUTAL, mais licence FMOD à payer et bibliothèques natives non fournies) ou SDL_mixer (choix du POC, gratuit, plus limité) ?
2. **Modèles 3D** : jusqu'où vont les modèles procéduraux du POC, et quand passe-t-on à l'import glTF ?
3. **UI de jeu** : ImGui aussi en jeu, comme dans le POC, ou une UI dédiée plus tard ?
4. **Export** : comment l'éditeur produit-il un build de jeu autonome (copie des DLL, contenu compilé, exécutable hôte) ?
5. **Coût de l'IA** : budget par feature, choix du modèle par tâche, et affichage des jetons consommés.
6. **POC** : la suppression du TS (`tools/ts-reference`, tests de parité, `docs/PROTO-TS-README.md`) se fait dans le dépôt du POC, pas ici.

## Obligations de licence BRUTAL

L'éditeur comme les jeux qu'il produit sont des produits « construits avec BRUTAL » au sens de la [BRUTAL License v1.0](https://github.com/ahwootech/BRUTAL/blob/main/LICENSE.txt). Chacun doit :

1. citer le copyright BRUTAL dans ses crédits (section 2.1) ;
2. afficher le logo BRUTAL avant de démarrer (section 2.2), sauf accord écrit d'Ahwoo ;
3. livrer le `NOTICE.txt` de BRUTAL (section 2.3).

Le splash et l'écran de crédits sont au jalon 1 de l'éditeur et dans le gabarit d'export des jeux.
