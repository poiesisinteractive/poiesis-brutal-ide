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

## Choix faits pendant le J2a (2026-11-24)

| Sujet | Choix | Pourquoi |
| --- | --- | --- |
| Réécriture | **Reprise de l'esprit et des chiffres du POC**, sans sa structure : données en JSON, sim sur l'ECS, phases parallèles | Parité abandonnée : on garde ce qui fait le jeu (classes, armes, vol, boucliers, dégâts localisés). |
| Projectiles et effets | **Tables de ressources**, pas d'entités ECS | Des dizaines de milliers d'objets créés et détruits à chaque seconde : le retrait ordonné de l'ECS serait trop coûteux. |
| Déterminisme parallèle | **Photo des positions en début de pas, effets croisés fusionnés dans l'ordre des tranches, aléa sans état** | Constaté en écrivant le vol : lire la cible pendant qu'un autre thread la déplace rend la sim dépendante des threads. |
| Point d'impact | **Sur la coque allongée**, réparti sur la longueur | Le POC prenait la sphère englobante : un tir de flanc sur un vaisseau de 3 km tombait à 1,5 km de l'axe et n'usait aucune tourelle (révélé par un test). |
| Composants à tableaux | **Champs nommés plus indexeur** (`Shields.Front…`, `WeaponBank.W0…`), pas de `InlineArray` | Lisibles et éditables tels quels dans l'inspecteur d'entités. |
| Hangars | **Reportés** | Les escadrilles demandent leur propre IA de vol ; le J2a se concentre sur les capitaux. |
| Astres | **Décor** (sphères), sans gravité ni collision | Suffisant pour situer la bataille ; l'évitement viendra avec l'IA. |
| Cap des flottes | Propriété **`Heading`** (degrés) et anneau de rotation générique | Tout nœud qui a `Position` et `Heading` profite du gizmo, sans code d'éditeur propre au jeu. |

## Choix faits pendant le J2b (2026-11-24)

| Sujet | Choix | Pourquoi |
| --- | --- | --- |
| Jeu exporté | **Un joueur générique** (`Poiesis.Player`) + la DLL du jeu + son contenu, décrits par `game.json` | Le même exécutable sert tous les jeux ; le jeu exporté charge son code comme l'éditeur, sans hot reload. |
| Code partagé | **`Poiesis.Runtime`** : ce que l'éditeur et le joueur affichent de la même façon | Le joueur ne dépend pas de l'éditeur ; l'éditeur ajoute ses outils (gizmos, sélection) par-dessus la même `WorldView`. |
| Plateformes d'export | **Windows x64** (cible) et **Linux x64** (pour vérifier ici) | La publication croisée depuis Linux fournit les bibliothèques natives Windows de BRUTAL ; l'export Linux permet de lancer le jeu exporté dans le conteneur. |
| Nom de l'exécutable | **Exécutable renommé** après publication | Passer `AssemblyName` à `dotnet publish` renomme aussi les projets référencés (« Ambiguous project name »). |
| Éditeur de code | **Commande réglable par projet**, VS Code par défaut ; sous Windows via `cmd /c` | `code` y est un script `code.cmd`, introuvable sans le shell. |
| Tests | **`dotnet test` en verbosité normale**, avec lecture des deux formats de totaux | Ce format donne les piles d'appels (emplacement de l'échec), mais pas la ligne de totaux habituelle : constaté en test. |
| Graines des séries | **Convention `Seed`** sur les nœuds | L'éditeur reste générique, sans interface de plus pour le jeu. |
| Bilan de partie | **`IGameReporter`** facultatif : fini, vainqueur, temps, mesures nommées | Le moteur agrège sans connaître le jeu ; le jeu choisit ce qui compte. |
| Hot reload pendant une série | **Reporté** tant que la série tourne | La série utilise le module : le décharger en pleine partie la casserait. |

## Choix faits pendant le J3a (2026-11-24)

| Sujet | Choix | Pourquoi |
| --- | --- | --- |
| Surcharges | **Calculées à l'enregistrement**, par comparaison avec la source rechargée | Pas d'état « surchargé » à tenir à jour à chaque modification : le fichier ne garde que les vraies différences, et une valeur remise comme la source cesse d'être une surcharge. |
| Clés de surcharge | **Identifiant du nœud dans la source** (pas son chemin) | Renommer ou réordonner les nœuds de la source ne casse pas les surcharges. |
| Identifiants des nœuds d'instance | **Dérivés** de l'instance et de la source | Deux instances d'une même scène ne partagent rien ; les identifiants sont stables sans être écrits. |
| Structure d'une instance | **Verrouillée** (pas d'ajout ni de suppression d'enfant dans une instance) ; « Rendre local » pour en sortir | Comme Godot sans « enfants éditables » : simple, et suffisant pour les flottes types. |
| Positions dans une instance | **Absolues**, sans transformation héritée du parent | Les nœuds d'Orionum portent des positions absolues ; une flotte type se déplace en surchargeant la position de sa racine. Une hiérarchie de transformations viendra avec l'édition avancée (J4, repère local ou global). |
| Presse-papiers | **Celui du système**, en JSON | Coller entre scènes, et entre deux éditeurs ouverts ; le texte reste lisible. |
| Fichiers de contenu | **Opérations directes sur le disque**, avec confirmation pour supprimer | Le projet est sous git : l'historique sert de corbeille. |
| Nom de l'API d'extension | **`Poiesis.Editor.Api`**, référencée par `Poiesis.Engine.Hosting` | Tous les hôtes (éditeur, headless, joueur) doivent pouvoir charger les types du jeu, même sans éditeur. |
| Nouveaux panneaux dans une ancienne disposition | **Ancrés à côté d'un voisin** à leur première apparition | Sinon ImGui les ouvre flottants et minuscules (constaté avec un `imgui.ini` d'avant le J3a). |

