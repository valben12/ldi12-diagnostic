using System.Runtime.CompilerServices;

// Certaines sondes portent un décodage qu'il vaut mieux tester directement que par une machine
// entière : l'état des produits du Centre de sécurité, dont l'encodage n'est pas documenté par
// Microsoft, et le calcul de gigue, qui doit refuser de conclure sur une seule mesure. Les rendre
// publics élargirait l'API du projet pour une raison qui ne concerne que ses tests.
[assembly: InternalsVisibleTo("LDI12.Tests")]
