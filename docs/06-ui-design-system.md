# 06 : Architecture UI et design system

## 6.1 Reprendre l'identité du site LDI12

Le design system est dérivé directement de `ldi12` (Next.js + Tailwind). Les jetons sont repris tels quels pour que le logiciel et le site soient visiblement de la même maison.

### Palette : thème sombre (par défaut)

| Jeton | Valeur | Origine / usage |
|---|---|---|
| `Bg.Base` | `#0A0A0B` | fond de fenêtre (`body` du site) |
| `Surface` | `#0B0B0C` | zones de contenu |
| `Surface.Soft` | `#121214` | **cartes** |
| `Surface.Hard` | `#060607` | barre latérale, entêtes |
| `Border.Subtle` | `#FFFFFF` @ 6 % | bordure de carte (`border-white/5`) |
| `Border.Strong` | `#FFFFFF` @ 14 % | survol, séparateurs |
| `Text.Primary` | `#FFFFFF` |  |
| `Text.Secondary` | `#FFFFFF` @ 70 % |  |
| `Text.Tertiary` | `#FFFFFF` @ 50 % | libellés, unités |
| `Brand.Red` | `#ED2E36` | accent, état critique |
| `Brand.RedDark` | `#BE2129` | survol, texte rouge sur fond clair |
| `Brand.RedButton` | `#DE2028` | fond des boutons pleins (contraste 4,84:1) |

Le fichier `globals.css` du site documente déjà cette distinction de contraste entre l'accent et le fond de bouton : elle est reprise à l'identique.

### Palette d'état : le point délicat

Le rouge de marque **est** la couleur d'alerte. Pour éviter la confusion entre « accent LDI12 » et « problème critique », une règle stricte :

> **Le rouge ne sert qu'à deux choses : un état critique, ou l'action principale unique de l'écran.** Nulle part ailleurs.

| État | Sombre | Clair | Icône (jamais la couleur seule) |
|---|---|---|---|
| Bon | `#34D399` | `#059669` | cercle plein |
| Attention | `#F59E0B` | `#B45309` | triangle |
| Problème | `#F97316` | `#C2410C` | losange |
| Critique | `#ED2E36` | `#BE2129` | octogone |
| Information | `#60A5FA` | `#2563EB` | cercle « i » |
| Non disponible | `#6B7280` | `#9CA3AF` | cercle barré |

« Non disponible » est **gris et distinct**, jamais rouge : une information manquante n'est pas un problème de la machine. Chaque état porte une icône et un libellé en plus de la couleur : daltonisme, et lisibilité sur les écrans délavés qu'on rencontre en intervention.

### Thème clair

Mêmes clés, valeurs inversées : `Bg.Base #F7F7F8`, `Surface.Soft #FFFFFF`, bordures `#0000000F`, texte `#0B0B0C`. Le rouge de texte passe à `#BE2129` pour tenir le contraste AA. Bascule à chaud par échange de `ResourceDictionary`, préférence enregistrée, avec une option « suivre Windows » (clé `AppsUseLightTheme`, présente à partir de Win10 ; sur Win7/8.1 l'option est simplement masquée).

### Typographie

**Segoe UI**, présente de Windows 7 à Windows 11, et c'est déjà la police effective du site sur Windows (`system-ui`). Aucune police à embarquer, aucun risque de rendu. Échelle : 11 / 12 / 13 / 14 / 16 / 20 / 28 / 40. Chiffres des indicateurs en `Typography.NumeralAlignment="Tabular"` pour que les valeurs qui se rafraîchissent ne fassent pas sauter la mise en page.

### Formes et mouvement

- Rayons : cartes 14 px, boutons 9 px, badges 7 px.
- Élévation par la clarté, jamais par une ombre : bordure de carte en dégradé, plus claire en haut, éteinte en bas.
- Animations : 100–260 ms, décélération `PowerEase(EaseOut, 3)`. Le balayage de l'arc de score fait exception à 680 ms : c'est le seul mouvement que l'œil doit suivre plutôt que subir.

**Un bandeau de couleur ne se colle jamais au bord d'une carte.** Il devrait alors épouser le rayon *intérieur* de celle-ci (rayon extérieur moins l'épaisseur de bordure), et WPF rabote un rayon de 13 px sur un élément large de 4 : la barre se déforme en fuseau. Les pastilles de gravité sont posées en retrait, arrondies sur 2 px, même vocabulaire que la pastille de navigation.

