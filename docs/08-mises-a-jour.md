# 08 : Vérification des mises à jour

> **État : livré en version 1.21.0.** Vérification signée, téléchargement
> contrôlé par empreinte, remplacement sur place et relance : éprouvés de bout
> en bout depuis un dossier isolé tenant lieu de clé USB.
> La partie serveur **existe et répond**, mais n'est **pas encore déployée** :
> au 6 septembre 2026, `ldi12.fr/api/updates/v1/…` et `/logiciels` répondent 404
> sur le site en ligne. Tout ce qui suit a été vérifié contre le serveur lancé
> en local.

## 8.0 Ce que la mise en œuvre a changé au plan

Cinq écarts, tous assumés, et la raison de chacun.

**Le remplacement est automatique.** Le plan disait « rien ne s'installe tout
seul » ; la décision a été prise autrement. Une version ordinaire est proposée 
(le technicien prend, remet à plus tard, ou écarte) mais dès qu'il accepte, le
logiciel se remplace et redémarre seul. Une version marquée obligatoire s'installe
sans rien demander, et passe outre un « ignorer cette version » : un outil de
diagnostic qui affiche un constat faux est pire qu'un outil absent.

**Il n'y a pas de fichier de commandes.** La méthode habituelle (un `.cmd` déposé
dans le dossier temporaire, qui attend puis recopie) a deux défauts : un fichier
de commandes se lit dans la page de codes du terminal, donc un chemin contenant un
accent casse tout ; et le morceau de code qui remplace l'exécutable n'est vérifié
par personne. Ici, c'est **la nouvelle version elle-même**, dont l'empreinte vient
d'être contrôlée, qu'on relance avec `--appliquer-maj` : elle attend la fin de
l'ancienne, se recopie à sa place, et redémarre.

**Le réglage voyage avec la clé.** Il est écrit dans `ldi12-maj.json`, à côté de
l'exécutable quand on peut y écrire, et seulement sinon dans le profil de
l'utilisateur. C'est le seul réglage du logiciel dans ce cas, et il le doit : un
technicien qui a répondu une fois ne doit pas se voir reposer la question sur
chacun des postes où il branche sa clé, et un refus doit rester un refus partout.

**Le téléchargement se dépose sur la clé.** Dans un dossier caché `.ldi12-maj`
voisin de l'exécutable, et non dans le dossier temporaire du client : douze
mégaoctets laissés sur un poste qui ne nous appartient pas seraient une trace de
plus. Le dossier est effacé au démarrage suivant, une fois la copie faite.

**Une adresse de serveur de rechange existe** (`LDI12_UPDATE_ENDPOINT`). Elle ne
peut pas servir à installer autre chose : le manifeste doit rester signé par la
clé compilée dans l'exécutable, et le fichier porter l'empreinte annoncée. Elle
sert à éprouver la chaîne avant le déploiement, et permettra à un parc d'entreprise
de servir ses postes depuis son propre miroir.

## 8.0 bis : trois pièges rencontrés, qu'aucun plan ne prévoyait

**`DispatcherPriority.ApplicationIdle` ne se déclenche jamais pendant une analyse.**
La vérification y était postée : elle n'est jamais partie, sur aucune machine, et
rien ne le signalait. L'interface se rafraîchit en continu pendant les sept
secondes d'une analyse rapide, et la file d'attente au repos n'est jamais servie.
En `Background`, elle passe.

**Un bouton lié à une commande garde le résultat de `CanExecute` évalué à la
construction.** Le bandeau se construit alors que son état est encore `Hidden` :
sans `RaiseCanExecuteChanged()` au changement d'état, « Mettre à jour » reste grisé
pour toujours. Constaté à l'écran, pas déduit.

**Un message qui promet doit être tenu.** La garde « une analyse est en cours : la
mise à jour sera posée dès qu'elle sera terminée » existait avant le code qui la
tient. `Resume()`, appelé à la fin de chaque analyse, la rend vraie.

## 8.1 Les cinq règles

Le logiciel est hors ligne par construction, et cela ne doit pas changer. La
vérification de mise à jour est donc la **seule** fonction qui touche au réseau
en dehors des sondes réseau, et elle obéit à cinq règles qui ne bougeront pas.

1. **Rien ne part sans un accord.** La vérification est déclenchée par le
   technicien, ou par un réglage qu'il a coché lui-même. Un logiciel lancé
   depuis une clé USB chez un client ne doit pas décider seul de sortir sur
   Internet.
2. **Rien d'identifiant ne part.** La requête ne transmet que le numéro de
   version installée, le canal, l'architecture et la famille de Windows. Pas de
   nom de machine, pas d'identifiant d'installation, pas de relevé. Le serveur
   n'enregistre que des compteurs agrégés par jour, jamais l'adresse IP.
