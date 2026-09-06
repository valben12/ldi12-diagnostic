using System.Runtime.CompilerServices;

// L'interprétation de la sortie des outils système (quel code de sortie de DISM vaut réussite,
// quelle phrase de SFC signifie « rien à réparer ») est la partie la plus facile à se tromper
// et la plus coûteuse à corriger tard. Elle mérite d'être testée directement, sans avoir à
// lancer sfc.exe : ces méthodes restent donc internes à l'assembly, mais visibles des tests.
// L'alternative aurait été de les rendre publiques, c'est-à-dire d'élargir l'API du projet pour
// une raison qui ne concerne que ses tests.
[assembly: InternalsVisibleTo("LDI12.Tests")]