**Toute largeur de texte plafonnée est explicitement calée à gauche.** WPF traite `HorizontalAlignment="Stretch"` comme `Center` dès qu'une `MaxWidth` empêche de remplir : un paragraphe plafonné se centre tout seul en fenêtre large. La même règle est utilisée volontairement pour centrer la colonne principale au-delà de 1440 px, et involontairement subie partout ailleurs si on ne l'écrit pas.

**Une barre supérieure ne se construit pas en `StackPanel` horizontal.** Chaque enfant y reçoit une largeur infinie : rien ne se tronque et tout déborde en fenêtre étroite. Grille, colonne élastique sur l'information sacrifiable, `TextTrimming` dessus.

### Bords de la zone de défilement : ne jamais utiliser `ScrollViewer.Padding`

**La réserve d'une zone défilante se porte sur le contenu, jamais sur le `ScrollViewer`.** Le gabarit du `ScrollViewer` applique sa marge intérieure comme *marge du présentateur* (`ScrollContentPresenter.Margin="{TemplateBinding Padding}"`). Le contenu est donc découpé aux bords du présentateur, c'est-à-dire **avant** le bord réel du cadre : on obtient une bande de fond nu de la hauteur de la réserve, au-dessus d'une carte tranchée. La coupure paraît prématurée parce qu'elle l'est.

```xml
<!-- Non : découpe 30 points trop tôt, laisse une bande de fond nu -->
<ScrollViewer Padding="28,30,28,36"><StackPanel>…</StackPanel></ScrollViewer>

<!-- Oui : la réserve défile avec le contenu, découpe au bord du cadre -->
<ScrollViewer><StackPanel Margin="28,30,28,36">…</StackPanel></ScrollViewer>
```

Le contenu se coupe alors net sous la barre supérieure et au-dessus de la barre d'état : comme dans Visual Studio Code ou les Paramètres de Windows 11. **Le bord franc n'est pas un défaut, c'est la limite assumée d'un cadre**, et il n'a pas besoin d'être adouci.

Deux adoucissements ont été essayés puis abandonnés, et leur échec vaut d'être noté :

- **Un dégradé de la couleur du fond posé par-dessus ne fond rien : il peint un voile.** Les cartes étant plus claires que le fond, il les assombrit au lieu de les effacer : une bande opaque en travers du contenu, exactement ce qu'on cherchait à supprimer.
- **Un masque d'opacité efface vraiment, et c'est le problème.** Il efface la carte et laisse voir le fond nu à sa place. Une carte à demi dissoute au bord n'est pas plus lisible qu'une carte coupée, seulement moins franche.

Les deux corrigeaient un symptôme dont la cause était ailleurs.

Le halo rouge du site reste en arrière-plan de la zone de contenu, sans plus rien pour le contrarier.

### Logotype

Le logotype (`Assets/logo.png`, dérivé de `logo.png` à la racine) remplace l'hexagone vectoriel. Le mot « LDI » y est blanc : invisible sur fond clair. Le thème clair fait donc apparaître derrière lui une plaque sombre arrondie (`Brush.LogoPlate`), que le thème sombre laisse transparente, une seule clé de jeton, aucune variante d'image à maintenir.

L'icône de l'exécutable (`Assets/ldi12.ico`) est générée depuis le même fichier en neuf résolutions de 16 à 256 points, charges utiles PNG : acceptées par Windows depuis Vista, donc sur toute la plage visée.