3. **L'absence de réseau n'est pas une erreur.** Chez un client dont la box est
   en panne (c'est-à-dire précisément là où le logiciel sert), l'échec doit
   s'afficher comme une information discrète, jamais comme un incident.
4. **Rien ne s'installe sans être montré.** Le logiciel annonce la version, ce
   qu'elle change, et attend un accord, sauf lorsqu'elle corrige un défaut qui
   rend la version installée peu fiable, auquel cas elle se pose sans demander,
   en le disant à l'écran pendant qu'elle le fait. Il n'y a ni service, ni tâche
   planifiée, ni installation : le logiciel reste un fichier unique, qui se
   remplace lui-même et redémarre. *(Cette règle a changé depuis le plan
   d'origine, voir 8.0.)*
5. **La confiance vient de la clé, pas du transport.** Le manifeste est signé ;
   le fichier téléchargé est vérifié par son empreinte. Un réseau d'entreprise
   qui déchiffre le trafic ne peut ni annoncer une fausse version, ni glisser un
   autre exécutable.

## 8.2 Le contrat serveur

### Vérifier

```
GET https://ldi12.fr/api/updates/v1/ldi12-diagnostic
      ?version=1.19.0      version installée (facultatif, sert à updateAvailable)
      &channel=stable      stable | beta
      &arch=x64            x86 | x64 | arm64 | any
      &os=win11            win7 | win8 | win10 | win11 | winserver
```

Réponse `200 application/json` :

```json
{
  "product": "ldi12-diagnostic",
  "name": "LDI12 Diagnostic",
  "channel": "stable",
  "checkedAt": "2026-09-06T13:40:12.004Z",
  "current": "1.19.0",
  "updateAvailable": true,
  "latest": {
    "version": "1.20.0",
    "title": "Ports en écoute et réparations réseau",
    "summary": "Une phrase.",
    "highlights": ["Trois points au maximum", "…"],
    "releasedAt": "2026-09-20T09:00:00.000Z",
    "mandatory": false,
    "minOs": "6.1",
    "notesUrl": "https://ldi12.fr/logiciels/ldi12-diagnostic/versions#v1-20-0",
    "pageUrl": "https://ldi12.fr/logiciels/ldi12-diagnostic",
    "download": {
      "url": "https://ldi12.fr/telecharger/ldi12-diagnostic/1.20.0/LDI12-Diagnostic-1.20.0.exe",
      "filename": "LDI12-Diagnostic-1.20.0.exe",
      "size": 12710400,
      "sha256": "d5c9f0…",
      "kind": "portable",
      "arch": "x64",
      "signed": false
    }
  },
  "signature": {
    "alg": "RS256",
    "keyId": "753c6075c5f3df96",
    "value": "<signature en base64>",
    "payload": "<octets signés, en base64>"
  }
}
```

Autres réponses possibles : `404` (logiciel inconnu ou diffusion coupée depuis
l'administration), `429` (plus de soixante vérifications par minute et par
adresse). `latest` vaut `null` tant qu'aucune version n'est publiée.

La réponse est mise en cache cinq minutes. Un parc entier qui démarre le lundi
matin n'interroge donc la base qu'une fois.

### Récupérer la clé publique

```
GET https://ldi12.fr/api/updates/v1/public-key            # PEM, avec le keyId en commentaire
GET https://ldi12.fr/api/updates/v1/public-key?format=json
```

**Cette adresse sert à récupérer la clé une fois, au moment d'écrire le code.**
Elle ne doit jamais servir à vérifier une signature à l'exécution : qui peut
servir un faux manifeste peut servir une fausse clé. La clé qui fait foi est
celle compilée dans l'exécutable.

## 8.3 Vérifier la signature : et les trois pièges .NET Framework

Le champ `signature.payload` porte **les octets exacts qui ont été signés**,
en base64. C'est délibéré : le client n'a pas à reproduire une sérialisation
canonique, exercice où une virgule ou un ordre de clés fait échouer la
vérification pour rien. La marche à suivre est donc :

1. décoder `signature.payload` (base64) → tableau d'octets ;
2. vérifier `signature.value` sur **ces octets-là**, avec la clé publique
   embarquée, en RSA-2048 / SHA-256 / PKCS#1 v1.5 ;
3. **si et seulement si** la vérification passe, analyser ce tableau d'octets
   comme du JSON UTF-8 et travailler sur ce résultat. Les champs lisibles du
   corps de la réponse sont là pour l'inspection humaine, pas pour la décision ;
4. si `signature.keyId` ne correspond pas à la clé embarquée, le dire
   explicitement : « cette version du logiciel ne connaît pas la clé du
   serveur » est un message autrement plus utile que « signature invalide ».

**Piège 1 : `ImportSubjectPublicKeyInfo` n'existe pas en .NET Framework.** Elle
est arrivée avec .NET Core 3.0. La clé publique doit donc être embarquée sous
forme de `RSAParameters` : modulus et exponent en base64, en constantes du code.

```csharp
private static RSAParameters PublicKey() => new RSAParameters
{
    Modulus  = Convert.FromBase64String("…"),   // 256 octets
    Exponent = new byte[] { 0x01, 0x00, 0x01 }, // 65537
};
```

**Piège 2 : `RSACryptoServiceProvider` ne sait pas faire de SHA-256 par
défaut.** Importé sans précaution, il utilise le fournisseur `PROV_RSA_FULL`,
qui ne connaît que SHA-1 ; `VerifyData(..., "SHA256", ...)` échoue alors sur une
exception peu parlante. Deux sorties : construire avec
`new CspParameters(24)` (`PROV_RSA_AES`), ou utiliser `RSACng`, disponible
depuis .NET Framework 4.6 et présent de Vista à Windows 11. `RSACng` est le
choix recommandé, avec repli sur le CSP si la construction échoue.

**Piège 3 : TLS 1.2 sur Windows 7.** Déjà traité deux fois dans le code
(`KlarviPublisher` ligne ~200, `NetworkTestsProbe` ligne ~258) : le même geste
est nécessaire ici.

```csharp
ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // Tls12
```

Sans lui, la requête part en TLS 1.0 et le serveur la refuse : panne qui
n'apparaîtrait que sur les vieilles machines, c'est-à-dire chez les clients.

## 8.4 Ce qu'il faut écrire

Le modèle existe déjà dans le dépôt : `LDI12.Publishing.Klarvi` isole un
contrat externe derrière une interface, injecte son `HttpMessageHandler` pour
les tests, et explique ses échecs en français. La mise à jour suit la même
forme.

> **Écrit, et à ces emplacements.** Le tableau ci-dessous a servi de plan ; les
> chemins réels ne s'en écartent que sur deux points : le projet réseau s'appelle
> `LDI12.Updates`, et l'installateur y a rejoint le téléchargeur.

| Fichier | Rôle |
|---|---|
| `src/LDI12.Core/Updates/UpdateManifest.cs` | Le manifeste et son `LatestRelease`, en objets immuables. Aucune logique. |
| `src/LDI12.Core/Updates/IUpdateChecker.cs` | `Task<Measured<UpdateManifest>> CheckAsync(CancellationToken)`. `Measured<T>` porte déjà « absent, et voici pourquoi » : c'est exactement ce qu'il faut pour un réseau coupé. |
| `src/LDI12.Core/Updates/UpdateSignature.cs` | Clé publique embarquée, `keyId`, vérification. Sans dépendance réseau, donc testable seul. |
| `src/LDI12.Updates/HttpUpdateChecker.cs` | Implémentation : requête, délai court, vérification de signature, analyse du JSON signé. `HttpMessageHandler` injectable. |
| `src/LDI12.Updates/UpdateDownloader.cs` | Téléchargement du fichier vers `%TEMP%`, contrôle du SHA-256 annoncé, ouverture de l'explorateur sur le fichier. Rien de plus. |
| `src/LDI12.App/ViewModels/UpdateViewModel.cs` | État affiché : à jour, mise à jour disponible, hors ligne, refusée. |
| `src/LDI12.App/Views/UpdateBanner.xaml` | Bandeau discret, et sa fenêtre de détail. |

Un projet séparé pour la partie réseau, comme pour Klarvi : la vérification de
signature reste dans `Core` (elle n'a pas de dépendance) mais rien de ce qui
parle HTTP ne doit entrer dans le noyau.

### Réglages persistés

Trois clés à ajouter dans `SettingsService`, toutes à `false` ou vides par
défaut :

- `Updates.CheckOnStartup` (bool, **défaut `false`**) : proposé une fois, à la
  première ouverture des réglages, jamais activé d'office ;
- `Updates.Channel` (`stable` | `beta`, défaut `stable`) ;
- `Updates.LastCheckUtc` (date) : pour ne pas vérifier plus d'une fois par jour
  quand la vérification au démarrage est active ;
- `Updates.SkippedVersion` (chaîne) : la version que le technicien a explicitement
  écartée ; on ne la lui repropose pas, sauf si elle est marquée obligatoire.

### Comportement attendu

| Situation | Ce que fait le logiciel |
|---|---|
| Pas de réseau, DNS muet, délai dépassé | Ligne grise « vérification impossible, pas de connexion ». Aucune fenêtre, aucun journal alarmiste. |
| `updateAvailable: false` | « Vous avez la dernière version (1.19.0). » |
| Version disponible | Bandeau avec le numéro, les trois points marquants, un lien vers les notes, un bouton « Télécharger ». |
| `mandatory: true` | Même bandeau, en teinte d'alerte, et le report n'est pas mémorisé : la question revient à chaque lancement. |
| `minOs` supérieur à la machine | « La version 1.20 demande Windows 10 ; la 1.19 reste la dernière disponible pour cette machine. » Pas de proposition de téléchargement. |
| Signature invalide, `keyId` inconnu | Rien n'est proposé. Message explicite, et l'incident est journalisé : c'est le seul cas qui mérite de l'être. |
| Empreinte du fichier téléchargé différente | Le fichier est effacé, le téléchargement est déclaré échoué. |

Le délai réseau est court, **cinq secondes**, une seule tentative. Une
vérification de mise à jour n'a pas à faire attendre qui que ce soit.

## 8.5 Étapes

**Étape 1 : le contrat, hors réseau.** `UpdateManifest`, `UpdateSignature`, et
des tests sur un manifeste réel enregistré dans les données de test : signature
valide acceptée, signature falsifiée refusée, `keyId` inconnu distingué d'une
signature fausse.
*Critère de sortie :* les tests passent sans qu'aucune requête ne parte.

**Étape 2 : la requête.** `HttpUpdateChecker` avec `HttpMessageHandler`
injecté. Tests : réponse normale, 404, 429, délai dépassé, JSON tronqué, corps
vide. Aucun de ces cas ne doit remonter une exception à l'interface : tous
passent par `Measured<T>`.
*Critère de sortie :* couverture des six cas, et une vérification réelle contre
`https://ldi12.fr/api/updates/v1/ldi12-diagnostic` depuis une machine Windows 7.

**Étape 3 : l'interface.** Bandeau, fenêtre de détail, réglage d'activation.
*Critère de sortie :* la vérification manuelle fonctionne, et le logiciel se
comporte à l'identique (sans bandeau ni ralentissement) quand le réglage est
décoché.

**Étape 4 : le téléchargement assisté.** `UpdateDownloader`, contrôle de
l'empreinte, ouverture de l'explorateur sur le fichier obtenu.
*Critère de sortie :* une empreinte falsifiée à la main fait échouer le
téléchargement et efface le fichier.

**Étape 5 : la matrice.** Vérification sur les six machines, Windows 7 SP1 x86
en premier : c'est là que TLS 1.2 se manifeste.

## 8.6 Ce que ce plan ne prévoit pas

- **Aucune installation automatique.** Ni service, ni tâche planifiée, ni
  remplacement de l'exécutable en cours d'exécution. Le logiciel est un fichier
  qu'on copie ; il le reste.
- **Aucun compte, aucune licence.** Le logiciel est gratuit et le restera : rien
  à activer, rien à vérifier côté serveur.
- **Aucune télémétrie d'usage.** Le serveur compte des vérifications, pas des
  utilisateurs. Ajouter un identifiant d'installation, même anonyme, changerait
  la nature de l'échange et obligerait à en parler dans la politique de
  confidentialité : ce n'est pas prévu.

## 8.7 Côté serveur, ce qui existe déjà

Rien à faire de ce côté ; c'est décrit ici pour que le contrat soit lisible d'un
seul endroit.

| Adresse | Contenu |
|---|---|
| `https://ldi12.fr/logiciels` | Liste des logiciels gratuits |
| `https://ldi12.fr/logiciels/ldi12-diagnostic` | Fiche : captures, points forts, configuration requise, FAQ, téléchargement avec empreinte |
| `https://ldi12.fr/logiciels/ldi12-diagnostic/versions` | Historique des versions, une ancre par version (`#v1-19-0`) |
| `https://ldi12.fr/logiciels/ldi12-diagnostic/versions.rss` | Flux des sorties |
| `https://ldi12.fr/telecharger/ldi12-diagnostic` | Dernière version stable, reprise de téléchargement gérée |
| `https://ldi12.fr/telecharger/ldi12-diagnostic/1.19.0/<fichier>` | Une version précise |
| `https://ldi12.fr/api/updates/v1/ldi12-diagnostic` | Manifeste signé |
| `https://ldi12.fr/api/updates/v1/public-key` | Clé publique de vérification |

La publication d'une version se fait depuis `admin.ldi12.fr` : on dépose
l'exécutable produit par `publish.ps1`, l'empreinte est recalculée côté serveur
et comparée au fichier `.sha256` déposé à côté, puis la version est publiée. La
fiche publique, l'historique, le flux et le manifeste suivent immédiatement.

**Le jour où le certificat de signature de code arrivera**, il restera à cocher
« fichier signé numériquement » au dépôt : la fiche cessera alors d'afficher
l'avertissement SmartScreen, et le manifeste passera `signed: true`.
