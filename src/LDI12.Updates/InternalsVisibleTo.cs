using System.Runtime.CompilerServices;

// Trois choses de ce projet se vérifient sans réseau et méritent de l'être : l'adresse
// exactement telle qu'elle part (c'est ainsi qu'on prouve que rien d'identifiant ne quitte la
// machine), l'analyse d'un manifeste à partir de ses octets signés, et la réduction d'un nom de
// fichier annoncé au nom qu'il prétend être. Les exposer publiquement pour cela reviendrait à
// élargir le contrat du projet pour la commodité des tests.
[assembly: InternalsVisibleTo("LDI12.Tests")]
