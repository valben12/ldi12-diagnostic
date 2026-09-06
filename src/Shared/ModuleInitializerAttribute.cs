using System;

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Marque une méthode exécutée par le runtime avant tout le reste du module.
    /// </summary>
    /// <remarks>
    /// Même catégorie que <c>IsExternalInit</c> : c'est un support de langage et non une
    /// bibliothèque. C# 9 sait produire un initialiseur de module, mais l'attribut qui le déclare
    /// n'entre dans le framework qu'avec .NET 5 ; sur net462 il suffit de le déclarer soi-même,
    /// le compilateur ne cherche que le nom complet.
    /// <para>
    /// Il n'y a pas de crochet plus précoce dans une application WPF. Le <c>Main</c> d'une
    /// application WPF est engendré par le compilateur de balisage, et la première ligne qu'on y
    /// écrirait s'exécuterait déjà trop tard : le chargement de l'assembly qui contient
    /// <c>App</c> entraîne celui de ses dépendances. Un résolveur installé ici, en revanche, est
    /// en place avant que la première d'entre elles soit demandée.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute
    {
    }
}
