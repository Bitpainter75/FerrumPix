Imports Avalonia
Imports Avalonia.Input
Imports Avalonia.Controls
Imports Avalonia.VisualTree
Imports System.Linq
Imports System.Runtime.CompilerServices

Namespace Services

    ''' <summary>
    ''' Maps shortcuts by intent instead of replacing every Control modifier
    ''' with Command. Standard commands and range selection use Command on macOS,
    ''' while FerrumPix-specific bindings keep Control so reserved shortcuts such
    ''' as Command+Q and Command+W remain available to the operating system.
    ''' </summary>
    Public NotInheritable Class PlatformShortcutService

        Private Sub New()
        End Sub

        Public Shared ReadOnly Property IsMacOS As Boolean = OperatingSystem.IsMacOS()
        Private Shared ReadOnly ShortcutLabelStates As New ConditionalWeakTable(Of TextBlock, PresentationTextState)()
        Private Shared ReadOnly ToolTipStates As New ConditionalWeakTable(Of Control, PresentationTextState)()

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

        Public Shared Function FormatPrimaryShortcut(keyText As String,
                                                     Optional includeShift As Boolean = False) As String
            If Not IsMacOS Then
                Return "Strg+" & If(includeShift, "Umschalt+", "") & keyText
            End If
            Return If(includeShift, "⇧⌘", "⌘") & keyText
        End Function

        Public Shared Function FormatShortcutInLabel(text As String, macShortcut As String) As String
            If Not IsMacOS OrElse String.IsNullOrEmpty(text) Then Return text
            Dim opening = text.LastIndexOf("("c)
            If opening < 0 Then Return text & " (" & macShortcut & ")"
            Return text.Substring(0, opening) & "(" & macShortcut & ")"
        End Function

        ''' <summary>
        ''' Restores the translated text that existed before macOS shortcut glyphs
        ''' were applied. Localization can then safely recognise its own last value
        ''' and switch languages repeatedly.
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
        ''' Applies macOS shortcut glyphs after localization while retaining the
        ''' unformatted value for the next localization pass.
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

        Public Shared Function FormatMacToolTip(text As String) As String
            If Not IsMacOS OrElse String.IsNullOrEmpty(text) Then Return text

            text = ReplaceShortcutToken(text, "Strg+Pfeil links", "⌘R")
            text = ReplaceShortcutToken(text, "Ctrl+Left", "⌘R")
            text = ReplaceShortcutToken(text, "Strg+Pfeil rechts", "⌥⌘R")
            text = ReplaceShortcutToken(text, "Ctrl+Right", "⌥⌘R")
            text = ReplaceShortcutToken(text, "Strg+Umschalt+G", "⇧⌘G")
            text = ReplaceShortcutToken(text, "Ctrl+Shift+G", "⇧⌘G")
            text = ReplaceShortcutToken(text, "Strg+Enter", "⌃Enter")
            text = ReplaceShortcutToken(text, "Ctrl+Enter", "⌃Enter")

            For Each key In {"A", "C", "D", "F", "G", "N", "P", "S", "V", "X", "Z"}
                text = ReplaceShortcutToken(text, "Strg+" & key, "⌘" & key)
                text = ReplaceShortcutToken(text, "Ctrl+" & key, "⌘" & key)
            Next

            Return text
        End Function

        Private Shared Function ReplaceLeadingModifier(text As String, symbol As String) As String
            Dim alreadyFormatted = text.Length > 0 AndAlso "⌘⌃⌥⇧".Contains(text(0))
            Dim separator = If(alreadyFormatted, -1, text.IndexOf("+"c))
            Dim keyText = If(separator >= 0, text.Substring(separator + 1), TrimLeadingModifierGlyphs(text))
            Return symbol & keyText
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

        Private Shared Function ReplaceShortcutToken(text As String,
                                                     oldValue As String,
                                                     newValue As String) As String
            Return text.Replace(oldValue, newValue, StringComparison.Ordinal)
        End Function

        Private NotInheritable Class PresentationTextState
            Public Unformatted As String
            Public Formatted As String
        End Class

    End Class

End Namespace
