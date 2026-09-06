# 05 : Système de scoring

Exigence du cahier des charges : *« Ne jamais inventer un score arbitraire. Chaque perte de points doit pouvoir être expliquée. »* Le modèle ci-dessous est conçu pour que le score soit **reconstructible ligne par ligne** dans le rapport.

## 5.1 Modèle

Six dimensions, chacune partant de 100 points, chacune diminuée par les pénalités portées par les findings.

| Dimension | Poids | Contenu |
|---|---|---|
| Matériel | 15 % | CPU, RAM, GPU, carte mère, batterie |
| Stockage | 25 % | SMART, espace, température, type de média |
| Windows | 25 % | intégrité système, mises à jour, services, événements, pilotes |
| Sécurité | 15 % | antivirus, pare-feu, UAC, comptes, exposition |
| Réseau | 10 % | adaptateurs, connectivité, DNS, qualité |
| Performances | 10 % | charge, démarrage, processus |

Le stockage et Windows pèsent le plus parce que ce sont statistiquement les deux causes majoritaires d'intervention. Les poids sont dans `scoring.profile.json` et modifiables.

## 5.2 Calcul

```
1. dimension.Score = 100 − Σ(pénalités de la dimension), borné à [0, 100]
2. Plafonnement par sévérité (appliqué après la somme) :
      un finding Critical      → dimension plafonnée à 40
      un finding Problem       → dimension plafonnée à 70
3. Plafonnement par famille de règles : une même cause ne peut pas coûter deux fois
      (ex. « espace disque » : maximum 30 points cumulés, quel que soit le nombre de volumes)
4. Renormalisation : les dimensions NotEvaluated sont exclues et les poids
   des dimensions restantes sont renormalisés à 100 %
5. Score global = Σ(dimension.Score × poids renormalisé)
6. Plafond global : au moins un Critical → score global plafonné à 60
```

L'étape **2** est essentielle : sans elle, un disque en train de mourir (dimension Stockage à 20) donnerait un global de 82/100 sur une machine par ailleurs saine. Un score qui rassure alors que les données sont en danger est pire que pas de score du tout.

L'étape **4** est la traduction du principe de compatibilité dans le scoring : **une donnée non mesurable ne coûte jamais de points.** Sur un Windows 7 où le SMART NVMe est inaccessible, la dimension Stockage n'est pas notée 0 : elle est notée sur ce qui a pu être mesuré, avec un indicateur de confiance réduit. Pénaliser l'absence d'information reviendrait à sanctionner la machine pour une limite de notre outil.

## 5.3 Confiance

```csharp
public sealed class DimensionScore
{
    public DiagnosticCategory Dimension { get; }
    public int? Score { get; }                        // null = NotEvaluated
    public double Weight { get; }
    public Confidence Confidence { get; }             // Full | Partial | Low
    public int EvaluatedRules { get; }
    public int SkippedRules { get; }
    public IReadOnlyList<ScorePenalty> Ledger { get; } // le détail, ligne par ligne
}
```

`Confidence` = part des règles de la dimension effectivement évaluées. L'UI affiche « Stockage 63/100 · basé sur 7 contrôles sur 11 » plutôt qu'un chiffre faussement absolu. C'est de l'honnêteté intellectuelle, et c'est aussi commercialement plus solide : on ne promet pas ce qu'on n'a pas mesuré.

## 5.4 Le grand livre des pénalités

```csharp
public sealed class ScorePenalty
{
    public FindingId Finding { get; }
    public DiagnosticCategory Dimension { get; }
    public int Points { get; }
    public string Reason { get; }        // "Volume système à 4,3 % libre (seuil critique : 5 %)"
    public int PointsBeforeCap { get; }  // avant plafonnement de famille
    public string? CapApplied { get; }
}
```

Le rapport technicien contient une section « Détail du score » qui est littéralement l'impression de ce registre :

```
Stockage ................................................ 63 / 100
  − 25   STO-001  Volume système à 4,3 % libre (seuil critique : 5 %)
  − 12   STO-004  2 secteurs réalloués détectés (seuil d'alerte : 1)
   −0    STO-007  Température disque non mesurable (non pénalisé)
  plafond Critical appliqué : aucun
  confiance : partielle, 7 contrôles sur 11 (SMART NVMe indisponible sous Windows 7)
```

Aucun chiffre ne peut apparaître sans sa ligne de justification. Si une ligne manque, c'est un bug, et un test le détecte : **`Σ pénalités + score final = 100` doit être vérifié pour chaque dimension** dans un test unitaire, sur chaque fixture.

## 5.5 Bandes et vocabulaire

| Score | Libellé | Couleur | Message technicien |
|---|---|---|---|
| 90–100 | Excellent | vert | Aucune action requise |
| 75–89 | Bon | vert | Points de surveillance mineurs |
| 60–74 | Attention | ambre | Interventions recommandées |
| 40–59 | Dégradé | orange | Interventions nécessaires |
| 0–39 | Critique | rouge | Action immédiate, risque de perte de données |

Le libellé est toujours affiché **à côté** du chiffre. Un « 72 » seul ne veut rien dire pour un client ; « 72 / 100, Attention » se comprend immédiatement.

## 5.6 Versionnement du profil de scoring

`ScoringProfileVersion` est enregistré dans chaque snapshot. Sans lui, une comparaison avant/après entre deux versions du logiciel serait mensongère (le score aurait changé parce que le barème a changé, pas la machine). Le comparateur refuse (ou signale explicitement), un diff entre deux profils différents.

## 5.7 Test du barème

Le scoring est la partie du logiciel la plus facile à casser silencieusement. Trois familles de tests :

1. **Fixtures réelles** : des snapshots JSON capturés sur les six VM de la matrice de test, avec le score attendu figé. Toute évolution de règle qui déplace un score fait échouer le test : la modification doit alors être délibérée et le fichier de référence mis à jour explicitement.
2. **Cas limites synthétiques** : machine parfaite (100), machine avec un seul Critical (≤ 60), toutes dimensions NotEvaluated (score `null`, pas 0), pénalités dépassant 100 (borné à 0, jamais négatif).
3. **Invariants** : somme du registre cohérente, aucune pénalité orpheline sans finding, aucun finding sans pénalité déclarée (même à 0), déterminisme (deux évaluations du même snapshot donnent le même score, au bit près).
