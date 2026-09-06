using System.Runtime.CompilerServices;

// L'assainissement d'un nom de machine décide d'un nom de fichier : un diagnostic perdu parce
// que le poste du client s'appelle « PC de Léa » serait absurde, et cela se teste directement
// plutôt qu'en archivant huit machines de référence.
[assembly: InternalsVisibleTo("LDI12.Tests")]