### Écran d'analyse

Pendant une collecte, la zone de contenu est **entièrement remplacée**. Ce n'est pas un écran d'attente :

- **Le plan des modules est affiché dès le départ**, tous en attente. Le technicien voit ce que l'outil va examiner avant qu'il l'ait examiné, ce qu'il doit de toute façon pouvoir dire au client qui regarde par-dessus son épaule.
- **Chaque tuile s'allume au moment où son module s'exécute réellement.** C'est ce qui a imposé de faire signaler au moteur le *démarrage* d'un module en plus de sa fin (`DiagnosticProgress.StartedProbe`). Sans cela, il aurait fallu deviner ce qui travaille à partir de l'ordre du catalogue et du plafond de parallélisme, c'est-à-dire donner pour certain quelque chose qu'on infère, ce que ce logiciel s'interdit partout ailleurs.
- **Le module annoncé sous l'anneau est le premier réellement en cours**, relu à chaque changement d'état. Mémoriser le dernier module démarré donnait un texte faux dès qu'il s'achevait avant ses voisins : quatre modules tournent de front.
- Trois ondes concentriques au trait, décalées dans le temps, donnent le mouvement de fond. Ce sont les seules animations continues de l'application, et elles suivent `Motion.Enabled`.
- L'anneau ne disparaît pas à la fin : il devient le score, balaie jusqu'à sa valeur et se dilate brièvement. La fin de l'analyse se lit comme la transformation de ce qu'on regardait déjà.

Une analyse annulée laisse les tuiles restées en attente telles quelles : on voit où elle s'est arrêtée.

### Mouvement : deux catégories, deux traitements

La distinction est structurante et vaut d'être posée avant d'ajouter la moindre animation.

| | Micro-interactions | Grande surface |
|---|---|---|
| Exemples | survol, pastille de navigation, chevron, enfoncement de bouton | ouverture de fenêtre, changement d'écran, balayage de l'arc, entrée en cascade |
| Surface repeinte | quelques dizaines de pixels | des milliers |
| Où | déclencheurs XAML | `LDI12.App.Motion` |
| En rendu logiciel | conservées | supprimées |

Couper *tout* mouvement en Tier 0 donne une interface qui paraît cassée, pas économe. Ce qui coûte, c'est la surface repeinte, pas le fait qu'il y ait mouvement.

Le mode capture (`--screenshot`) coupe aussi les animations de grande surface : une image prise au milieu d'un fondu ne prouve rien.

### Fenêtre sans cadre système

`WindowChrome`, et **jamais** `AllowsTransparency="True"` : une fenêtre transparente bascule tout le rendu WPF en logiciel, ce qui infligerait à toutes les machines le mode dégradé réservé aux plus faibles. Disponible depuis .NET Framework 4.0, donc sur Windows 7 comme sur Windows 11.

`CaptionHeight` vaut la hauteur de la barre supérieure : Windows continue de gérer le déplacement, le double-clic qui agrandit, l'accroche aux bords et le menu système. Tout élément cliquable de cette bande porte `WindowChrome.IsHitTestVisibleInChrome="True"`.

Deux comportements doivent être repris à la main :

1. **`WM_GETMINMAXINFO`.** Sans lui, la fenêtre agrandie recouvre la barre des tâches et déborde de la largeur des poignées de redimensionnement. On impose la zone de travail de l'écran **le plus proche** : WPF calcule sa borne supérieure sur le seul écran principal, et un portable branché sur un écran externe plus grand est le cas ordinaire en clientèle. Valeurs en pixels physiques des deux côtés : aucune conversion de mise à l'échelle ne doit être introduite.
2. **Angles arrondis Windows 11** (`DWMWA_WINDOW_CORNER_PREFERENCE`, build ≥ 22000), demandés explicitement.

