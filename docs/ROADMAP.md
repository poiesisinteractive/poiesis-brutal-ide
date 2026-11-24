# Jalons

Avec une équipe d'une ou deux personnes, chaque jalon est une **tranche verticale utilisable**, pas une couche complète. Un jalon est fini quand on peut faire la démo décrite dans son critère.

## J0 : squelette ✅

- Solution .NET 10 et paquets BRUTAL, compilée et testée.
- ECS déterministe, nœuds, sérialisation JSON, cuisson, pas fixe.
- Module de jeu rechargé à chaud, déchargement vérifié par test.
- Modèle de l'éditeur : document, sélection, annuler/rétablir, Play/Pause/Stop, hot reload qui garde la scène.
- IA : conversation neutre, Anthropic (SDK officiel), client compatible OpenAI (OpenAI, Gemini, Ollama, LM Studio), boucle d'agent, outils confinés au worktree, workflow feature (branche, garde-fous, diff, accepter ou refuser).
- Orionum minimal : deux flottes JSON, approche et combat placeholder, runner headless.

## J1 : la tranche verticale de l'éditeur ✅

Critère : *on ouvre Orionum dans l'IDE, on déplace une flotte dans le viewport, on change le nombre de croiseurs dans l'inspecteur, on appuie sur Play et on voit la bataille ; on demande à l'IA « les croiseurs des Clans tirent 20 % plus loin », on relit le diff, on accepte, le jeu se recharge à chaud sans fermer l'éditeur.*

Démontré de bout en bout sous Xvfb, avec Vulkan logiciel, souris et clavier simulés par `xdotool` : sélection au clic, gizmo, Ctrl+Z, Ctrl+D, F5, Ctrl+B et rechargement. Pour l'IA, un serveur local compatible OpenAI jouait l'agent ; le reste était réel (worktree, build et tests du jeu dans le worktree, diff, fusion, recompilation, rechargement en génération 2).

| # | Élément | État |
| --- | --- | --- |
| 1 | **Vulkan** : device, swapchain, viewport hors écran (une cible par image en vol), maillages instanciés éclairés, lignes | ✅ |
| 2 | **ImGui** sur GLFW et Vulkan, docking, disposition par défaut sauvegardée par projet | ✅ |
| 3 | **Splash BRUTAL** au démarrage, crédits dans « À propos », `NOTICE.txt` livré | ✅ |
| 4 | **Panneaux** : arbre (ajouter, dupliquer, supprimer, glisser-déposer), inspecteur généré (float, int, bool, enum, `float3`, chaîne, référence de contenu), console, Play/Pause/Stop et vitesse dans la barre de menus | ✅ |
| 5 | **Viewport** : caméra orbitale, sélection au clic, gizmo de translation sur trois axes, aperçu cuit en édition, taille apparente minimale (vue tactique) | ✅ |
| 6 | **Entités en direct** : liste, composants et valeurs | ✅ |
| 7 | **Build intégré** : Ctrl+B, diagnostics dans la console, rechargement à chaud à la fin | ✅ (les erreurs ne sont pas encore cliquables) |
| 8 | **Panneau IA** : fournisseur, description, actions de l'agent, garde-fous, diff coloré, Accepter, Refuser, chemin du worktree, jetons | ✅ (vérifié avec un serveur simulé ; aucun appel réel à un fournisseur depuis le cloud, faute de clé) |
| 9 | **Données** : édition des `*.ship.json`, enregistrement et rechargement du module | ✅ |

Pas encore fait :
- erreurs de build cliquables ;
- « Enregistrer sous » ;
- dialogues de fichiers natifs ;
- fenêtres ImGui détachables (multi-viewports) ;
- édition de l'arbre pendant une partie.

## J1.5 : à vérifier sur Windows

Le jalon 1 a été écrit et testé sous Linux, sans GPU. À confirmer sur un poste Windows avec un vrai GPU :

- démarrage, police Segoe UI, redimensionnement de la fenêtre, glisser-déposer des onglets ;
- fluidité du viewport sur la scène d'exemple, puis avec 500 coques par camp ;
- un vrai tour d'IA avec chacun des fournisseurs (Anthropic, OpenAI, Gemini, Ollama ou LM Studio) ;
- fusion d'une feature quand le dépôt a des modifications locales non commitées (la fusion git refuse alors de toucher les fichiers concernés ; le message doit être clair).

## Principe des jalons suivants

