using System;
using System.Collections.Generic;
using System.Diagnostics;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;

namespace LDI12.Actions.Backup
{
    /// <summary>
    /// L'avancement d'une copie, et le temps qu'il lui reste.
    /// </summary>
    /// <remarks>
    /// <b>La barre est comptée en octets, pas en fichiers.</b> Un dossier Vidéos de soixante
    /// gigaoctets tient en soixante-dix fichiers, un profil de navigateur de trente mégaoctets en
    /// quatre cents : compter les fichiers ferait bondir la barre sur le second et la figer sur le
    /// premier. Chaque fichier pèse deux fois sa taille, une pour l'écriture, une pour la relecture.
    /// <para>
    /// <b>Le temps restant, lui, tient compte des deux.</b> Une copie paie un prix par fichier
    /// (ouvrir, créer, fermer, relire) et un prix par octet. La première version mesurait un débit
    /// en octets sur les trente dernières secondes : sur l'essai réel d'un profil Chrome, elle a
    /// annoncé trente et une minutes quand il en restait moins de deux, parce que des milliers de
    /// petits fichiers faisaient chuter le débit juste avant les gros. Comparés sur les relevés de
    /// cet essai, le débit global se trompait de 51 % en médiane, et l'estimation des deux prix,
    /// retenue ici, de 37 %.
    /// </para>
    /// <para>
    /// Les deux prix s'estiment par moindres carrés sur un relevé par seconde depuis le début. Tant
    /// que la copie n'a pas encore montré assez de variété pour les séparer (un prix sortirait
    /// négatif), c'est le débit global en octets qui répond. Rien n'est annoncé avant cinq secondes.
    /// </para>
    /// </remarks>
    internal sealed class CopyProgress
    {
        private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan SamplePeriod = TimeSpan.FromSeconds(1);

        private readonly IProgress<ActionProgress>? _sink;
        private readonly Func<TimeSpan> _clock;
        private readonly long _totalUnits;
        private readonly long _totalBytes;
        private readonly int _totalFiles;
        private readonly string _verb;

        private long _done;
        private long _fileUnits;
        private int _files;
        private string _folder = string.Empty;
        private TimeSpan _lastReport = TimeSpan.MinValue;
        private TimeSpan _lastSample = TimeSpan.MinValue;

        // Sommes des moindres carrés de t ≈ a·fichiers + c·unités, sans constante.
        private double _sff, _suu, _sfu, _stf, _stu;

        public CopyProgress(IProgress<ActionProgress>? sink, long totalBytes, int totalFiles, string verb, Func<TimeSpan>? clock = null)
        {
            _sink = sink;
            _totalBytes = Math.Max(0, totalBytes);
            _totalFiles = totalFiles;
            _verb = verb;

            // Une unité de plus par fichier : un fichier vide doit aussi faire avancer la barre.
            _totalUnits = _totalBytes * 2 + totalFiles;

            if (clock == null)
            {
                var stopwatch = Stopwatch.StartNew();
                _clock = () => stopwatch.Elapsed;
            }
            else
            {
                _clock = clock;
            }
        }

        public double Fraction => _totalUnits == 0 ? 1 : Math.Min(1, (double)_done / _totalUnits);

        public void Folder(string label)
        {
            _folder = label;
            Report(force: true);
        }

        /// <summary>Octets écrits ou relus, remontés par la passerelle pendant la copie d'un fichier.</summary>
        public void Bytes(long count)
        {
            if (count <= 0) return;
            _fileUnits += count;
            _done += count;
            Report(force: false);
        }

        /// <summary>
        /// Un fichier traité, quelle qu'en soit l'issue.
        /// </summary>
        /// <remarks>
        /// Un fichier déjà présent ou refusé n'a rien écrit : sa part est ajoutée d'un coup, pour que
        /// la barre arrive à cent pour cent quand tout a été vu, et non quand tout a été copié.
        /// </remarks>
        public void FileDone(long size)
        {
            var expected = Math.Max(0, size) * 2 + 1;
            if (_fileUnits < expected) _done += expected - _fileUnits;
            _fileUnits = 0;
            _files++;
            Report(force: _files == _totalFiles);
        }

        /// <summary>Des fichiers écartés sans être tentés : leur part est comptée comme vue.</summary>
        public void Skip(IEnumerable<FileEntry> files)
        {
            foreach (var file in files)
            {
                _done += Math.Max(0, file.SizeBytes) * 2 + 1;
                _files++;
            }

            Report(force: true);
        }

        /// <summary>Temps restant estimé, ou nul s'il ne peut pas encore s'estimer.</summary>
        public TimeSpan? Remaining()
        {
            var now = _clock();
            if (now < Warmup || _done <= 0) return null;

            var filesLeft = Math.Max(0, _totalFiles - _files);
            var unitsLeft = Math.Max(0, _totalUnits - _done);
            if (unitsLeft == 0) return TimeSpan.Zero;

            var det = _sff * _suu - _sfu * _sfu;
            if (det > 1e-9 * _sff * _suu)
            {
                var perFile = (_stf * _suu - _stu * _sfu) / det;
                var perUnit = (_stu * _sff - _stf * _sfu) / det;
                if (perFile >= 0 && perUnit >= 0)
                    return TimeSpan.FromSeconds(perFile * filesLeft + perUnit * unitsLeft);
            }

            return TimeSpan.FromSeconds(unitsLeft / (_done / now.TotalSeconds));
        }

        private void Report(bool force)
        {
            var now = _clock();
            Sample(now);

            if (_sink == null) return;
            if (!force && now - _lastReport < Throttle) return;
            _lastReport = now;

            var written = Math.Min(_totalBytes, _done / 2);
            _sink.Report(new ActionProgress(_verb + " « " + _folder + " »", Fraction)
            {
                Detail = ValueFormat.Bytes(written) + " sur " + ValueFormat.Bytes(_totalBytes) + " · " +
                         _files + " fichier(s) sur " + _totalFiles,
                Remaining = Remaining(),
                Elapsed = now,
            });
        }

        private void Sample(TimeSpan now)
        {
            if (_lastSample != TimeSpan.MinValue && now - _lastSample < SamplePeriod) return;
            _lastSample = now;

            double t = now.TotalSeconds, f = _files, u = _done;
            _sff += f * f;
            _suu += u * u;
            _sfu += f * u;
            _stf += t * f;
            _stu += t * u;
        }
    }
}
