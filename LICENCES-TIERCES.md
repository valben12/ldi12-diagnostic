# Composants tiers

**LDI12 Diagnostic** est un exécutable unique : les bibliothèques dont il dépend sont embarquées
à l'intérieur du fichier plutôt que posées à côté de lui. Elles sont donc **distribuées** avec le
programme, et cette page existe pour cette raison : tant que l'outil vivait sur une clé USB, la
question ne se posait pas ; en téléchargement public, si.

Chaque bibliothèque reste soumise à sa propre licence, qui prime sur celle de LDI12 Diagnostic
pour ce qui la concerne.

Cette liste est tenue dans le code (`src/LDI12.Core/Runtime/Credits.cs`) et **un test vérifie
qu'aucun paquet du projet n'y manque** : un composant ajouté et oublié ici serait distribué sans
sa licence, sans que rien ne le signale. Elle est également consultable dans le programme, écran
**Réglages → À propos**.

## Bibliothèques embarquées

| Composant | Version | Rôle dans LDI12 | Licence |
|---|---|---|---|
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | 0.9.4 | Lecture des capteurs matériels : désactivée par défaut | **MPL-2.0** |
| [HidSharp](http://www.zer7.com/files/oss/hidsharp/LICENSE.txt) | 2.1.0 | Accompagne la bibliothèque de capteurs | Voir la licence publiée par l'auteur |
| [Mono.Posix.NETStandard](https://go.microsoft.com/fwlink/?linkid=869050) | 1.0.0 | Accompagne la bibliothèque de capteurs | Voir la licence publiée par l'éditeur |
| [Newtonsoft.Json](https://www.newtonsoft.com/json) | 13.0.3 | Lecture et écriture des diagnostics archivés | **MIT** |

HidSharp et Mono.Posix.NETStandard n'ont pas été choisis : ils arrivent avec
LibreHardwareMonitorLib. Leurs paquets NuGet ne déclarent qu'une adresse de licence, sans la
nommer : elle est donc citée telle quelle plutôt que devinée.

S'y ajoutent les **bibliothèques de compatibilité .NET publiées par Microsoft** (les
`System.*.dll` qui accompagnent une cible netstandard, ainsi que `netstandard.dll`), redistribuées
selon les termes de Microsoft. Elles ne sont pas listées une par une : il y en a plus de cent, et
les nommer toutes ferait une page que personne ne lirait.

## Obligation particulière à la MPL-2.0

`LibreHardwareMonitorLib` est publiée sous **Mozilla Public License 2.0**. Elle est redistribuée
ici **sans aucune modification**, sous forme binaire, à l'intérieur de l'exécutable.

- Le texte complet de la licence : <https://mozilla.org/MPL/2.0/>
- Le code source de la version distribuée (0.9.4) :
  <https://github.com/LibreHardwareMonitor/LibreHardwareMonitor>

Ce que la MPL n'impose pas, et qu'il faut dire pour éviter le malentendu : elle est une licence
« par fichier ». Redistribuer une bibliothèque sous MPL **n'oblige pas** à publier le code du
programme qui l'utilise, dès lors que ce code vit dans ses propres fichiers, ce qui est le cas
ici.

## À propos du pilote de capteurs

`LibreHardwareMonitorLib` charge le pilote noyau **WinRing0** pour lire les registres du
processeur et les puces de la carte mère. Ce pilote figure sur la liste des pilotes vulnérables
tenue par Microsoft : Windows Defender peut signaler la menace
« VulnerableDriver:WinNT/Winring0 » en désignant le fichier qui porte le nom du programme.

C'est pourquoi la lecture des capteurs est **désactivée par défaut** et ne s'active que par une
case à cocher explicite, sous un avertissement qui dit exactement cela. Voir
`docs/01-architecture.md` § 1.6 ter pour le raisonnement complet, et pourquoi écrire un pilote
maison n'est pas une échappatoire.