Le jeu tire l'éditeur (décision du 2026-11-24) : chaque jalon d'Orionum livre aussi les outils d'éditeur dont il a besoin, et rien n'est construit « au cas où ». À la fin du J4, l'éditeur couvre les quatre familles jugées indispensables :
- contenu et réutilisation ;
- édition de scène avancée ;
- débogage en jeu ;
- code et build.

## J2 : Orionum réécrit, première partie, et débogage en jeu

Critère : *la bataille des capitaux du POC tourne dans l'éditeur. On la met en pause sur un tir, on avance image par image, on modifie la coque d'un vaisseau en direct, on revient à un instantané. On exporte le jeu et un joueur le lance sans l'éditeur.*

Jeu :
- vol newtonien assisté, croisière, boucliers à 6 faces, sous-systèmes, tourelles et lances, dégâts localisés ;
- index spatial et phases parallèles (`JobScheduler` de BRUTAL), objectif 500 contre 500 fluide ;
- rendu de masse : instanciation, icônes tactiques au loin.

Éditeur, ce qu'il faut pour mettre au point cette sim :
- **débogage en jeu** : pause, image par image, édition des composants en direct dans le panneau Entités ;
- **instantanés manuels** de la partie (prendre, revenir), sur le clone du monde ECS ;
- **dessin de débogage** fourni par le jeu (portées, cibles, vecteurs) ;
- **profileur par système** (temps par phase et par image) ;
- **gizmo de rotation** (orientation des flottes et des coques) ;
- **code** : erreurs de build cliquables qui ouvrent VS Code à la bonne ligne (`code --goto`), ouverture du projet C#, tests du jeu lancés depuis l'éditeur ;
- **export Windows autonome** : exécutable hôte, DLL du jeu, contenu, splash et `NOTICE.txt` BRUTAL ;
- **IA qui analyse une partie** : métriques et journaux d'une partie, ou d'une série en headless ; l'agent explique l'équilibrage, les bugs, les performances.

## J3 : modèles, contenu et réutilisation

Critère : *on conçoit un croiseur dans un concepteur de coques propre à Orionum, on en fait une flotte type réutilisable, on l'instancie dans deux scénarios avec des surcharges, et on la retrouve dans le navigateur de contenu.*

Jeu :
- générateur de coques paramétré (proues, superstructures, placement des tourelles) ;
- import glTF (Brutal.Gltf, MeshOptimizer) et textures KTX2 en alternative.

Éditeur :
- **API d'extension** : le jeu ajoute ses panneaux, inspecteurs, gizmos et dessins de débogage, rechargés à chaud avec sa DLL. Le concepteur de coques est la première extension d'Orionum ;
- **scènes imbriquées**, à la Godot : toute scène s'instancie dans une autre, avec des surcharges de propriétés enregistrées dans l'instance ;
- **navigateur de contenu** : arborescence, vignettes rendues hors écran, création, renommage, déplacement avec mise à jour des références ;
- **copier-coller** de nœuds, entre scènes comprises ;
- **IA qui écrit des outils d'éditeur** : une extension générée dans la DLL du jeu, par le workflow feature.

## J4 : effets, son, UI de jeu, et édition de scène avancée

Critère : *on compose la bataille à l'échelle de Wolf 359 (astres, flottes, escadres) en multi-sélection et magnétisme, ou en demandant à l'IA « place trois escadres en tenaille autour des Clans ». On voit l'aperçu, on valide, et la bataille a ses effets, son son et son HUD.*

Jeu :
- effets : tirs, impacts de bouclier hexagonaux, explosions, HDR et bloom en Vulkan ;
- audio selon la décision FMOD ou SDL_mixer, avec des banques de cues éditables ;
- HUD et menus.

Éditeur :
- **édition de scène avancée** : multi-sélection (rectangle, Ctrl), gizmo d'échelle, magnétisme sur la grille, repère local ou global, alignement et répartition, caméras enregistrées ;
- **IA qui agit sur la scène ouverte** : elle propose des commandes d'éditeur, affichées en fantôme dans le viewport et en liste dans le panneau IA. On les applique d'un clic, en une seule commande annulable, ou on les refuse.

## Plus tard

- Migration de l'état ECS au hot reload (sans arrêter la partie).
- Frise temporelle complète (instantanés réguliers et ordres rejoués), dans le prolongement des instantanés manuels du J2.
- Play en process séparé (Brutal.Monitor) pour tester le build réel.
- Linux, puis peut-être macOS.
