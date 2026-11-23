# Jalons

Avec une équipe d'une ou deux personnes, chaque jalon est une **tranche verticale utilisable**, pas une couche complète. Un jalon est fini quand on peut faire la démo décrite dans son critère.

## J0 : squelette ✅ (ce commit)

- Solution .NET 10 et paquets BRUTAL, compilée et testée (20 tests).
- ECS déterministe, nœuds, sérialisation JSON, cuisson, pas fixe.
- Module de jeu rechargé à chaud, déchargement vérifié par test.
- Modèle de l'éditeur : document, sélection, annuler/rétablir, Play/Pause/Stop, hot reload qui garde la scène.
- IA : conversation neutre, Anthropic (SDK officiel), client compatible OpenAI (OpenAI, Gemini, Ollama, LM Studio), boucle d'agent, outils confinés au worktree, workflow feature (branche, garde-fous, diff, accepter ou refuser).
- Orionum minimal : deux flottes JSON, approche et combat placeholder, runner headless.

## J1 : la tranche verticale de l'éditeur

Critère : *on ouvre Orionum dans l'IDE, on déplace une flotte dans le viewport, on change le nombre de croiseurs dans l'inspecteur, on appuie sur Play et on voit la bataille ; on demande à l'IA « les croiseurs des Clans tirent 20 % plus loin », on relit le diff, on accepte, le jeu se recharge à chaud sans fermer l'éditeur.*

1. **Vulkan** : instance, device, swapchain, cible hors écran du viewport, un pipeline de maillages instanciés (couleur unie et éclairage simple). Partir de `samples/Demo/DemoVulkan.cs` de BRUTAL.
2. **ImGui** sur ses backends GLFW et Vulkan, avec le docking ; disposition par défaut sauvegardée.
3. **Splash BRUTAL** et écran de crédits (obligations de licence).
4. **Panneaux** : arbre de scène (ajouter, supprimer, renommer, glisser-déposer), inspecteur généré depuis les propriétés (float, int, enum, `float3`, chaîne, référence de catalogue), console de logs, barre Play/Pause/Stop et vitesse.
5. **Viewport** : caméra orbitale, sélection au clic, gizmo de translation, aperçu des flottes en mode édition (cuisson à chaque modification).
6. **Inspecteur d'entités en direct** pendant le Play : entités, composants, valeurs.
7. **Build intégré** : `dotnet build` de la DLL du jeu depuis l'éditeur, erreurs cliquables, hot reload automatique à la fin du build.
8. **Panneau IA** : choix du fournisseur, description de la feature, flux des actions de l'agent, état des garde-fous, diff coloré, boutons Accepter, Refuser, Ouvrir le worktree. Jetons consommés affichés.
9. **Définitions de vaisseaux** : éditeur de `*.ship.json` dans l'inspecteur, rechargement des données sans recompiler.

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
