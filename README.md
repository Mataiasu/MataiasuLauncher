# Launch'aiasu

Launcher Windows universel en développement.

## V1

- Détection des jeux enregistrés dans Windows.
- Détection des jeux installés via Steam.
- Détection des jeux installés via Epic Games.
- Recherche dans la bibliothèque.
- Bibliothèques personnalisées et favoris.
- Modes de lancement multiples quand disponibles.
- Affichage de la source et du chemin d'installation.
- Lancement via Steam URI quand disponible.
- Lancement direct de l'exécutable quand il est connu.
- Build Windows x64 autonome via GitHub Actions.

## Build

Le workflow `Build Launch'aiasu` compile automatiquement `MataiasuLauncher.exe` sur chaque push sur `main` et peut aussi être lancé manuellement depuis l'onglet Actions.

## Roadmap

- Jaquettes et bibliothèque visuelle.
- Détection Ubisoft Connect, EA app, Battle.net et Xbox.
- Détection plus fiable des exécutables.
- Profils de lancement et arguments.
- Historique et temps de jeu.
- Gestion avancée des mods.
- Mise à jour automatique du launcher.
