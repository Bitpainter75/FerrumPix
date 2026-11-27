Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Text.RegularExpressions

Namespace Services

    ''' <summary>Ein Suchbegriff samt Vorzeichen: 1 heisst "muss vorkommen" (<c>+Wort</c>), -1
    ''' "darf nicht vorkommen" (<c>-Wort</c>), 0 ein freies Wort ohne Zeichen.</summary>
    Friend Structure SearchTerm
        Public Text As String
        Public Sign As Integer
    End Structure

    ''' <summary>Der zerlegte Suchtext: eine Gruppe, oder mehrere, wenn ein ausdrueckliches
    ''' OR/ODER darin steht.</summary>
    Friend NotInheritable Class ParsedSearchText
        Public ReadOnly Groups As New List(Of List(Of SearchTerm))()
        Public HasExplicitOr As Boolean

        Public ReadOnly Property IsEmpty As Boolean
            Get
                Return Groups.All(Function(group) group.Count = 0)
            End Get
        End Property
    End Class

    ''' <summary>
    ''' Die EINE Auswertung des freien Suchtexts, fuer jeden Suchweg: Suchfeld der Galerie (Umfang
    ''' Ordner und Katalog), gespeicherte Suchen und Suchdialog, Immich und Nextcloud nach der
    ''' Vorauswahl am Server, die mobile Seite im Browser. Gegen welchen Text verglichen wird, steht
    ''' in <see cref="SearchVocabulary"/>; hier nur, was der Suchtext bedeutet.
    '''
    ''' <para>Aus GalleryViewModel hierher umgezogen, unveraendert bis auf die Sichtbarkeit, damit
    ''' die Seite im Browser denselben Weg nimmt statt einer eigenen Zerlegung.</para>
    ''' </summary>
    Friend NotInheritable Class SearchTextQuery

        Private Sub New()
        End Sub

        Private Shared ReadOnly ExplicitOrPattern As New Regex("\s+(?:OR|ODER)\s+", RegexOptions.IgnoreCase)

        ''' <summary>So viele Zeichen eines Suchtexts zaehlen; der Rest faellt weg. Ein Suchfeld
        ''' erreicht das nie, eine Anfrage an die Seite im Browser schon: der Text geht je Bild durch
        ''' jeden Begriff.</summary>
        Friend Const MaxSearchTextLength As Integer = 1000

        ''' <summary>Grenzen eines Platzhalterbegriffs. Laenger oder mit mehr Sternen gilt er als
        ''' gewoehnliches Wort (Enthaltensein) statt als Muster. Der Vergleich selbst ist auch
        ''' innerhalb der Grenzen hoechstens Musterlaenge mal Namenslaenge (WildcardMatches).</summary>
        Friend Const MaxWildcardLength As Integer = 200
        Friend Const MaxWildcardStars As Integer = 16

        ''' <summary>Zerlegt den freien Suchtext. Er gilt gegen Dateiname, Stichwoerter, KI-Begriffe
        ''' und Katalogtexte, in dieser Reihenfolge der Regeln:
        '''
        ''' - <c>+Wort</c> muss vorkommen, <c>-Wort</c> darf nicht vorkommen. So sucht man Bilder mit
        '''   ALLEN Stichwoertern: <c>+Urlaub +Strand -Regen</c>.
        ''' - Freie Woerter ohne Zeichen sind eine ODER-Liste: "Meer Strand" findet Meer ODER Strand.
        '''   Stehen sie neben Woertern mit Zeichen, muss zusaetzlich mindestens eines treffen.
        ''' - Ein ausdrueckliches OR/ODER trennt Gruppen, von denen eine zutreffen muss; innerhalb einer
        '''   Gruppe muessen dann ALLE freien Woerter treffen.
        '''
        ''' Ein Ausdruck in Anfuehrungszeichen bleibt zusammen, auch mit Zeichen davor
        ''' (<c>+"New York"</c>). Ein einzelnes + oder - ist kein Zeichen, sondern Text, und ein
        ''' Bindestrich mitten im Wort ebenso. Platzhalter (* und ?) gelten je Wort und nur fuer den
        ''' Dateinamen, auch mit Zeichen (<c>-*.png</c>).</summary>
        Friend Shared Function ParseSearchText(textQuery As String) As ParsedSearchText
            Dim result As New ParsedSearchText()
            Dim text = Bounded(textQuery).Trim()
            result.HasExplicitOr = ExplicitOrPattern.IsMatch(text)
            If result.HasExplicitOr Then
                For Each group In ExplicitOrPattern.Split(text)
                    result.Groups.Add(SplitSearchTerms(group))
                Next
            Else
                result.Groups.Add(SplitSearchTerms(text))
            End If
            Return result
        End Function

        ''' <summary>Zerlegt eine Eingabe in ihre Suchbegriffe. Ein Ausdruck in Anfuehrungszeichen
        ''' bleibt zusammen, sonst trennen Leerzeichen. EINE Stelle fuer alle Aufrufer: mit einer
        ''' eigenen Fassung je Stelle zerfaellt derselbe Text irgendwann verschieden, und dann findet
        ''' die Vorauswahl der Dateien etwas anderes als der Vergleich je Bild.</summary>
        Friend Shared Function SplitSearchTerms(textQuery As String) As List(Of SearchTerm)
            Dim terms As New List(Of SearchTerm)()
            For Each match As Match In Regex.Matches(Bounded(textQuery).Trim(), "[+-]?""[^""]*""|\S+")
                Dim value = match.Value
                Dim sign = 0
                If value.Length > 1 AndAlso (value(0) = "+"c OrElse value(0) = "-"c) Then
                    sign = If(value(0) = "+"c, 1, -1)
                    value = value.Substring(1)
                End If
                value = value.Trim(""""c)
                If String.IsNullOrWhiteSpace(value) Then Continue For
                terms.Add(New SearchTerm With {.Text = value, .Sign = sign})
            Next
            Return terms
        End Function

        Friend Shared Function IsWildcardTerm(term As String) As Boolean
            Return term.IndexOf("*"c) >= 0 OrElse term.IndexOf("?"c) >= 0
        End Function

        Friend Shared Function MatchesParsedSearchText(query As ParsedSearchText, fileName As String, haystack As String) As Boolean
            If query.IsEmpty Then Return True
            Dim freeTermsAsAny = Not query.HasExplicitOr
            Return query.Groups.Any(Function(group) MatchesSearchGroup(group, fileName, haystack, freeTermsAsAny))
        End Function

        ''' <summary>Eine Gruppe trifft, wenn jedes Plus-Wort vorkommt, kein Minus-Wort, und von den
        ''' freien Woertern eines (<paramref name="freeTermsAsAny"/>) oder alle.</summary>
        Private Shared Function MatchesSearchGroup(group As List(Of SearchTerm), fileName As String, haystack As String,
                                                   freeTermsAsAny As Boolean) As Boolean
            Dim hasFree = False
            Dim freeHit = False
            For Each term In group
                Dim hit = MatchesSingleSearchTerm(term.Text, fileName, haystack)
                If term.Sign > 0 Then
                    If Not hit Then Return False
                ElseIf term.Sign < 0 Then
                    If hit Then Return False
                Else
                    hasFree = True
                    If hit Then
                        freeHit = True
                    ElseIf Not freeTermsAsAny Then
                        Return False
                    End If
                End If
            Next
            Return Not hasFree OrElse freeHit OrElse Not freeTermsAsAny
        End Function

        Private Shared Function MatchesSingleSearchTerm(term As String, fileName As String, haystack As String) As Boolean
            If IsWildcardTerm(term) AndAlso IsBoundedWildcard(term) Then Return WildcardMatches(term, If(fileName, ""))
            Return If(haystack, "").Contains(term, StringComparison.OrdinalIgnoreCase)
        End Function

        Private Shared Function Bounded(text As String) As String
            Dim value = If(text, "")
            Return If(value.Length > MaxSearchTextLength, value.Substring(0, MaxSearchTextLength), value)
        End Function

        ''' <summary>Innerhalb der Grenzen fuer ein Muster (Laenge, Zahl der Sterne)?</summary>
        Friend Shared Function IsBoundedWildcard(term As String) As Boolean
            If term.Length > MaxWildcardLength Then Return False
            Dim stars = 0
            For Each character In term
                If character = "*"c Then stars += 1
            Next
            Return stars <= MaxWildcardStars
        End Function

        ''' <summary>Passt der ganze Name auf das Muster? * steht fuer beliebig viele Zeichen, ? fuer
        ''' genau eines, Gross- und Kleinschreibung zaehlen nicht. Ohne regulaeren Ausdruck: der
        ''' Vergleich geht den Namen einmal entlang und springt bei einem Fehlschlag nur an den
        ''' letzten Stern zurueck, das kostet hoechstens Musterlaenge mal Namenslaenge. Ein Muster
        ''' wie *a*a*a*b brachte den frueheren Weg ueber Regex ohne Zeitgrenze an langen Namen zum
        ''' Stehen.</summary>
        Friend Shared Function WildcardMatches(pattern As String, text As String) As Boolean
            Dim p = If(pattern, "").ToLowerInvariant()
            Dim t = If(text, "").ToLowerInvariant()
            Dim pi = 0
            Dim ti = 0
            Dim starAt = -1
            Dim resumeAt = 0
            While ti < t.Length
                If pi < p.Length AndAlso p(pi) = "*"c Then
                    starAt = pi
                    resumeAt = ti
                    pi += 1
                ElseIf pi < p.Length AndAlso (p(pi) = "?"c OrElse p(pi) = t(ti)) Then
                    pi += 1
                    ti += 1
                ElseIf starAt >= 0 Then
                    pi = starAt + 1
                    resumeAt += 1
                    ti = resumeAt
                Else
                    Return False
                End If
            End While
            While pi < p.Length AndAlso p(pi) = "*"c
                pi += 1
            End While
            Return pi = p.Length
        End Function

    End Class

End Namespace