Boutons de fenêtre aux dimensions Windows : 46 px de large, toute la hauteur de la barre, glyphes au trait d'1 px. Ce ne sont pas des icônes Lucide : le technicien les reconnaît sans les lire, les redessiner « à notre façon » coûterait une seconde à chaque fermeture.

Non retenu : **Snap Layouts** (survol du bouton Agrandir sous Windows 11) demande d'intercepter `WM_NCHITTEST` avant `WindowChrome`, et l'ordre des hooks n'est pas garanti. **L'ombre portée native** impose un cadre de verre DWM qui bave d'1 px en haut ; remplacée par un liseré d'1 px, prévisible de Windows 7 à 11.

## 6.2 Performance de rendu : les contraintes WPF sur vieux PC

Le point où une belle UI moderne tue les performances sur le matériel visé.

1. **`DropShadowEffect` et `BlurEffect` sont proscrits.** Ce sont des effets pixel-shader ; sur une machine en rendu logiciel (Tier 0), ils repeignent des zones entières à chaque frame. Les ombres sont dessinées avec des `Border` superposées à opacité dégressive, ou une bordure 1 px + un fond légèrement plus clair. Visuellement équivalent, coût nul.
2. **Pas de flou d'arrière-plan** (`backdrop-blur` du site) : impossible à faire proprement et à coût raisonnable en WPF. Remplacé par une opacité de surface.
3. **Détection du niveau de rendu** : `RenderCapability.Tier == 0` (rendu logiciel, fréquent en RDP, sur GPU sans pilote, sur machines virtuelles) → `Motion.Enabled = false` (animations de grande surface) et `Timeline.DesiredFrameRate = 10`. Les micro-interactions survivent, voir « Mouvement : deux catégories, deux traitements ». Les dégradés conservés sont au nombre de quatre (fond de barre latérale, bordure de carte, fondu de pied, halo rouge), tous statiques, aucun animé.
4. **Virtualisation obligatoire** sur toute liste : journaux d'événements, processus, pilotes, services. `VirtualizingStackPanel.IsVirtualizing=True`, `VirtualizationMode=Recycling`, `ScrollUnit=Pixel`. Une liste de 5 000 événements sans virtualisation gèle l'application pendant 20 secondes.
5. **Aucun binding sur une propriété qui change à haute fréquence sans throttle.** Les compteurs live sont échantillonnés à 1 Hz maximum et poussés vers l'UI par un agrégateur unique, pas par un timer par contrôle.
6. **Démarrage** : la fenêtre s'affiche en moins de 800 ms sur un Core 2 Duo. Rien de bloquant dans le constructeur de la fenêtre ; le diagnostic rapide démarre après le premier rendu.

## 6.3 Icônes

Le site utilise **lucide-react**. Les icônes Lucide sont des SVG au trait (MIT), directement convertibles en `Geometry` WPF :

```xml
<Path Data="{StaticResource Icon.HardDrive}" Stroke="{DynamicResource Text.Secondary}"
      StrokeThickness="2" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
      StrokeLineJoin="Round" Width="20" Height="20" Stretch="Uniform"/>
```

Un script de build convertit les ~60 icônes nécessaires en un `LucideIcons.xaml`. Résultat : rendu vectoriel net à tous les DPI, cohérence parfaite avec le site, zéro dépendance, zéro fichier image.

## 6.4 Composition de l'application

```
┌──────────────┬────────────────────────────────────────────────────────────┐
│  ▣ LDI12     │  DESKTOP-A7B2  ·  Windows 11 Pro 23H2 (22631.3155)  ·  x64 │
│  Diagnostic  │  🔒 Élevé    [ ⟳ Diagnostic complet ]                       │
├──────────────┼────────────────────────────────────────────────────────────┤
│ 🏠 Vue d'ens.│                                                            │
│ 🖥 Matériel  │                     ZONE DE CONTENU                        │
│ 💿 Stockage  │                                                            │
│ 🪟 Windows   │                                                            │
│ 🌐 Réseau    │                                                            │
│ 🛡 Sécurité  │                                                            │
│ 📊 Perform.  │                                                            │
│ 🧹 Maintenan.│                                                            │
│ 🔧 Outils    │                                                            │
│ 📄 Rapports  │                                                            │
├──────────────┤                                                            │
│ ⚙ Réglages   │                                                            │
└──────────────┴────────────────────────────────────────────────────────────┘
```

