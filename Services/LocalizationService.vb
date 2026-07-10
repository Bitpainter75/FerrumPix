Imports System
Imports System.Globalization
Imports System.Resources
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.RegularExpressions
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
        ''' Eigene Ressourcendatei nur für die Such-Tags der Formen/Symbole-Icons (Resources/IconTags*.resx),
        ''' getrennt von den allgemeinen UI-Texten, damit sich beide unabhängig voneinander pflegen lassen.
        Private Shared ReadOnly IconTags As New ResourceManager("FerrumPix.IconTags", GetType(LocalizationService).Assembly)

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
                    Case Else : Return "English"
                End Select
            End Get
        End Property

        Public Shared Function NormalizeLanguageMode(value As String) As String
            Select Case If(value, "").Trim()
                Case "German", "English", "Spanish", "French", "Italian"
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
        ''' über die dedizierte IconTags-Ressource. Fällt wie T() auf den Ausgangstext zurück, wenn
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
                Case "English" : Return ""
                Case Else
                    Dim systemCode = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant()
                    Select Case systemCode
                        Case "de", "es", "fr", "it"
                            Return systemCode
                        Case Else
                            Return ""
                    End Select
            End Select
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

        Private Shared Sub ApplyOne(node As ILogical)
            Dim textBlock = TryCast(node, TextBlock)
            If textBlock IsNot Nothing AndAlso Not String.IsNullOrEmpty(textBlock.Text) Then
                textBlock.Text = T(textBlock.Text)
            End If

            Dim content = TryCast(node, ContentControl)
            If content IsNot Nothing AndAlso TypeOf content.Content Is String Then
                content.Content = T(CStr(content.Content))
            End If

            Dim menuItem = TryCast(node, MenuItem)
            If menuItem IsNot Nothing AndAlso TypeOf menuItem.Header Is String Then
                menuItem.Header = T(CStr(menuItem.Header))
            End If

            Dim textBox = TryCast(node, TextBox)
            If textBox IsNot Nothing AndAlso Not String.IsNullOrEmpty(textBox.PlaceholderText) Then
                textBox.PlaceholderText = T(textBox.PlaceholderText)
            End If

            Dim control = TryCast(node, Control)
            If control IsNot Nothing Then
                Dim tip = ToolTip.GetTip(control)
                If TypeOf tip Is String Then ToolTip.SetTip(control, T(CStr(tip)))
            End If
        End Sub
    End Class

End Namespace
