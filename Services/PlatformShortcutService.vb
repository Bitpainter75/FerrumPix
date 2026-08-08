Imports Avalonia
Imports Avalonia.Input
Imports Avalonia.Controls
Imports Avalonia.VisualTree
Imports System.Collections.Generic
Imports System.Linq
Imports System.Runtime.CompilerServices

Namespace Services

    ''' <summary>
    ''' Bildet Tastenkuerzel nach ihrer ABSICHT ab, statt stumpf jedes Strg durch Command zu
    ''' ersetzen. Die uebliche Handvoll Befehle und die Bereichsauswahl nehmen auf macOS Command;
    ''' die eigenen Kuerzel von FerrumPix behalten Strg, damit belegte Kombinationen wie Command+Q
    ''' und Command+W dem Betriebssystem erhalten bleiben.
    ''' </summary>
    Public NotInheritable Class PlatformShortcutService

        Private Sub New()
        End Sub

        Public Shared ReadOnly Property IsMacOS As Boolean = OperatingSystem.IsMacOS()
        Private Shared ReadOnly ShortcutLabelStates As New ConditionalWeakTable(Of TextBlock, PresentationTextState)()
        Private Shared ReadOnly ToolTipStates As New ConditionalWeakTable(Of Control, PresentationTextState)()

        ''' Austauschliste der Kurzinfo-Kuerzel samt der Sprache, fuer die sie gebaut wurde.
        Private Shared _replacements As List(Of KeyValuePair(Of String, String))
        Private Shared _replacementCulture As String

        Public Shared Function HasPrimaryModifier(modifiers As KeyModifiers) As Boolean
            Dim modifier = If(IsMacOS, KeyModifiers.Meta, KeyModifiers.Control)
            Return modifiers.HasFlag(modifier)
        End Function

        Public Shared Function HasSelectionModifier(modifiers As KeyModifiers) As Boolean
            Return HasPrimaryModifier(modifiers)
        End Function

        Public Shared Function HasApplicationModifier(modifiers As KeyModifiers) As Boolean
            Return modifiers.HasFlag(KeyModifiers.Control)
        End Function

        Public Shared Function IsRedoShortcut(key As Key, modifiers As KeyModifiers) As Boolean
            If Not HasPrimaryModifier(modifiers) Then Return False
            If IsMacOS Then
                Return key = Key.Z AndAlso modifiers.HasFlag(KeyModifiers.Shift)
            End If
            Return key = Key.Y AndAlso Not modifiers.HasFlag(KeyModifiers.Shift)
        End Function

        Public Shared Function IsUndoShortcut(key As Key, modifiers As KeyModifiers) As Boolean
            Return key = Key.Z AndAlso HasPrimaryModifier(modifiers) AndAlso
                   Not modifiers.HasFlag(KeyModifiers.Shift)
        End Function

        Public Shared Function IsMacFullscreenShortcut(key As Key, modifiers As KeyModifiers) As Boolean
            Return IsMacOS AndAlso key = Key.F AndAlso
                   modifiers.HasFlag(KeyModifiers.Control) AndAlso
                   modifiers.HasFlag(KeyModifiers.Meta) AndAlso
                   Not modifiers.HasFlag(KeyModifiers.Alt) AndAlso
                   Not modifiers.HasFlag(KeyModifiers.Shift)
        End Function

        ''' <summary>
        ''' Liefert die macOS-Schreibweise eines Kuerzels. Auf allen anderen Plattformen
        ''' bleibt der uebersetzte Text unveraendert, weil FormatShortcutInLabel dort gar
        ''' nicht erst umschreibt. Hier steht deshalb bewusst KEINE eigene Ersatzschreibung
        ''' fuer Windows und Linux: sie waere eine feste Sprache im Quelltext und liefe an
        ''' den Ressourcendateien vorbei.
        ''' </summary>
        Public Shared Function FormatPrimaryShortcut(keyText As String,
                                                     Optional includeShift As Boolean = False) As String
            Return If(includeShift, "⇧⌘", "⌘") & keyText
        End Function

        Public Shared Function FormatShortcutInLabel(text As String, macShortcut As String) As String
            If Not IsMacOS OrElse String.IsNullOrEmpty(text) Then Return text
            Dim opening = text.LastIndexOf("("c)
            If opening < 0 Then Return text & " (" & macShortcut & ")"
            Return text.Substring(0, opening) & "(" & macShortcut & ")"
        End Function

        ''' <summary>
        ''' Stellt den uebersetzten Text wieder her, wie er vor dem Einsetzen der macOS-Zeichen fuer
        ''' Tastenkuerzel aussah. Die Lokalisierung erkennt danach ihren eigenen letzten Wert wieder
        ''' und kann die Sprache beliebig oft wechseln.
        ''' </summary>
        Public Shared Sub RestoreMacPresentation(root As Visual)
            If Not IsMacOS OrElse root Is Nothing Then Return

            For Each label In root.GetVisualDescendants().OfType(Of TextBlock)()
                Dim kind = TryCast(label.Tag, String)
                If String.IsNullOrEmpty(kind) Then Continue For

                Dim state As PresentationTextState = Nothing
                If ShortcutLabelStates.TryGetValue(label, state) AndAlso
                   state.Unformatted IsNot Nothing AndAlso
                   String.Equals(label.Text, state.Formatted, StringComparison.Ordinal) Then
                    label.Text = state.Unformatted
                End If
            Next

            For Each control In root.GetVisualDescendants().OfType(Of Control)()
                Dim state As PresentationTextState = Nothing
                If Not ToolTipStates.TryGetValue(control, state) OrElse
                   state.Unformatted Is Nothing Then Continue For

                Dim tip = TryCast(ToolTip.GetTip(control), String)
                If String.Equals(tip, state.Formatted, StringComparison.Ordinal) Then
                    ToolTip.SetTip(control, state.Unformatted)
                End If
            Next
        End Sub

        ''' <summary>
        ''' Setzt die macOS-Zeichen fuer Tastenkuerzel nach der Uebersetzung ein und behaelt den
        ''' unformatierten Wert fuer den naechsten Uebersetzungsdurchlauf.
        ''' </summary>
        Public Shared Sub ApplyMacPresentation(root As Visual)
            If Not IsMacOS OrElse root Is Nothing Then Return

            For Each label In root.GetVisualDescendants().OfType(Of TextBlock)()
                Dim kind = TryCast(label.Tag, String)
                If String.IsNullOrEmpty(kind) OrElse String.IsNullOrEmpty(label.Text) Then Continue For

                Dim state = ShortcutLabelStates.GetValue(label, Function(ignored) New PresentationTextState())
                state.Unformatted = label.Text
                state.Formatted = FormatMacShortcutLabel(label.Text, kind)
                label.Text = state.Formatted
            Next

            For Each control In root.GetVisualDescendants().OfType(Of Control)()
                Dim tip = TryCast(ToolTip.GetTip(control), String)
                If String.IsNullOrEmpty(tip) Then Continue For

                Dim state = ToolTipStates.GetValue(control, Function(ignored) New PresentationTextState())
                state.Unformatted = tip
                state.Formatted = FormatMacToolTip(tip)
                ToolTip.SetTip(control, state.Formatted)
            Next
        End Sub

        Public Shared Function FormatMacShortcutLabel(text As String, kind As String) As String
            If Not IsMacOS OrElse String.IsNullOrEmpty(text) OrElse String.IsNullOrEmpty(kind) Then Return text

            Select Case kind
                Case "Primary"
                    Return ReplaceLeadingModifier(text, "⌘")
                Case "PrimaryShift"
                    Return ReplaceThroughLastModifier(text, "⇧⌘")
                Case "Selection"
                    Return ReplaceLeadingModifier(text, "⌘")
                Case "Application"
                    Return ReplaceLeadingModifier(text, "⌃")
                Case "MacFullscreen"
                    Return "⌃⌘F / F11"
                Case "MacFavorite"
                    Return ". / ⌃Q"
                Case "MacRotateLeft"
                    Return "⌘R"
                Case "MacRotateRight"
                    Return "⌥⌘R"
                Case "MacRedo"
                    Return "⇧⌘Z"
                Case "MacSave"
                    Return "⌘S / ⇧⌘S"
                Case "MacZoomIn"
                    Return "⌘+ / +"
                Case "MacZoomOut"
                    Return "⌘− / −"
                Case "MacOption"
                    If text.StartsWith("Option (⌥)", StringComparison.Ordinal) Then Return text
                    Dim suffix = text.IndexOf(" "c)
                    Return "Option (⌥)" & If(suffix >= 0, text.Substring(suffix), "")
                Case "MacTextNewline"
                    Return ReplaceLeadingModifier(text, "⌃")
            End Select

            Return text
        End Function

        ''' <summary>
        ''' Schreibt die Kuerzel in einer Kurzinfo auf die macOS-Zeichen um. Die Suchmuster
        ''' stammen aus den Ressourcendateien und nicht aus fest verdrahteten Zeichenketten:
        ''' die Tastennamen sind uebersetzt ("Pfeil links", "Flèche gauche", "Flecha
        ''' izquierda", "Freccia sinistra", "Seta esquerda"), eine feste Liste traefe nur
        ''' Deutsch und Englisch.
        ''' </summary>
        Public Shared Function FormatMacToolTip(text As String) As String
            If Not IsMacOS OrElse String.IsNullOrEmpty(text) Then Return text

            For Each ersetzung In ShortcutReplacements()
                text = text.Replace(ersetzung.Key, ersetzung.Value, StringComparison.Ordinal)
            Next

            Return text
        End Function

        ''' <summary>
        ''' Baut die Austauschliste fuer die aktuell eingestellte Sprache und behaelt sie,
        ''' bis umgeschaltet wird. Laeuft nur auf dem Oberflaechen-Thread, daher ohne Sperre.
        '''
        ''' Sortiert wird nach Laenge ABSTEIGEND, und das ist der eigentliche Punkt: das kurze
        ''' "Ctrl+F" steckt als Anfang in "Ctrl+Flèche gauche" und "Ctrl+Flecha izquierda",
        ''' "Ctrl+S" in "Ctrl+Seta esquerda". Ungeordnet ersetzt die kurze Marke zuerst und
        ''' setzt ein FALSCHES Kuerzel ein - schlimmer als gar keine Umschrift.
        ''' </summary>
        Private Shared Function ShortcutReplacements() As List(Of KeyValuePair(Of String, String))
            Dim culture = LocalizationService.EffectiveCulture.Name
            If _replacements IsNot Nothing AndAlso
               String.Equals(_replacementCulture, culture, StringComparison.Ordinal) Then
                Return _replacements
            End If

            Dim map As New Dictionary(Of String, String)(StringComparer.Ordinal)
            ' Die Suchmuster sind der WORTLAUT aus der Oberflaeche und muessen sich mit ihm
            ' aendern - seit die Tastennamen in Grossbuchstaben stehen, also STRG und SHIFT.
            AddShortcut(map, "STRG+PFEIL LINKS", "⌘R")
            AddShortcut(map, "STRG+PFEIL RECHTS", "⌥⌘R")
            AddShortcut(map, "STRG+SHIFT+G", "⇧⌘G")
            AddShortcut(map, "STRG+ENTER", "⌃ENTER")
            For Each key In {"A", "C", "D", "F", "G", "N", "P", "S", "V", "X", "Z"}
                AddShortcut(map, "STRG+" & key, "⌘" & key)
            Next

            Dim items = map.ToList()
            items.Sort(Function(a, b) b.Key.Length.CompareTo(a.Key.Length))
            _replacements = items
            _replacementCulture = culture
            Return items
        End Function

        ''' <summary>
        ''' Nimmt den deutschen Ausgangstext UND seine Uebersetzung auf. Der Ausgangstext
        ''' bleibt noetig, weil eine noch nicht uebersetzte Kurzinfo im deutschen Wortlaut
        ''' in der Oberflaeche steht.
        ''' </summary>
        Private Shared Sub AddShortcut(map As Dictionary(Of String, String),
                                       germanSource As String,
                                       macShortcut As String)
            map(germanSource) = macShortcut
            Dim translated = LocalizationService.T(germanSource)
            If Not String.IsNullOrEmpty(translated) Then map(translated) = macShortcut
        End Sub

        ''' <summary>
        ''' Ersetzt die Modifikatortaste durch ihr macOS-Zeichen. Welches Wort das ist,
        ''' steht im Text selbst (alles vor dem ersten Pluszeichen), deshalb funktioniert
        ''' das in jeder Sprache ohne eine Liste im Quelltext.
        '''
        ''' Ersetzt werden ALLE Nennungen, nicht nur die erste: eine Zeile kann zwei
        ''' Kuerzel tragen ("Strg+1 bis Strg+5"), und dann bliebe sonst die zweite Haelfte
        ''' in der Schreibweise der anderen Plattform stehen.
        ''' </summary>
        Private Shared Function ReplaceLeadingModifier(text As String, symbol As String) As String
            If text.Length = 0 Then Return text
            If "⌘⌃⌥⇧".Contains(text(0)) Then Return symbol & TrimLeadingModifierGlyphs(text)

            Dim separator = text.IndexOf("+"c)
            If separator < 0 Then Return symbol & text
            Return text.Replace(text.Substring(0, separator + 1), symbol, StringComparison.Ordinal)
        End Function

        Private Shared Function ReplaceThroughLastModifier(text As String, symbols As String) As String
            Dim alreadyFormatted = text.Length > 0 AndAlso "⌘⌃⌥⇧".Contains(text(0))
            Dim separator = If(alreadyFormatted, -1, text.LastIndexOf("+"c))
            Dim keyText = If(separator >= 0, text.Substring(separator + 1), TrimLeadingModifierGlyphs(text))
            Return symbols & keyText
        End Function

        Private Shared Function TrimLeadingModifierGlyphs(text As String) As String
            Dim offset = 0
            While offset < text.Length AndAlso "⌘⌃⌥⇧".Contains(text(offset))
                offset += 1
            End While
            Return text.Substring(offset)
        End Function

        Private NotInheritable Class PresentationTextState
            Public Unformatted As String
            Public Formatted As String
        End Class

    End Class

End Namespace