- Barre latérale 240 px, `Surface.Hard`, logo hexagonal LDI12 en haut, item actif marqué par une barre rouge 3 px à gauche + fond `#FFFFFF0A`. Repliable à 64 px (icônes seules) pour les écrans 1366×768, encore très courants sur le parc à dépanner.
- Barre supérieure : identité machine, badge d'élévation, action principale. C'est le seul bouton rouge plein de l'application.
- Chaque item de navigation porte une **pastille de sévérité** dès qu'un diagnostic a tourné : le technicien voit d'un coup d'œil où sont les problèmes sans ouvrir les onglets.

## 6.5 Les trois niveaux d'information

Traduction du principe *résumé → détail → données techniques* en un contrôle réutilisable, `DiagnosticCard` :

```
┌────────────────────────────────────────────────────────┐
│ 🟠  Stockage                            63/100    [ v ]│  ← niveau 1 : résumé
│     Disque système presque plein                       │     (toujours visible)
├────────────────────────────────────────────────────────┤
│  Samsung SSD 860 EVO 500 Go · SATA · SSD               │  ← niveau 2 : détail
│  C: 456,2 / 476,8 Go  ████████████████████░  95,7 %    │     (déplié au clic)
│  SMART : OK · 14 823 h · 2 875 démarrages · 38 °C      │
│  ⚠ 2 secteurs réalloués (seuil : 1)                    │
│                              [ Analyser ] [ Nettoyer ] │
├────────────────────────────────────────────────────────┤
│  ▸ Données techniques                                  │  ← niveau 3 : brut
│    Attributs SMART complets · provenance de chaque      │     (2e clic, pour
│    valeur · journal de collecte du module              │      le technicien)
└────────────────────────────────────────────────────────┘
```

Le niveau 3 affiche systématiquement la **provenance** (`Measured<T>.Source`) et le journal du module. C'est ce qui permet de répondre à « d'où sort ce chiffre ? », et c'est ce qui manque à tous les outils grand public.

## 6.6 Écran « Vue d'ensemble »

- **Jauge de score** : arc circulaire (un `Path` avec `ArcSegment`, coût de rendu négligeable), chiffre en 40 px tabulaire, libellé de bande, et sous-titre de confiance (« basé sur 48 contrôles sur 54 »).
- **Six tuiles de dimension** : nom, score, barre de progression fine colorée par bande, nombre de findings par sévérité. Cliquables vers l'écran correspondant.
- **Bloc « À traiter en priorité »** : les 3 à 5 recommandations en tête, avec leur corrélation en une phrase. C'est le vrai livrable de l'écran d'accueil, pas la liste des composants.
- **Bandeau de compatibilité** si `CompatibilityLevel != Full` : « Windows 7 SP1, 6 contrôles indisponibles sur cette version. Détails ». Le technicien sait immédiatement ce qu'il ne saura pas.

## 6.6 bis Écrans d'intervention : une grammaire, trois écrans

Réparations, Nettoyage et Outils partagent le même parcours, et chaque étape ajoute une information qui n'était pas là à l'étape précédente :

```
[ Prévisualiser ]  →  panneau neutre  →  [ Exécuter ]  →  panneau rouge  →  [ Confirmer ]
   rien n'est          ce qui va se        apparaît         répète ce qui       compte rendu
   modifié             passer,             seulement        va se passer        coloré par le
                       et ce qui           après le                             résultat réel
                       ne sera pas         relevé
                       touché
```

Trois règles tiennent l'ensemble :

