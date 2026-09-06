using System.Runtime.CompilerServices;

// La traduction d'un échec réseau en phrase française est le cœur de ce projet : « la connexion
// sécurisée n'a pas pu être établie, sur Windows 7 la cause habituelle est l'absence de la mise
// à jour TLS 1.2 » évite au technicien d'aller vérifier la box du client pendant une heure. Cela
// se teste directement, sur des exceptions fabriquées, plutôt qu'en débranchant un réseau.
[assembly: InternalsVisibleTo("LDI12.Tests")]
