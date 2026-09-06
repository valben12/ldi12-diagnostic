using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// L'état des profils, et ce que le client décrit sans savoir le nommer.
    /// </summary>
    /// <remarks>
    /// <b>Une panne de profil ne ressemble jamais à une panne.</b> La machine démarre, Windows
    /// s'ouvre, tout va bien, sauf que le bureau est vide, ou que le travail de la journée
    /// disparaît au redémarrage. Rien dans les journaux, rien dans le matériel, rien dans le
    /// réseau. C'est la catégorie de problème où un client est le plus démuni, et où un relevé
    /// change le plus de choses.
    /// </remarks>
    internal static class ProfileRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Windows;

        /// <summary>Un profil provisoire et un profil mis de côté sont les deux moitiés d'un même incident.</summary>
        private const string Family = "windows.profiles";

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("PRO-001", "Profil provisoire", Cat, Temporary, Family);
            yield return new Rule("PRO-002", "Profil mis de côté", Cat, SetAside, Family);
            yield return new Rule("PRO-003", "Profils sans dossier", Cat, Orphaned);
            yield return new Rule("PRO-004", "Sessions ouvertes", Cat, OpenSessions);
            yield return new Rule("PRO-005", "Profils inutilisés", Cat, Unused);
        }

        /// <summary>
        /// La session en cours travaille dans un profil provisoire.
        /// </summary>
        /// <remarks>
        /// <b>Le constat le plus grave que ce logiciel puisse produire sans qu'aucun matériel soit
        /// en cause.</b> Tout ce que l'utilisateur enregistre disparaît à la fermeture de session,
        /// et rien ne le lui dit à part une notification qu'il a fermée trois semaines plus tôt.
        /// Chaque jour qui passe est une journée de travail perdue, et il continuera de croire
        /// que « l'ordinateur efface ses fichiers ».
        /// <para>
        /// L'urgence est double : arrêter la perte, et ne surtout pas fermer la session avant
        /// d'avoir mis à l'abri ce qui s'y trouve.
        /// </para>
        /// </remarks>
        private static RuleResult Temporary(RuleContext c)
        {
            var profiles = c.System.Profiles.Profiles;
            if (profiles.Count == 0)
                return RuleResult.NotEvaluated("Aucun profil utilisateur n'a été relevé.");

            var findings = new List<Finding>();

            foreach (var profile in profiles)
            {
                if (profile.State != ProfileState.Temporary) continue;

                var who = profile.AccountName.Or(profile.Sid);

                findings.Add(c.Finding(Severity.Critical,
                    profile.IsCurrent
                        ? "Cette session travaille dans un profil provisoire"
                        : "Un compte de cette machine ouvre ses sessions dans un profil provisoire",
                    who + " : profil provisoire en « " + profile.Path + " »" +
                    (profile.IsCurrent ? ", session en cours" : string.Empty) + ".",
                    "Windows n'a pas réussi à charger le profil de ce compte et lui en a donné un provisoire. " +
                    "Tout ce qui y est enregistré (documents, courriels, mots de passe du navigateur, réglages) " +
                    "est effacé à la fermeture de la session. Les données d'origine, elles, n'ont pas bougé : " +
                    "elles sont dans l'ancien dossier du profil. Avant toute chose, sauvegarder ce qui se trouve " +
                    "dans la session en cours, et ne pas redémarrer sans l'avoir fait.",
                    subject: who,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Dossier de la session", profile.Path, DataSource.Registry),
                        Evidence.Of("Compte", who, DataSource.NativeApi)),
                    recommendations: RuleContext.Rec(Rec.BackupNow, Rec.RepairUserProfile)));
            }

            return RuleResult.Of(findings);
        }

        /// <summary>
        /// Windows a mis un profil de côté et en a créé un neuf.
        /// </summary>
        /// <remarks>
        /// C'est le « j'ai tout perdu » le plus fréquent, et le plus facile à rassurer : le
        /// dossier d'origine est intact à côté du nouveau. Le constat existe surtout pour dire
        /// cela : un technicien qui ne connaît pas le mécanisme cherche une restauration là où
        /// il suffit de recopier.
        /// </remarks>
        private static RuleResult SetAside(RuleContext c)
        {
            var profiles = c.System.Profiles.Profiles;
            if (profiles.Count == 0)
                return RuleResult.NotEvaluated("Aucun profil utilisateur n'a été relevé.");

            var findings = new List<Finding>();

            foreach (var profile in profiles)
            {
                if (profile.State != ProfileState.SetAside) continue;

                var who = profile.AccountName.Or(profile.Sid);
                var kept = profile.FolderExists.Or(false);

                findings.Add(c.Finding(kept ? Severity.Warning : Severity.Problem,
                    "Windows a mis un profil de côté et en a ouvert un neuf",
                    who + " : ancien profil en « " + profile.Path + " », " +
                    (kept ? "dossier toujours présent" : "dossier absent") + ".",
                    kept
                        ? "Windows n'a pas su charger ce profil lors d'une ouverture de session et en a créé un " +
                          "autre à la place. C'est ce que l'utilisateur décrit par « j'ai tout perdu » : bureau " +
                          "vide, documents introuvables, favoris disparus. Rien n'est perdu : l'ancien dossier " +
                          "est intact et son contenu se recopie dans le nouveau profil."
                        : "Windows a mis ce profil de côté, et son dossier n'existe plus. Cette fois les données " +
                          "ne sont pas récupérables depuis le profil lui-même : il faut se tourner vers une " +
                          "sauvegarde ou un point de restauration.",
                    subject: who,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Ancien dossier", profile.Path, DataSource.Registry),
                        Evidence.Of("Dossier présent", kept ? "oui" : "non", DataSource.FileSystem)),
                    recommendations: RuleContext.Rec(Rec.RepairUserProfile)));
            }

            return RuleResult.Of(findings);
        }

        /// <summary>
        /// Des profils dont le dossier n'existe plus.
        /// </summary>
        /// <remarks>
        /// Sans conséquence pour l'utilisateur, et utile au technicien : c'est la trace d'un
        /// compte supprimé à moitié, et l'explication d'une liste de comptes plus longue à
        /// l'écran de connexion que dans la réalité.
        /// </remarks>
        private static RuleResult Orphaned(RuleContext c)
        {
            var profiles = c.System.Profiles.Profiles;
            if (profiles.Count == 0)
                return RuleResult.NotEvaluated("Aucun profil utilisateur n'a été relevé.");

            var names = new List<string>();
            foreach (var profile in profiles)
                if (profile.State == ProfileState.Orphaned)
                    names.Add(profile.AccountName.Or(profile.Sid));

            if (names.Count == 0) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                "Des profils sont déclarés sans avoir de dossier",
                names.Count + " profil(s) sans dossier : " + string.Join(", ", names) + ".",
                "Windows garde la trace de ces profils alors que leur dossier a été supprimé. Ce n'est pas " +
                "une panne et cela ne gêne personne au quotidien : c'est la trace de comptes effacés à " +
                "moitié, et cela explique une liste de comptes plus longue qu'attendu.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Profils sans dossier", names.Count.ToString(), DataSource.Registry))));
        }

        /// <summary>
        /// Plusieurs sessions ouvertes en même temps.
        /// </summary>
        /// <remarks>
        /// Une session verrouillée reste une session ouverte : sa mémoire est occupée, ses
        /// programmes tournent. C'est la réponse la moins chère à « la machine rame alors que
        /// personne ne s'en sert », et elle ne coûte rien à vérifier.
        /// </remarks>
        private static RuleResult OpenSessions(RuleContext c)
        {
            var loaded = c.System.Profiles.LoadedCount;
            if (!loaded.IsReliable)
                return RuleResult.NotEvaluated(loaded.Reason ?? "Les sessions ouvertes n'ont pas pu être comptées.");

            if (loaded.Value <= 1) return RuleResult.Clean;

            var others = new List<string>();
            foreach (var profile in c.System.Profiles.Profiles)
                if (profile.Loaded && !profile.IsCurrent)
                    others.Add(profile.AccountName.Or(profile.Sid));

            return RuleResult.Of(c.Finding(Severity.Info,
                "Plusieurs sessions sont ouvertes sur cette machine",
                loaded.Value + " session(s) ouverte(s)" +
                (others.Count > 0 ? ", dont : " + string.Join(", ", others) : string.Empty) + ".",
                "D'autres comptes ont une session ouverte, verrouillée ou simplement laissée de côté. Une " +
                "session verrouillée n'est pas une session fermée : sa mémoire reste occupée et ses programmes " +
                "continuent de tourner. C'est l'explication la plus fréquente d'une machine partagée qui " +
                "ralentit au fil de la journée, et la fermer proprement suffit à la corriger.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Sessions ouvertes", loaded.Value.ToString(), DataSource.Registry, "1"))));
        }

        /// <summary>
        /// Des profils dont personne ne s'est servi depuis longtemps.
        /// </summary>
        /// <remarks>
        /// Le constat ne propose pas de les supprimer : un profil contient les documents de
        /// quelqu'un, et ce quelqu'un n'est pas toujours celui qui apporte la machine. Il indique
        /// où regarder quand le disque est plein, et c'est tout ce qu'un logiciel a le droit de
        /// faire ici.
        /// </remarks>
        private static RuleResult Unused(RuleContext c)
        {
            var profiles = c.System.Profiles.Profiles;
            if (profiles.Count == 0)
                return RuleResult.NotEvaluated("Aucun profil utilisateur n'a été relevé.");

            var reference = c.Snapshot.Metadata.CreatedAt;
            var stale = new List<string>();
            var evaluated = false;

            foreach (var profile in profiles)
            {
                if (profile.State != ProfileState.Normal || profile.IsCurrent || profile.Loaded) continue;
                if (!profile.LastUsed.IsReliable) continue;

                evaluated = true;

                var months = (reference - profile.LastUsed.Value).TotalDays / 30.44;
                if (months < c.T.UnusedProfileMonthsWarning) continue;

                stale.Add(profile.AccountName.Or(profile.Sid) + " (" +
                          Fmt.Date(profile.LastUsed.Value) + ")");
            }

            if (!evaluated)
                return RuleResult.NotEvaluated(
                    "Aucun profil inactif ne porte de date de dernière utilisation exploitable.");

            if (stale.Count == 0) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                "Des profils ne servent plus depuis longtemps",
                stale.Count + " profil(s) inutilisé(s) depuis plus de " + c.T.UnusedProfileMonthsWarning +
                " mois : " + string.Join(", ", stale) + ".",
                "Ces comptes n'ont pas ouvert de session depuis longtemps et leurs dossiers occupent toujours " +
                "le disque. C'est la première piste quand l'espace manque sur une machine partagée ou " +
                "d'entreprise. Rien n'est proposé automatiquement : un profil contient les documents de " +
                "quelqu'un, et sa suppression se décide avec le client, pas avec un logiciel.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Profils inutilisés", stale.Count.ToString(), DataSource.Registry,
                        c.T.UnusedProfileMonthsWarning + " mois"))));
        }
    }
}
