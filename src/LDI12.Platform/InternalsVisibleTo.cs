using System.Runtime.CompilerServices;

// La disposition mémoire des structures natives est la seule chose de cette couche qu'un test
// puisse vérifier sans le matériel correspondant : une WLAN_BSS_ENTRY mal alignée rendrait des
// fréquences et des puissances fantaisistes, sans jamais échouer bruyamment.
[assembly: InternalsVisibleTo("LDI12.Tests")]
