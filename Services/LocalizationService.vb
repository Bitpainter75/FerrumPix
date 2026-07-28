Imports System
Imports System.Globalization
Imports System.Resources
Imports System.Runtime.CompilerServices
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.RegularExpressions
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Controls.Primitives
Imports Avalonia.Input
Imports Avalonia.LogicalTree

Namespace Services

    Public NotInheritable Class LocalizationService
        Private Sub New()
        End Sub

        Public Shared Event LanguageChanged As EventHandler

        Private Shared _languageMode As String = "System"
        Private Shared ReadOnly Strings As New ResourceManager("FerrumPix.Strings", GetType(LocalizationService).Assembly)
        Private Shared ReadOnly AppliedValues As New ConditionalWeakTable(Of ILogical, LocalizedNodeState)()
        ''' Eigene Ressourcendatei nur für die Such-Tags der Formen/Symbole-Icons (Resources/IconTags*.resx),
        ''' getrennt von den allgemeinen UI-Texten, damit sich beide unabhängig voneinander pflegen lassen.
        Private Shared ReadOnly IconTags As New ResourceManager("FerrumPix.IconTags", GetType(LocalizationService).Assembly)

        ''' Markiert UI-Bereiche, deren Text absichtlich unverändert bleiben soll, z.B. die
        ''' Eigennamen der Sprachen in der Sprachauswahl.
        Public Shared ReadOnly KeepOriginalProperty As AttachedProperty(Of Boolean) =
            AvaloniaProperty.RegisterAttached(Of LocalizationService, Control, Boolean)("KeepOriginal")

        Public Shared Function GetKeepOriginal(target As Control) As Boolean
            Return target.GetValue(KeepOriginalProperty)
        End Function

        Public Shared Sub SetKeepOriginal(target As Control, value As Boolean)
            target.SetValue(KeepOriginalProperty, value)
        End Sub

        Public Shared Property LanguageMode As String
            Get
                Return _languageMode
            End Get
            Set(value As String)
                Dim normalized = NormalizeLanguageMode(value)
                If _languageMode = normalized Then Return
                _languageMode = normalized
                RaiseEvent LanguageChanged(Nothing, EventArgs.Empty)
            End Set
        End Property

        Public Shared ReadOnly Property EffectiveCulture As CultureInfo
            Get
                Dim code = ResolveCultureCode(_languageMode)
                If String.IsNullOrEmpty(code) Then Return CultureInfo.InvariantCulture
                Return CultureInfo.GetCultureInfo(code)
            End Get
        End Property

        Public Shared ReadOnly Property EffectiveLanguage As String
            Get
                Select Case EffectiveCulture.TwoLetterISOLanguageName
                    Case "de" : Return "German"
                    Case "es" : Return "Spanish"
                    Case "fr" : Return "French"
                    Case "it" : Return "Italian"
                    Case "pt" : Return "Portuguese"
                    Case "zh" : Return "Chinese"
                    Case Else : Return "English"
                End Select
            End Get
        End Property

        Public Shared Function NormalizeLanguageMode(value As String) As String
            Select Case If(value, "").Trim()
                Case "German", "English", "Spanish", "French", "Italian", "Portuguese", "Chinese"
                    Return value.Trim()
                Case Else
                    Return "System"
            End Select
        End Function

        Public Shared Function T(text As String) As String
            If String.IsNullOrEmpty(text) Then Return text

            Try
                Dim key = MakeKey(text)
                Dim translated = Strings.GetString(key, EffectiveCulture)
                If Not String.IsNullOrEmpty(translated) Then Return translated

                translated = Strings.GetString(key, CultureInfo.InvariantCulture)
                Return If(String.IsNullOrEmpty(translated), text, translated)
            Catch ex As MissingManifestResourceException
                Return text
            End Try
        End Function

        ''' Übersetzt einen Icon-Such-Tag (z.B. den aus dem SVG-Dateinamen abgeleiteten Namen "Stern")
        ''' über die dedizierte IconTags-Ressource. Fällt wie T auf den Ausgangstext zurück, wenn
        ''' für den Tag noch keine Übersetzung gepflegt wurde.
        Public Shared Function Tag(text As String) As String
            If String.IsNullOrEmpty(text) Then Return text

            Try
                Dim key = MakeTagKey(text)
                Dim translated = IconTags.GetString(key, EffectiveCulture)
                If Not String.IsNullOrEmpty(translated) Then Return translated

                translated = IconTags.GetString(key, CultureInfo.InvariantCulture)
                Return If(String.IsNullOrEmpty(translated), text, translated)
            Catch ex As MissingManifestResourceException
                Return text
            End Try
        End Function

        Private Shared Function MakeTagKey(text As String) As String
            Dim baseName = Regex.Replace(text, "[^A-Za-z0-9]+", "_").Trim("_"c)
            If baseName.Length = 0 Then baseName = "Tag"
            Return baseName.Substring(0, Math.Min(baseName.Length, 64))
        End Function

        Public Shared Sub ApplyTo(root As ILogical)
            If root Is Nothing Then Return
            Dim control = TryCast(root, Control)
            If control IsNot Nothing AndAlso GetKeepOriginal(control) Then Return
            ApplyOne(root)
            For Each child In root.GetLogicalChildren()
                ApplyTo(child)
            Next
        End Sub

        Private Shared Function ResolveCultureCode(mode As String) As String
            Select Case NormalizeLanguageMode(mode)
                Case "German" : Return "de"
                Case "Spanish" : Return "es"
                Case "French" : Return "fr"
                Case "Italian" : Return "it"
                Case "Portuguese" : Return "pt"
                Case "Chinese" : Return "zh-CN"
                Case "English" : Return ""
                Case Else
                    Return ResolveSystemCultureCode()
            End Select
        End Function

        Private Shared Function ResolveSystemCultureCode() As String
            Dim systemCode = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant()
            If IsChineseCultureCode(systemCode) Then Return "zh-CN"
            If IsSupportedCultureCode(systemCode) Then Return systemCode

            For Each variableName In {"LANGUAGE", "LC_MESSAGES", "LANG"}
                Dim code = ExtractCultureCode(Environment.GetEnvironmentVariable(variableName))
                If IsChineseCultureCode(code) Then Return "zh-CN"
                If IsSupportedCultureCode(code) Then Return code
            Next

            Return ""
        End Function

        Private Shared Function ExtractCultureCode(value As String) As String
            value = If(value, "").Trim()
            If String.IsNullOrEmpty(value) Then Return ""

            Dim first = value.Split(":"c)(0).Trim()
            If String.Equals(first, "C", StringComparison.OrdinalIgnoreCase) OrElse
               String.Equals(first, "POSIX", StringComparison.OrdinalIgnoreCase) Then Return ""

            Dim normalized = first.Split("."c)(0).Split("@"c)(0).Replace("-"c, "_"c)
            Dim separator = normalized.IndexOf("_"c)
            If separator >= 0 Then normalized = normalized.Substring(0, separator)
            Return normalized.ToLowerInvariant()
        End Function

        Private Shared Function IsSupportedCultureCode(code As String) As Boolean
            Select Case If(code, "").ToLowerInvariant()
                Case "de", "es", "fr", "it", "pt"
                    Return True
                Case Else
                    Return False
            End Select
        End Function

        Private Shared Function IsChineseCultureCode(code As String) As Boolean
            Return String.Equals(If(code, ""), "zh", StringComparison.OrdinalIgnoreCase)
        End Function

        Private Shared Function MakeKey(text As String) As String
            Dim baseName = Regex.Replace(text, "[^A-Za-z0-9]+", "_").Trim("_"c)
            If baseName.Length = 0 Then baseName = "Text"
            If baseName.Length > 48 Then baseName = baseName.Substring(0, 48)

            Using sha = SHA1.Create()
                Dim hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text))
                Dim hash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8).ToLowerInvariant()
                Return $"{baseName}_{hash}"
            End Using
        End Function

        ''' <summary>Ein Text mit dieser Klasse wird NICHT uebersetzt. Gedacht fuer Anzeigen, deren
        ''' Inhalt aus einer Bindung kommt: das Zuweisen von Text loescht die Bindung, und die Anzeige
        ''' steht danach fuer immer auf dem Wert, den sie beim Sprachwechsel zufaellig hatte.
        '''
        ''' Warum das lange nicht auffiel: der Baumdurchlauf ueberspringt LEERE Texte, und die
        ''' allermeisten gebundenen Anzeigen sind beim Anwenden der Sprache noch leer. Erst eine, die
        ''' schon beim Aufbau etwas anzeigt, faellt in die Falle.</summary>
        Public Const KeineUebersetzung As String = "no-translate"

        Private Shared Sub ApplyOne(node As ILogical)
            Dim state = AppliedValues.GetValue(node, Function(ignored) New LocalizedNodeState())

            Dim textBlock = TryCast(node, TextBlock)
            If textBlock IsNot Nothing AndAlso Not String.IsNullOrEmpty(textBlock.Text) AndAlso
               Not textBlock.Classes.Contains(KeineUebersetzung) Then
                textBlock.Text = TranslateTracked(textBlock.Text, state.Text)
            End If

            Dim content = TryCast(node, ContentControl)
            If content IsNot Nothing AndAlso TypeOf content.Content Is String Then
                content.Content = TranslateTracked(CStr(content.Content), state.Content)
            End If

            Dim menuItem = TryCast(node, MenuItem)
            If menuItem IsNot Nothing AndAlso TypeOf menuItem.Header Is String Then
                menuItem.Header = TranslateTracked(CStr(menuItem.Header), state.MenuHeader)
            End If

            ' Expander und TabItem erben Header nicht von MenuItem, sondern von
            ' HeaderedContentControl - ohne diesen Zweig blieben ihre Überschriften deutsch.
            Dim headered = TryCast(node, HeaderedContentControl)
            If headered IsNot Nothing AndAlso TypeOf headered.Header Is String Then
                headered.Header = TranslateTracked(CStr(headered.Header), state.Header)
            End If

            Dim textBox = TryCast(node, TextBox)
            If textBox IsNot Nothing AndAlso Not String.IsNullOrEmpty(textBox.PlaceholderText) Then
                textBox.PlaceholderText = TranslateTracked(textBox.PlaceholderText, state.Placeholder)
            End If

            ' AutoCompleteBox ist keine TextBox, hat aber denselben Platzhalter.
            Dim autoComplete = TryCast(node, AutoCompleteBox)
            If autoComplete IsNot Nothing AndAlso Not String.IsNullOrEmpty(autoComplete.PlaceholderText) Then
                autoComplete.PlaceholderText = TranslateTracked(autoComplete.PlaceholderText, state.Placeholder)
            End If

            ' ComboBox erbt ihren Platzhalter ebenfalls nicht von TextBox - ohne diesen Zweig bliebe der
            ' Text einer noch leeren Auswahlliste deutsch.
            Dim comboBox = TryCast(node, ComboBox)
            If comboBox IsNot Nothing AndAlso Not String.IsNullOrEmpty(comboBox.PlaceholderText) Then
                comboBox.PlaceholderText = TranslateTracked(comboBox.PlaceholderText, state.Placeholder)
            End If

            Dim control = TryCast(node, Control)
            If control IsNot Nothing Then
                Dim tip = ToolTip.GetTip(control)
                If TypeOf tip Is String Then
                    ToolTip.SetTip(control, TranslateTracked(CStr(tip), state.ToolTip))
                End If
            End If
        End Sub

        Private Shared Function TranslateTracked(current As String, state As LocalizedValueState) As String
            If state.Source Is Nothing OrElse
               Not String.Equals(current, state.LastApplied, StringComparison.Ordinal) Then
                state.Source = current
            End If

            Dim translated = T(state.Source)
            state.LastApplied = translated
            Return translated
        End Function

        Private NotInheritable Class LocalizedNodeState
            Public ReadOnly Text As New LocalizedValueState()
            Public ReadOnly Content As New LocalizedValueState()
            Public ReadOnly MenuHeader As New LocalizedValueState()
            Public ReadOnly Header As New LocalizedValueState()
            Public ReadOnly Placeholder As New LocalizedValueState()
            Public ReadOnly ToolTip As New LocalizedValueState()
        End Class

        Private NotInheritable Class LocalizedValueState
            Public Source As String
            Public LastApplied As String
        End Class
    End Class

End Namespace
