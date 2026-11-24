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

## J2 : Orionum réécrit, première partie

Critère : *la bataille des capitaux du POC tourne dans l'éditeur, en lignes de bataille lisibles.*

- Vol newtonien assisté, croisière, boucliers à 6 faces, sous-systèmes, tourelles et lances, dégâts localisés.
- Index spatial et phases parallèles (`JobScheduler` de BRUTAL) : objectif 500 contre 500 fluide.
- Rendu de masse : instanciation, icônes tactiques au loin.

## J3 : modèles procéduraux et assets

- Générateur de coques paramétré (proues, superstructures, placement des tourelles), édité comme un nœud avec aperçu 3D.
- Import glTF (Brutal.Gltf, MeshOptimizer) et textures KTX2 en alternative.

## J4 : effets, son, UI de jeu

- Effets : tirs, impacts de bouclier hexagonaux, explosions, HDR et bloom en Vulkan.
- Audio selon la décision FMOD ou SDL_mixer ; banques de cues éditables.
- HUD et menus.

## Plus tard

- Migration de l'état ECS au hot reload (sans arrêter la partie).
- Export d'un build de jeu autonome.
- Play en process séparé (Brutal.Monitor) pour tester le build réel.
- Linux, puis peut-être macOS.
