Imports System.Globalization
Imports Avalonia.Controls
Imports Avalonia.Controls.Documents
Imports Avalonia.Media
Imports FerrumPix.Services

Namespace Controls

    ''' <summary>Schrift fuer selbst gezeichneten Text (FormattedText) in Steuerelementen, die ihre
    ''' Beschriftung in <c>Render</c> zeichnen.
    '''
    ''' WARUM NICHT EINFACH <c>New Typeface(FontFamily.Default)</c>: "$Default" ist nur ein
    ''' Platzhalter; er wird auf die Standardfamilie des Systems abgebildet, unter Linux auf den
    ''' Familiennamen der Skia-Standardschrift. Fuehrt die Schriftenliste des Systems genau diesen
    ''' Namen nicht (auf schmalen Systemen und in AppImage-Umgebungen ohne vollstaendige
    ''' Fontconfig-Einrichtung der Fall), wirft bereits der Zugriff auf den GlyphTypeface. Das
    ''' passiert MITTEN im Zeichenlauf, also ausserhalb jedes Try/Catch der Oberflaeche: der Prozess
    ''' endet dort kommentarlos, ohne Fenstermeldung.
    '''
    ''' Deshalb hier: eine Familie suchen, die nachweislich eine Schrift liefert, das Ergebnis
    ''' merken, und wenn wirklich keine zu bekommen ist, KEINE melden. Dann bleibt die Beschriftung
    ''' weg und alles andere wird weiter gezeichnet, statt die Anwendung zu beenden.</summary>
    Friend Module UiTypeface

        ''' Einmal gepruefte Schrift. Die Familie stammt vom ersten Fragenden; da sie im Regelfall
        ''' vom Fenster geerbt wird, ist sie fuer alle Steuerelemente dieselbe.
        Private _cached As Typeface?

        ''' Ein Schriftproblem betrifft jeden Frame; ohne diese Sperre schriebe es das
        ''' Fehlerprotokoll in Sekunden voll.
        Private _reported As Boolean

        ''' <summary>Erzeugt den Textblock zum Zeichnen. Gibt Nothing zurueck, wenn keine Schrift zu
        ''' bekommen ist oder die Formatierung fehlschlaegt; der Aufrufer laesst den Text dann weg.</summary>
        Friend Function TryFormat(owner As Control, text As String, culture As CultureInfo,
                                  fontSize As Double, brush As IBrush) As FormattedText
            If String.IsNullOrEmpty(text) Then Return Nothing

            Dim typeface As Typeface = Nothing
            If Not TryResolve(owner, typeface) Then Return Nothing

            Try
                Dim formatted = New FormattedText(text, culture, FlowDirection.LeftToRight, typeface, fontSize, brush)
                ' Der Zugriff auf die Breite erzwingt die Formatierung JETZT. Ohne ihn liefe sie erst
                ' beim Zeichnen an und ein Fehler faende dieses Try nicht mehr vor. Genau dort
                ' scheitert auch die Ersatzsuche fuer Zeichen, die die gewaehlte Schrift nicht hat.
                Dim unusedWidth = formatted.Width
                Return formatted
            Catch ex As Exception
                ReportOnce(ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Ermittelt eine Schrift, deren Glyphen sich wirklich laden lassen.</summary>
        Private Function TryResolve(owner As Control, ByRef typeface As Typeface) As Boolean
            If _cached.HasValue Then
                typeface = _cached.Value
                Return True
            End If

            For Each family In Candidates(owner)
                If family Is Nothing Then Continue For
                ' Der Platzhalter fuehrt zurueck auf genau die Standardfamilie, um die es hier geht.
                If String.Equals(family.Name, FontFamily.Default.Name, StringComparison.Ordinal) Then Continue For
                Dim candidate = New Typeface(family)
                If Not CanRender(candidate) Then Continue For
                _cached = candidate
                typeface = candidate
                Return True
            Next

            ReportOnce(New InvalidOperationException(
                "Keine verwendbare Schrift gefunden: weder die geerbte Familie noch die Standardfamilie " &
                "des Systems noch eine der installierten Schriften liefert Glyphen. Beschriftungen in " &
                "selbst gezeichneten Steuerelementen bleiben leer."))
            Return False
        End Function

        ''' <summary>Die Kandidaten in der Reihenfolge, in der sie geprueft werden: die vom Fenster
        ''' geerbte Familie zuerst (sie traegt bereits die uebrige Oberflaeche), dann die
        ''' Standardfamilie des Systems, zuletzt die installierten Schriften der Reihe nach.</summary>
        Private Iterator Function Candidates(owner As Control) As IEnumerable(Of FontFamily)
            If owner IsNot Nothing Then Yield TextElement.GetFontFamily(owner)

            Dim manager As FontManager = Nothing
            Try
                manager = FontManager.Current
            Catch
                ' Selbst der Zugriff auf den Schriftenverwalter wirft, wenn das System keine
                ' Standardfamilie nennt. Dann gibt es hier schlicht nichts mehr zu holen.
            End Try
            If manager Is Nothing Then Return

            Yield manager.DefaultFontFamily

            Dim installed As IReadOnlyList(Of FontFamily) = Nothing
            Try
                installed = manager.SystemFonts
            Catch
            End Try
            If installed Is Nothing Then Return
            For i = 0 To installed.Count - 1
                Yield installed(i)
            Next
        End Function

        Private Function CanRender(candidate As Typeface) As Boolean
            Try
                Dim glyphs As GlyphTypeface = Nothing
                Return FontManager.Current.TryGetGlyphTypeface(candidate, glyphs)
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Haelt das erste Schriftproblem im Fehlerprotokoll fest. Weitere bleiben stumm:
        ''' die Ursache ist dieselbe und der Zeichenlauf wiederholt sie mit jedem Frame.</summary>
        Private Sub ReportOnce(ex As Exception)
            If _reported Then Return
            _reported = True
            DiagnosticLogService.LogException("UiTypeface", ex)
        End Sub

    End Module

End Namespace
