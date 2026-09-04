Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq

Namespace Services

    ''' <summary>Übersetzt die stabilen englischen Modellbegriffe für die Oberfläche und bildet die
    ''' Gegenrichtung für Suche/Filter. Die Modell- und Datenbanksprache bleibt immer Englisch;
    ''' deshalb bleibt ein Bild beim Sprachwechsel ohne neue Analyse auffindbar.
    '''
    ''' Das Wörterbuch ist absichtlich ein eigener Dienst statt UI-Textressourcen: ein Begriff wie
    ''' "dog" ist Dateninhalt, nicht der Text eines Knopfs. Neue Sprachpakete können hier ergänzt
    ''' werden, ohne die Modellresultate oder XMP-Dateien umzuschreiben.</summary>
    Public NotInheritable Class AiTagLocalizationService

        Private Sub New()
        End Sub

        Private Shared ReadOnly German As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"person", "Person"}, {"people", "Menschen"}, {"man", "Mann"}, {"woman", "Frau"},
            {"child", "Kind"}, {"baby", "Baby"}, {"dog", "Hund"}, {"cat", "Katze"}, {"bird", "Vogel"},
            {"horse", "Pferd"}, {"cow", "Kuh"}, {"sheep", "Schaf"}, {"elephant", "Elefant"},
            {"car", "Auto"}, {"truck", "Lastwagen"}, {"bus", "Bus"}, {"train", "Zug"},
            {"bicycle", "Fahrrad"}, {"motorcycle", "Motorrad"}, {"airplane", "Flugzeug"},
            {"boat", "Boot"}, {"road", "Straße"}, {"bridge", "Brücke"}, {"building", "Gebäude"},
            {"house", "Haus"}, {"city", "Stadt"}, {"village", "Dorf"}, {"street", "Straße"},
            {"tree", "Baum"}, {"forest", "Wald"}, {"grass", "Gras"}, {"flower", "Blume"},
            {"plant", "Pflanze"}, {"mountain", "Berg"}, {"beach", "Strand"}, {"sea", "Meer"},
            {"ocean", "Ozean"}, {"lake", "See"}, {"river", "Fluss"}, {"waterfall", "Wasserfall"},
            {"sky", "Himmel"}, {"cloud", "Wolke"}, {"sun", "Sonne"}, {"sunset", "Sonnenuntergang"},
            {"sunrise", "Sonnenaufgang"}, {"snow", "Schnee"}, {"rain", "Regen"}, {"fog", "Nebel"},
            {"landscape", "Landschaft"}, {"portrait", "Porträt"}, {"selfie", "Selfie"},
            {"food", "Essen"}, {"restaurant", "Restaurant"}, {"table", "Tisch"}, {"chair", "Stuhl"},
            {"book", "Buch"}, {"computer", "Computer"}, {"phone", "Telefon"}, {"camera", "Kamera"},
            {"window", "Fenster"}, {"door", "Tür"}, {"wall", "Wand"}, {"floor", "Boden"},
            {"indoor", "Innenraum"}, {"outdoor", "Draußen"}, {"night", "Nacht"}, {"day", "Tag"},
            {"sport", "Sport"}, {"football", "Fußball"}, {"tennis", "Tennis"}, {"swimming", "Schwimmen"},
            {"wedding", "Hochzeit"}, {"birthday", "Geburtstag"}, {"festival", "Festival"},
            {"art", "Kunst"}, {"painting", "Gemälde"}, {"animal", "Tier"}, {"wildlife", "Wildtier"},
            {"flowerpot", "Blumentopf"}, {"garden", "Garten"}, {"park", "Park"}, {"church", "Kirche"},
            {"castle", "Schloss"}, {"tower", "Turm"}, {"bed", "Bett"},
            {"kitchen", "Küche"}, {"living room", "Wohnzimmer"}, {"office", "Büro"}, {"shop", "Geschäft"}}

        Public Shared Function Display(canonical As String) As String
            Dim key = If(canonical, "").Trim()
            If key.Length = 0 Then Return ""
            If LocalizationService.EffectiveCulture.TwoLetterISOLanguageName = "de" Then
                Dim translated As String = Nothing
                If German.TryGetValue(key, translated) Then Return translated
            End If
            ' Englisch ist der kanonische, portable Rückfall. Er zeigt einen echten Modellbegriff
            ' statt einer leeren Fläche, solange ein Sprachpaket einen seltenen Ausdruck noch nicht
            ' enthält.
            Return key
        End Function

        ''' <summary>Alle kanonischen Begriffe, die ein Suchtext meinen kann. Für unbekannte Wörter
        ''' wird der Text selbst versucht - das trifft englische Modellbegriffe ohne Sonderweg.</summary>
        Public Shared Function CanonicalsForQuery(text As String) As IReadOnlyList(Of String)
            Dim query = If(text, "").Trim()
            If query.Length = 0 Then Return Array.Empty(Of String)()
            Dim result As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {query}
            If LocalizationService.EffectiveCulture.TwoLetterISOLanguageName = "de" Then
                For Each item In German
                    If String.Equals(item.Value, query, StringComparison.CurrentCultureIgnoreCase) Then result.Add(item.Key)
                Next
            End If
            Return result.ToList()
        End Function

    End Class

End Namespace
