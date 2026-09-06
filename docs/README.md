# LDI12 Diagnostic : dossier de conception

Outil de diagnostic et de maintenance Windows pour les interventions de **Laguiole Dépannage Informatique**.
Compatible Windows 7 SP1 → Windows 11, x86 / x64 / ARM64 (émulé).

> **État : phase 0 terminée.** Le socle technique et la couche de compatibilité sont implémentés
> et testés ; voir [`../README.md`](../README.md) pour compiler et essayer. Les documents ci-dessous
> restent la référence de conception et sont mis à jour au fil des phases.

| Document | Contenu |
|---|---|
| [01 : Architecture technique](01-architecture.md) | Choix du runtime, structure des 9 projets, dépendances, capteurs, signature de code |
| [02 : Compatibilité Windows](02-compatibilite-windows.md) | Détection de plateforme, registre de fonctionnalités, matrice ✅/⚠️/❌, pièges JIT et localisation |
| [03 : Modèle de données](03-modele-donnees.md) | `Measured<T>`, `SystemSnapshot`, règles de modélisation, confidentialité, comparaison avant/après |
| [04 : Moteur de diagnostic](04-moteur-diagnostic.md) | Pipeline, sondes, règles, corrélations, séparation diagnostic / réparation |
| [05 : Système de scoring](05-scoring.md) | Barème explicite, plafonds, renormalisation, grand livre des pénalités, tests |
| [06 : UI et design system](06-ui-design-system.md) | Palette dérivée du site LDI12, contraintes de rendu WPF, composition des écrans, MVVM |
| [07 : Plan d'implémentation](07-plan-implementation.md) | 8 phases, livrables, critères de sortie, risques |
| [08 : Vérification des mises à jour](08-mises-a-jour.md) | Contrat de l'API ldi12.fr, signature du manifeste, pièges .NET Framework, étapes |

## Les six décisions structurantes

1. **.NET Framework 4.6.2, WPF, AnyCPU** : seule combinaison couvrant Win7 → Win11 avec un exécutable unique.
2. **Couche de compatibilité par sondage de capacité**, pas par numéro de version, avec un état ✅/⚠️/❌ et sa raison pour chaque fonctionnalité.
3. **`Measured<T>` partout** : une donnée absente dit pourquoi elle l'est, et ne coûte jamais de points au score.
4. **Sondes isolables dans un processus satellite** : un WMI bloqué ne peut pas figer l'application, et l'élévation ne concerne que ce qui l'exige.
5. **Diagnostic et réparation séparés par le typage** : une sonde ne peut pas modifier la machine.
6. **Scoring reconstructible ligne par ligne** : chaque point perdu a une ligne de justification dans le rapport.
