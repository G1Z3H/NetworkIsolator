# NetworkIsolator

[🇬🇧 English](README.md) · 🇫🇷 Français

Un petit outil Windows qui empêche **une application précise** d'accéder au réseau, sans toucher à rien d'autre sur votre PC. Il crée des règles ciblées dans le Pare-feu Windows : c'est Windows lui-même qui applique le blocage.

- **Aucune dépendance tierce** : C# / WPF sur .NET, appels Win32 natifs uniquement.
- **Réversible** : seules des règles de pare-feu sont créées. Pas de pilote, pas de modification du registre, l'application n'est pas touchée.
- **Interface bilingue** : français et anglais, changement à la volée (le choix est mémorisé).

## Deux façons de l'utiliser

| | Ce que ça fait |
|---|---|
| 🔒 **Bloquer pour toujours** | Crée des règles de pare-feu permanentes. L'application ne peut plus se connecter, quelle que soit la façon de la lancer (menu Démarrer, raccourci, double-clic). L'outil n'a pas besoin de rester ouvert. |
| ▶ **Lancer sans connexion (une fois)** | Démarre l'application avec le réseau coupé pour cette session uniquement. Les règles sont supprimées à la fermeture de l'application. |

Débloquer, ou retirer une application de la liste, supprime ses règles.

## Fonctionnalités

- Liste « Mes applications » avec l'état **réel** du pare-feu pour chaque entrée (bloquée / libre / incomplet).
- Niveau de blocage : **tout le réseau**, ou **Internet seulement** (le réseau local reste accessible).
- Blocage optionnel des connexions entrantes (neutralise les règles « autoriser » ajoutées par certains installeurs).
- Plusieurs programmes par entrée (une application et ses programmes annexes).
- Lancement ponctuel avec : application sans droits administrateur, suivi de l'arbre de processus (les processus enfants sont coupés aussi), variables proxy mortes en couche supplémentaire.
- Résistance aux plantages : un journal écrit à l'avance permet de nettoyer les règles temporaires laissées par un crash ou un kill.
- Raccourcis Bureau, import / export de la liste, journal d'activité.
- Ligne de commande pour les scripts et les raccourcis.
- Refuse de bloquer les programmes du dossier Windows (cela pourrait couper votre accès à Internet ou casser le système).

## Prérequis

- Windows 10 / 11
- Droits administrateur (l'outil les demande une seule fois au démarrage ; ils sont nécessaires pour créer et supprimer des règles de pare-feu)
- Pare-feu Windows activé
- Pour compiler : SDK .NET 10

## Compilation

```powershell
dotnet build -c Release
```

Publication en un seul fichier :

```powershell
dotnet publish NetworkIsolator/NetworkIsolator.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## Ligne de commande

```text
NetworkIsolator.exe <app.exe> [options] [-- arguments de l'application]
NetworkIsolator.exe --profile "Nom" [options]
NetworkIsolator.exe --block "Nom"          bloque un profil pour toujours
NetworkIsolator.exe --unblock "Nom"        retire le blocage permanent d'un profil
NetworkIsolator.exe --list | --cleanup | --version | --help
NetworkIsolator.exe --shortcut "Nom"       crée un raccourci Bureau pour un profil
NetworkIsolator.exe --export <fichier.json> | --import <fichier.json>
NetworkIsolator.exe                        ouvre l'interface
```

Options : `--lan`, `--block-all`, `--no-inbound`, `--no-proxy-env`, `--no-children`, `--keep-admin`, `--args "..."`, `--cwd <dossier>`, `--wait`.

Codes de sortie : `0` ok, `1` erreur, `2` syntaxe, `3` introuvable, `4` UAC refusé, `5` pare-feu, `6` lancement.

## Fonctionnement

- Chaque règle est une règle de **blocage** ciblant le chemin complet d'un `.exe`, créée avec `netsh advfirewall`.
- Les règles permanentes s'appellent `NetIsolator_P_<empreinte>_<nom>_<out|in>` ; le nom est dérivé du chemin, on peut donc toujours les retrouver, les vérifier et les supprimer.
- Les règles temporaires (lancement ponctuel) s'appellent `NetIsolator_...` et sont notées dans un journal avant d'être créées.
- Le mode « Internet seulement » bloque les plages d'adresses publiques (un blocage l'emporte toujours sur une autorisation dans le Pare-feu Windows).

## Données

Tout est stocké dans `%APPDATA%\NetworkIsolator` : `profiles.json` (votre liste), `sessions\` (journal), `logs\`, `language.txt`.

## Tout annuler à la main

Ouvrez `wf.msc` (Pare-feu Windows Defender avec fonctions avancées de sécurité) et supprimez les règles dont le nom commence par `NetIsolator_`.

## Limites

- Le blocage vise une application, pas Windows : les services système ne sont pas concernés.
- La recherche d'un nom de site passe par un service Windows et n'est pas bloquée, mais la connexion elle-même l'est.
- Les applications du Microsoft Store (UWP) ne sont pas prises en charge.
- Le blocage suit le chemin du fichier : si l'application est déplacée ou réinstallée ailleurs, il faut la rebloquer.
- Une application bloquée perd ses fonctions en ligne (multijoueur, mises à jour, vérification de licence, synchronisation). Certains programmes refusent de démarrer sans connexion : débloquez-les.