## Choix faits pendant le J3b (2026-11-24)

| Sujet | Choix | Pourquoi |
| --- | --- | --- |
| Maillages propres au jeu | **Fabriqués par le jeu, rendus par le moteur** (`MeshLibrary` dans le monde, `Renderable.Asset`) | Le rendu reste générique ; un jeu peut générer, importer ou mêler les deux. |
| Libération des maillages | **Après 120 images sans usage**, puis après les images en vol | Le concepteur régénère la coque à chaque réglage : sans libération, la mémoire GPU grossirait à chaque glissement. |
| Textures | **Une texture de couleur par maillage**, blanche par défaut, teintée par la couleur de l'instance | Le minimum utile pour les modèles importés et le blindage des coques ; matériaux complets (normales, métal) plus tard. |
| Format des modèles | **glTF 2.0** ; textures PNG, JPEG ou **KTX2 Basis** ; l'export écrit du KTX2 Basis | glTF est le format d'échange courant ; KTX2 Basis est compact et se transcode vers tout GPU. |
| Coque générée | **Paramètres sans unité** (fractions de la longueur) dans le JSON de la classe | Changer la longueur d'une classe garde sa silhouette ; le diff d'une coque se lit. |
| Tourelles et coque | **Posées à la demande** (« Poser les tourelles sur la coque »), pas à chaque changement | Les emplacements sont des données de gameplay : on ne les déplace pas sans le dire. |
| Aperçus et vignettes | **Rendus hors écran à part** (un rendu pour l'aperçu, un pour les vignettes) | Le viewport n'est jamais dérangé ; les vignettes se relisent sans attendre l'image suivante. |
| Cache des vignettes | **Sur disque, par clé** (fichier, date, génération du module) | Rouvrir le projet ne refait pas toutes les vignettes ; recharger le jeu refait celles qu'il dessine. |
| L'IA et les outils d'éditeur | **L'agent reçoit un résumé de l'API, vérifié par un test**, et lit les extensions existantes | Le résumé évite qu'il invente l'API ; le test l'oblige à suivre ses évolutions. |
| Réglages des extensions | **Texte, dans `.poiesis/extensions.json`** | Ils survivent aux rechargements sans retenir de type du jeu. |

## Choix faits pendant le J4a (2026-11-24)

| Sujet | Choix | Pourquoi |
| --- | --- | --- |
| Découpage du J4 | **J4a éditeur, J4b jeu** | L'éditeur ne dépend d'aucune décision ; l'audio attend le choix FMOD ou SDL_mixer. |
| Sélection au rectangle | **Maj + glisser** (clic gauche) | Le glisser simple tourne la caméra depuis le jalon 1 ; on ne change pas ce geste. |
| Nœud principal | **Le dernier sélectionné** | Référence naturelle pour aligner, pour le repère local et pour l'inspecteur (comme Blender et Unity). |
| Inspecteur groupé | **Une valeur pour tous**, si les nœuds sont du même type | Le cas courant (même échelle, même composition pour plusieurs flottes) ; sinon seul le principal est affiché. |
| Gizmo d'échelle | **Convention `[ScaleHandle]`** sur une propriété numérique | Les nœuds d'Orionum n'ont pas d'échelle géométrique ; ce qui « grandit » dépend du nœud (rayon, espacement). |
| Magnétisme | **Pas de la grille affichée**, 15°, 10 % ; Ctrl l'inverse | Le pas suit le zoom : à 500 km de vue, 100 km ; de près, 1 km. |
| Caméras enregistrées | **Par scène, sur le poste** (`.poiesis/cameras.json`) | Une aide de travail, pas une donnée du jeu (comme la disposition des panneaux). |
| IA sur la scène | **Propositions sur une copie, fantômes, cases à cocher, une commande** | Rien ne change sans validation ; on garde une partie d'une proposition ; une annulation défait tout. |
| Outils de l'agent de scène | **Scène et contenu seulement**, valeurs validées comme dans l'inspecteur | L'agent ne touche pas aux fichiers ; ses erreurs (type inconnu, hors bornes) lui reviennent pour qu'il corrige. |

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