1. **Le bouton d'exécution n'existe pas avant la prévisualisation.** Ce n'est pas une désactivation : il est absent de l'écran. Un bouton grisé invite à chercher comment l'activer ; un bouton absent fait lire ce qui est écrit au-dessus.
2. **La confirmation n'a pas la même apparence que le reste.** Panneau rouge, bordure pleine, question explicite. Il n'y a aucune raison qu'un clic qui supprime ressemble à un clic qui affiche.
3. **« Ce qui n'est pas touché » est une section à part entière**, en vert, dans chaque prévisualisation. C'est la question que pose le client, et la seule à laquelle le technicien doit pouvoir répondre sans supposer. Un test l'exige de toutes les actions.

**Ce que le nettoyage montre avant de supprimer :** par source, le nombre d'éléments, le volume, la conséquence réelle de la suppression, et la liste des chemins relevés : dépliable, bornée à deux cents lignes avec le total exact au-dessus. Quelques centaines de lignes suffisent à voir *de quoi* il s'agit ; ce qui compte est que rien ne soit supprimé qui ne figure pas dans le relevé, pas que le relevé tienne à l'écran.

**Une case cochée d'avance est une suppression automatique déguisée.** Les sources contenant des éléments produits par l'utilisateur (la corbeille) ne sont jamais présélectionnées, et portent une phrase rouge qui dit pourquoi.

**Un outil indisponible est expliqué, jamais seulement grisé.** La distinction qui compte pour le client : l'édition Famille de Windows ne donne pas accès à `gpedit.msc`, et ce n'est pas un symptôme de la machine qu'on lui répare. Un outil grisé sans phrase laisse exactement l'impression inverse.

**Le journal d'intervention se relit dans l'écran des rapports**, et non sur les écrans d'action. C'est au moment de remettre les documents qu'on veut relire l'intervention d'un seul tenant, et c'est là que le technicien vérifie que ce qu'il donne correspond à ce qu'il a fait.

## 6.7 MVVM et structure du projet UI

```
LDI12.App/
├── App.xaml(.cs)              composition root (MEDI), thème, gestion globale des exceptions
├── Themes/
│   ├── Tokens.Dark.xaml       couleurs et brosses
│   ├── Tokens.Light.xaml
│   ├── Typography.xaml
│   ├── LucideIcons.xaml       géométries générées
│   └── Controls/              Card, Badge, Button, Gauge, Sidebar, DataList, Expander…
├── Shell/                     MainWindow, ShellViewModel, NavigationService
├── Views/                     une vue par section (10)
├── ViewModels/                un VM par vue + VM de composants réutilisables
├── Controls/                  DiagnosticCard, ScoreGauge, SeverityBadge, MeasuredValue,
│                              CompatibilityBanner, ProgressOverlay
└── Converters/                Bytes→Go, ms→durée, Severity→Brush/Icon, Availability→visuel
```

- **MVVM** avec `CommunityToolkit.Mvvm`. Navigation *view-model first* : le shell expose `CurrentViewModel`, les `DataTemplate` du dictionnaire de ressources choisissent la vue. Ajouter un écran = un VM + un `DataTemplate`, sans toucher au shell.
- **Le snapshot immuable est la source de vérité de l'UI.** Les VM le projettent, ne le modifient jamais. Un nouveau diagnostic remplace l'objet entier : pas de synchronisation partielle, pas d'état incohérent à l'écran.
- **`MeasuredValue`**, contrôle unique pour afficher un `Measured<T>` : valeur formatée, ou badge d'indisponibilité avec info-bulle expliquant la raison. Il est utilisé partout : c'est ce qui rend les ✅ / ⚠️ / ❌ homogènes dans toute l'application sans effort par écran.
- **Gestion globale des exceptions** : `DispatcherUnhandledException` + `AppDomain.UnhandledException` → journalisation, boîte de dialogue expliquée, et l'application reste utilisable si l'erreur est confinée à une vue.
